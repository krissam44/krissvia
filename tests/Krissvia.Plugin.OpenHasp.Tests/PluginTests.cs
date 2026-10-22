using System.Collections.Concurrent;

using Krissvia.Contracts;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Le plugin face à un broker simulé : ce qu'il publie quand une platine se connecte, s'endort, se réveille.</summary>
public class PluginTests
{
    /// <summary>Un broker qui retient tout ce qu'on lui publie.</summary>
    private sealed class Broker : IMqttLink
    {
        public ConcurrentQueue<(string Topic, string Charge)> Publications { get; } = new();

        public Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public Task<bool> PublishAsync(string topic, string charge, CancellationToken ct)
        {
            Publications.Enqueue((topic, charge));
            return Task.FromResult(true);
        }

        public List<(string Topic, string Charge)> Vider()
        {
            var liste = new List<(string, string)>();
            while (Publications.TryDequeue(out var p)) liste.Add(p);
            return liste;
        }
    }

    private sealed class Banc
    {
        public Broker Broker { get; } = new();
        public FakeTimeProvider Horloge { get; } = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        public Reglages Reglages { get; } = Donnees.Reglages();
        public OpenHaspPlugin Plugin { get; }

        public Banc() => Plugin = new OpenHaspPlugin(Reglages, Broker, Horloge, NullLogger<OpenHaspPlugin>.Instance);

        /// <summary>Le message arrive, puis on attend que les envois qu'il a déclenchés (sur une autre tâche) soient finis.</summary>
        public async Task<List<(string Topic, string Charge)>> RecevoirAsync(string topic, string charge, int attendus = 1)
        {
            await Plugin.MessageAsync(topic, charge);
            var limite = DateTime.UtcNow.AddSeconds(5);
            var recues = new List<(string, string)>();
            while (DateTime.UtcNow < limite)
            {
                await Task.Delay(50);
                var lot = Broker.Vider();
                recues.AddRange(lot);
                if (recues.Count >= attendus && lot.Count == 0) break;
            }

            return recues;
        }
    }

    private static string Topic(string attribut) => $"hasp/appart1/command/{attribut}";

    [Fact]
    public async Task Une_platine_qui_se_connecte_est_effacee_recoit_ses_pages_puis_son_affichage()
    {
        var b = new Banc();

        var publiees = await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);

        Assert.Equal((Topic("clearpage"), "all"), publiees[0]);
        var lignes = MiseEnPage.Generer(Donnees.Salon(), b.Reglages);
        Assert.Equal(lignes, publiees.Skip(1).Take(lignes.Count).Select(p => p.Charge));
        Assert.All(publiees.Skip(1).Take(lignes.Count), p => Assert.Equal(Topic("jsonl"), p.Topic));
        Assert.Equal((Topic("page"), "1"), publiees[lignes.Count + 1]);
        Assert.Contains((Topic("p1b1.text"), "Salon"), publiees);
        Assert.Contains((Topic("p1b9.text"), "Statut: indisponible"), publiees);   // rien reçu du cœur : on le dit
        Assert.DoesNotContain(publiees, p => p.Topic == Topic("backlight"));        // personne n'a touché au curseur

        var connexion = Assert.Single(Assert.IsType<StateBatch>(b.Plugin.Snapshot()[1]).States);
        Assert.True(connexion.Value.GetBoolean());
    }

    [Fact]
    public async Task Le_online_relu_apres_une_reconnexion_ne_repeint_pas_mais_un_redemarrage_si()
    {
        var b = new Banc();
        await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);

        Assert.Empty(await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 0));

        await b.RecevoirAsync("hasp/appart1/LWT", "offline", attendus: 0);
        var repeinte = await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);
        Assert.Equal((Topic("clearpage"), "all"), repeinte[0]);
        // Tout est renvoyé, même ce qui n'a pas changé : la platine est repartie de rien.
        Assert.Contains((Topic("p1b1.text"), "Salon"), repeinte);
    }

    [Fact]
    public async Task Un_noeud_inconnu_est_ignore()
    {
        var b = new Banc();

        Assert.Empty(await b.RecevoirAsync("hasp/plaque_du_voisin/LWT", "online", attendus: 0));
    }

    [Fact]
    public async Task Rafraichir_ne_renvoie_que_ce_qui_a_change()
    {
        var b = new Banc();
        await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);

        await b.Plugin.RafraichirAsync(CancellationToken.None);
        Assert.Empty(b.Broker.Vider());

        b.Horloge.Advance(Republication.RafraichissementForce);
        await b.Plugin.RafraichirAsync(CancellationToken.None);
        Assert.Contains((Topic("p1b1.text"), "Salon"), b.Broker.Vider());
    }

    [Fact]
    public async Task La_veille_eteint_l_ecran_qui_reste_noir_puis_le_reveil_rend_la_luminosite_choisie()
    {
        var b = new Banc();
        await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);

        // Sans réglage du curseur : le réveil rallume à la luminosité par défaut, jamais « rien ».
        Assert.Contains((Topic("backlight"), """{"state":"off"}"""), await b.RecevoirAsync("hasp/appart1/state/idle", "long"));
        Assert.Contains((Topic("backlight"), """{"state":"on","brightness":200}"""), await b.RecevoirAsync("hasp/appart1/state/idle", "off"));

        Assert.Contains((Topic("backlight"), """{"state":"on","brightness":120}"""),
            await b.RecevoirAsync("hasp/appart1/state/p3b20", """{"event":"up","val":120}"""));

        await b.RecevoirAsync("hasp/appart1/state/idle", """{"idle":"short"}""");
        b.Horloge.Advance(Republication.RafraichissementForce);
        await b.Plugin.RafraichirAsync(CancellationToken.None);
        Assert.DoesNotContain(b.Broker.Vider(), p => p.Topic == Topic("backlight"));   // l'écran endormi reste noir

        var reveil = await b.RecevoirAsync("hasp/appart1/state/idle", "off");
        Assert.Contains((Topic("backlight"), """{"state":"on","brightness":120}"""), reveil);
        Assert.Contains((Topic("p3b21.text"), "47 %"), reveil);
    }

    [Fact]
    public async Task Sans_extinction_en_veille_l_ecran_n_est_jamais_coupe()
    {
        var b = new Banc();
        b.Reglages.Affichage.EteindreEnVeille = false;
        await b.RecevoirAsync("hasp/appart1/LWT", "online", attendus: 50);

        Assert.Empty(await b.RecevoirAsync("hasp/appart1/state/idle", "long", attendus: 0));
    }

    [Fact]
    public void Les_entites_suivies_sont_celles_des_reglages_sans_doublon()
    {
        var r = Donnees.Reglages();

        Assert.Equal(
            [Donnees.Thermostat, Donnees.Lampe, Donnees.Prise, "sensor.salon_temperature", "sensor.chambre_rdc_temperature",
                "sensor.chambre_etage_temperature", "sensor.sdb_temperature", Donnees.Exterieure, Donnees.Alarme],
            r.Entites());
    }
}
