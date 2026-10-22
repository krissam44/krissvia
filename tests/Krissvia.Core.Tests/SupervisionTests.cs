using System.Net;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.Supervision;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>
/// La supervision : les familles d'alertes de DomoMud (capteur muet, pile faible, plugin arrêté, présence perdue) et les
/// nouvelles (connexion Internet, disque). On vérifie ce qui PART sur le téléphone, et ce qui ne doit pas partir.
/// </summary>
public class SupervisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly AlertSettings Reglages = new();

    private static RegisteredEntity E(string id, EntityKind kind, string? classe, string plugin = "z2m", bool active = true) => new()
    {
        EntityId = id, DeviceId = id, IntegrationId = plugin, UniqueId = id, Kind = kind, DisplayName = id, DeviceClass = classe, Enabled = active,
    };

    private static SupervisionInput Entree(DateTimeOffset now, EtatsEnMemoire etats, IReadOnlyList<RegisteredEntity> entites,
        IReadOnlyList<PluginHealth>? plugins = null, bool? presence = null, long? disque = null) =>
        new(now, entites, etats, plugins ?? [], presence, disque, TimeZoneInfo.Utc);

    // ── Règles ───────────────────────────────────────────────────────────────

    [Fact]
    public void Seuls_les_capteurs_bavards_peuvent_etre_muets_et_un_capteur_jamais_entendu_ne_conclut_rien()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.salon", 19.5, T0.AddHours(-3));
        etats.Fixer("sensor.cuisine", 20.0, T0.AddMinutes(-10));
        etats.Fixer("switch.lampe", true, T0.AddDays(-2));                 // un interrupteur ne parle qu'au changement
        etats.Fixer("sensor.tarif", "HC", T0.AddDays(-2));                 // un libellé ne parle qu'au changement
        etats.Fixer("binary_sensor.porte", false, T0.AddHours(-1), disponible: false);
        etats.Fixer("sensor.thermostat", 19.0, T0.AddDays(-1));            // entité du cœur
        etats.Fixer("sensor.cave", 12.0, T0.AddDays(-1));                  // capteur désactivé
        etats.Fixer("sensor.shelly", 900.0, T0.AddHours(-3));              // son plugin est arrêté
        var entites = new[]
        {
            E("sensor.salon", EntityKind.Sensor, DeviceClasses.Temperature),
            E("sensor.cuisine", EntityKind.Sensor, DeviceClasses.Temperature),
            E("sensor.neuve", EntityKind.Sensor, DeviceClasses.Temperature),
            E("switch.lampe", EntityKind.Switch, null),
            E("sensor.tarif", EntityKind.Sensor, DeviceClasses.Enum),
            E("binary_sensor.porte", EntityKind.BinarySensor, DeviceClasses.Opening),
            E("sensor.thermostat", EntityKind.Sensor, DeviceClasses.Temperature, "coeur.thermostats"),
            E("sensor.cave", EntityKind.Sensor, DeviceClasses.Temperature, active: false),
            E("sensor.shelly", EntityKind.Sensor, DeviceClasses.Power, "shelly"),
        };

        var items = SupervisionRules.Evaluate(Entree(T0, etats, entites, [new("z2m", "Zigbee", true), new("shelly", "Shelly", false)]), Reglages, capteursEnRepit: false)
            .Where(i => i.Category == SupervisionCategory.Sensor).ToDictionary(i => i.Name);

        Assert.Equal(["binary_sensor.porte", "sensor.cuisine", "sensor.salon"], items.Keys.Order());
        Assert.True(items["sensor.salon"].Problem);
        Assert.Equal("muet depuis 22/10 07:00", items["sensor.salon"].Detail);
        Assert.True(items["binary_sensor.porte"].Problem);   // injoignable
        Assert.False(items["sensor.cuisine"].Problem);

        Assert.DoesNotContain(SupervisionRules.Evaluate(Entree(T0, etats, entites), Reglages, capteursEnRepit: true),
            i => i.Category == SupervisionCategory.Sensor);
    }

    [Fact]
    public void Une_pile_faible_se_leve_au_seuil_et_ne_retombe_qu_au_dessus_de_la_marge()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("sensor.p15", 15, T0);
        etats.Fixer("sensor.p22", 22, T0);
        etats.Fixer("sensor.p30", 30, T0);
        etats.Fixer("binary_sensor.pile", true, T0);
        var entites = new[]
        {
            E("sensor.p15", EntityKind.Sensor, DeviceClasses.Battery), E("sensor.p22", EntityKind.Sensor, DeviceClasses.Battery),
            E("sensor.p30", EntityKind.Sensor, DeviceClasses.Battery), E("binary_sensor.pile", EntityKind.BinarySensor, DeviceClasses.Battery),
        };

        var piles = SupervisionRules.Evaluate(Entree(T0, etats, entites), Reglages, false)
            .Where(i => i.Category == SupervisionCategory.Battery).ToDictionary(i => i.Name, i => i.Problem);

        Assert.Equal(new Dictionary<string, bool> { ["sensor.p15"] = true, ["sensor.p30"] = false, ["binary_sensor.pile"] = true }, piles);
    }

    [Fact]
    public void Le_disque_et_la_presence_sont_surveilles_seulement_quand_on_sait()
    {
        var etats = new EtatsEnMemoire();
        var items = SupervisionRules.Evaluate(Entree(T0, etats, [], presence: true, disque: 500L * 1024 * 1024), Reglages, false);

        Assert.True(items.Single(i => i.Category == SupervisionCategory.Disk).Problem);
        Assert.True(items.Single(i => i.Category == SupervisionCategory.Presence).Problem);
        Assert.Empty(SupervisionRules.Evaluate(Entree(T0, etats, []), Reglages, false));   // aucun suivi, disque inconnu
    }

    // ── Alertes ──────────────────────────────────────────────────────────────

    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(
                Documents.Where(kv => kv.Key.Item1 == collection).Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(Documents.Remove((collection, id)));
    }

    private sealed class FauxNtfy : HttpMessageHandler
    {
        public List<(string Titre, string Message)> Recues { get; } = [];
        public bool Injoignable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Injoignable) throw new HttpRequestException("Connexion refusée");
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Recues.Add((json.GetProperty("title").GetString()!, json.GetProperty("message").GetString()!));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FauxNtfy Ntfy { get; } = new();
        public EtatsEnMemoire Etats { get; } = new();
        public AlertService Alertes { get; }
        public SupervisionWatch Supervision { get; }
        public List<RegisteredEntity> Entites { get; } = [];
        public List<PluginHealth> Plugins { get; } = [];

        public Banc()
        {
            var magasin = new Magasin();
            var notifications = new NotificationService(magasin, new HttpClient(Ntfy));
            notifications.SaveAsync(new NotificationSettings { Enabled = true, BaseUrl = "http://ntfy.essai", Topic = "maison" }).GetAwaiter().GetResult();
            Alertes = new AlertService(notifications, magasin, Horloge);
            Supervision = new SupervisionWatch(Alertes, notifications, Horloge);
        }

        public Task<SupervisionReport> VerifierAsync(TimeSpan apres)
        {
            Horloge.SetUtcNow(T0 + apres);
            return Supervision.CheckAsync(Entree(Horloge.GetUtcNow(), Etats, Entites, Plugins));
        }

        public IEnumerable<string> Titres => Ntfy.Recues.Select(r => r.Titre);
    }

    private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

    [Fact]
    public async Task Un_plugin_qui_redemarre_n_est_pas_signale_un_plugin_arrete_l_est_une_fois()
    {
        var b = new Banc();
        b.Plugins.Add(new("shelly", "Shelly", Connected: false));

        await b.VerifierAsync(Min(0));
        await b.VerifierAsync(Min(4));
        Assert.Empty(b.Ntfy.Recues);

        await b.VerifierAsync(Min(5));
        await b.VerifierAsync(Min(6));
        Assert.Equal(["Service arrêté : Shelly"], b.Titres);

        b.Plugins[0] = b.Plugins[0] with { Connected = true };
        await b.VerifierAsync(Min(20));
        Assert.Equal(["Service arrêté : Shelly", "Service rétabli : Shelly"], b.Titres);
        Assert.Contains("après 20 min", b.Ntfy.Recues[1].Message);
    }

    [Fact]
    public async Task Une_box_qui_republie_coupee_a_chaque_releve_compte_depuis_le_premier_constat()
    {
        var b = new Banc();
        b.Entites.Add(E("binary_sensor.internet", EntityKind.BinarySensor, DeviceClasses.Connectivity, "livebox"));

        foreach (var m in new[] { 0, 1, 2, 3, 4, 5 })
        {
            b.Etats.Fixer("binary_sensor.internet", false, T0 + Min(m));   // republiée à chaque relevé
            await b.VerifierAsync(Min(m));
        }

        Assert.Equal(["Connexion Internet coupée"], b.Titres);
        Assert.Contains("coupée depuis 22/10 10:00", b.Ntfy.Recues[0].Message);
    }

    [Fact]
    public async Task Une_coupure_que_l_alerte_n_a_pas_pu_dire_est_racontee_une_fois_au_retour()
    {
        var b = new Banc();
        b.Entites.Add(E("binary_sensor.internet", EntityKind.BinarySensor, DeviceClasses.Connectivity, "livebox"));
        b.Ntfy.Injoignable = true;   // ntfy public : injoignable pendant la coupure

        b.Etats.Fixer("binary_sensor.internet", false, T0);
        await b.VerifierAsync(Min(0));
        await b.VerifierAsync(Min(10));
        await b.VerifierAsync(Min(20));

        b.Ntfy.Injoignable = false;
        b.Etats.Fixer("binary_sensor.internet", true, T0 + Min(38));
        await b.VerifierAsync(Min(38));
        await b.VerifierAsync(Min(39));

        var (titre, message) = Assert.Single(b.Ntfy.Recues);
        Assert.Equal("Coupure Internet", titre);
        Assert.Contains("coupée de 22/10 10:00 à 22/10 10:38 (après 38 min)", message);
    }

    [Fact]
    public async Task Au_demarrage_les_capteurs_ont_dix_minutes_pour_se_manifester()
    {
        var b = new Banc();
        b.Entites.Add(E("sensor.salon", EntityKind.Sensor, DeviceClasses.Temperature));
        b.Etats.Fixer("sensor.salon", 19.0, T0.AddHours(-5));   // état restauré, ancien

        var rapport = await b.VerifierAsync(Min(1));
        Assert.True(rapport.SensorsInGrace);
        Assert.Empty(b.Ntfy.Recues);

        b.Etats.Fixer("sensor.salon", 19.2, T0 + Min(3));        // la sonde a reparlé entre-temps
        await b.VerifierAsync(Min(10));
        Assert.Empty(b.Ntfy.Recues);

        await b.VerifierAsync(Min(3 + 121));
        Assert.Equal(["Capteur muet : sensor.salon"], b.Titres);
    }

    [Fact]
    public async Task Ce_qu_on_ne_surveille_plus_est_oublie_sans_message()
    {
        var b = new Banc();
        b.Plugins.Add(new("shelly", "Shelly", Connected: false));
        await b.VerifierAsync(Min(0));
        await b.VerifierAsync(Min(5));
        Assert.True(b.Alertes.IsActive("plugin:shelly"));

        b.Plugins.Clear();   // désactivé dans la page Services
        await b.VerifierAsync(Min(6));

        Assert.False(b.Alertes.IsActive("plugin:shelly"));
        Assert.Equal(["Service arrêté : Shelly"], b.Titres);
    }
}
