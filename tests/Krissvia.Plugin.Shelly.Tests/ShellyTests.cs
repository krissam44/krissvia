using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.Shelly.Tests;

/// <summary>
/// Le plugin Shelly. Génération 1 : réponses RÉELLES d'un Shelly EM (relevées le 15/10/2026, voie 0 sur l'arrivée
/// de la maison, voie 1 sans pince). Génération 2 : réponses construites d'après la documentation de Shelly.
/// </summary>
public class ShellyTests
{
    private const string Mac = "8caab505e44c";

    private const string Gen1Shelly = """
        {"type":"SHEM","mac":"8CAAB505E44C","auth":false,"fw":"20231107-164916/v1.14.1-rc1-g0617c15","discoverable":false,"num_outputs":1,"num_meters":0,"num_emeters":2,"report_period":1}
        """;

    private static string Gen1Status(bool relais = false, bool voie0Valide = true) => """
        {"emeters":[{"power":2008.63,"reactive":-289.88,"pf":-0.99,"voltage":240.64,"is_valid":@VALIDE@,"total":4885773.0,"total_returned":0.0},
                    {"power":0.00,"reactive":0.00,"pf":0.00,"voltage":240.64,"is_valid":true,"total":0.0,"total_returned":0.0}],
         "relays":[{"ison":@RELAIS@,"has_timer":false,"overpower":false,"is_valid":true,"source":"input"}],
         "mqtt":{"connected":true}}
        """.Replace("@VALIDE@", voie0Valide ? "true" : "false").Replace("@RELAIS@", relais ? "true" : "false");

    private const string Gen2Shelly = """{"id":"shellypro1pm-aabbcc","mac":"AABBCCDDEEFF","model":"SPSW-201PE16EU","gen":2,"app":"Pro1PM","ver":"1.4.4"}""";

    private const string Gen2Status = """
        {"switch:0":{"id":0,"source":"HTTP","output":true,"apower":1520.3,"voltage":231.2,"current":6.6,"aenergy":{"total":123456.7}},
         "em1:0":{"id":0,"current":9.1,"voltage":231.4,"act_power":2050.0,"aprt_power":2105.2,"pf":0.97,"freq":50.0},
         "sys":{"mac":"AABBCCDDEEFF"}}
        """;

    private static JsonElement Json(string texte) => JsonDocument.Parse(texte).RootElement.Clone();

    private static Dictionary<string, JsonElement> Valeurs(IEnumerable<EntityState> etats) =>
        etats.ToDictionary(e => e.EntityUniqueId, e => e.Value);

    // ── Traduction ───────────────────────────────────────────────────────────

    [Fact]
    public void Un_Shelly_EM_est_identifie_par_sa_MAC_avec_ses_voies_et_son_relais()
    {
        var id = ShellyTranslator.Identify(Json(Gen1Shelly), null)!;

        Assert.Equal((Mac, 1, "SHEM", 2, 1), (id.Mac, id.Generation, id.Model, id.Emeters, id.Outputs));
        var entites = ShellyTranslator.Describe(id, "Pince maison").Entities.Select(e => e.UniqueId).ToList();
        Assert.Contains($"{Mac}:emeter0:apparent_power", entites);
        Assert.Contains($"{Mac}:emeter1:energy", entites);
        Assert.Contains($"{Mac}:relay0", entites);
    }

    [Fact]
    public void La_puissance_apparente_est_calculee_depuis_l_active_et_la_reactive()
    {
        var id = ShellyTranslator.Identify(Json(Gen1Shelly), null)!;

        var v = Valeurs(ShellyTranslator.ReadStatus(id, Json(Gen1Status())));

        Assert.Equal(2008.63, v[$"{Mac}:emeter0:power"].GetDouble());
        Assert.Equal(2029.4, v[$"{Mac}:emeter0:apparent_power"].GetDouble());   // √(2008,63² + 289,88²)
        Assert.Equal(4885.773, v[$"{Mac}:emeter0:energy"].GetDouble());
        Assert.Equal(240.64, v[$"{Mac}:emeter0:voltage"].GetDouble());
        Assert.False(v[$"{Mac}:relay0"].GetBoolean());
    }

    [Fact]
    public void Une_voie_invalide_ne_publie_rien_pas_meme_zero()
    {
        var id = ShellyTranslator.Identify(Json(Gen1Shelly), null)!;

        var v = Valeurs(ShellyTranslator.ReadStatus(id, Json(Gen1Status(voie0Valide: false))));

        Assert.DoesNotContain(v.Keys, k => k.StartsWith($"{Mac}:emeter0:", StringComparison.Ordinal));
        Assert.Contains($"{Mac}:emeter1:power", v.Keys);
    }

    [Theory]
    [InlineData(StandardCommands.TurnOn, "/relay/0?turn=on")]
    [InlineData(StandardCommands.TurnOff, "/relay/0?turn=off")]
    [InlineData(StandardCommands.Toggle, "/relay/0?turn=toggle")]
    public void Un_ordre_de_generation_1_vise_relay(string commande, string chemin)
    {
        var id = ShellyTranslator.Identify(Json(Gen1Shelly), null)!;

        Assert.Equal(chemin, ShellyTranslator.CommandPath(id, $"{Mac}:relay0", commande));
        Assert.Null(ShellyTranslator.CommandPath(id, $"{Mac}:relay1", commande));
        Assert.Null(ShellyTranslator.CommandPath(id, $"{Mac}:emeter0:power", commande));
    }

    [Fact]
    public void Un_Shelly_de_generation_2_donne_sa_puissance_apparente_mesuree_et_se_pilote_en_rpc()
    {
        var id = ShellyTranslator.Identify(Json(Gen2Shelly), Json(Gen2Status))!;

        Assert.Equal(2, id.Generation);
        Assert.Equal(["switch:0"], id.Gen2Switches);
        Assert.Equal(["em1:0"], id.Gen2Em1);
        var v = Valeurs(ShellyTranslator.ReadStatus(id, Json(Gen2Status)));
        Assert.Equal(2105.2, v["aabbccddeeff:emeter0:apparent_power"].GetDouble());
        Assert.True(v["aabbccddeeff:relay0"].GetBoolean());
        Assert.Equal("/rpc/Switch.Set?id=0&on=false", ShellyTranslator.CommandPath(id, "aabbccddeeff:relay0", StandardCommands.TurnOff));
        Assert.Equal("/rpc/Shelly.GetStatus", ShellyTranslator.StatusPath(id));
    }

    // ── Plugin ───────────────────────────────────────────────────────────────

    /// <summary>Le réseau simulé : une réponse par chemin, ou rien (appareil injoignable).</summary>
    private sealed class FauxReseau : IShellyHttp
    {
        public bool Joignable { get; set; } = true;
        public bool RelaisAllume { get; set; }
        public ConcurrentQueue<string> Appels { get; } = new();

        public Task<JsonElement?> GetAsync(string host, string chemin, CancellationToken ct)
        {
            Appels.Enqueue(chemin);
            if (!Joignable) return Task.FromResult<JsonElement?>(null);

            if (chemin.StartsWith("/relay/0?turn=", StringComparison.Ordinal))
                RelaisAllume = chemin.EndsWith("on", StringComparison.Ordinal) || (chemin.EndsWith("toggle", StringComparison.Ordinal) && !RelaisAllume);

            return Task.FromResult<JsonElement?>(chemin switch
            {
                "/shelly" => Json(Gen1Shelly),
                "/status" => Json(Gen1Status(RelaisAllume)),
                _ when chemin.StartsWith("/relay/", StringComparison.Ordinal) => Json($$"""{"ison":{{(RelaisAllume ? "true" : "false")}}}"""),
                _ => null,
            });
        }
    }

    private static (ShellyPlugin Plugin, FauxReseau Reseau, ConcurrentQueue<Message> Envoyes) Monter()
    {
        var reseau = new FauxReseau();
        var envoyes = new ConcurrentQueue<Message>();
        var plugin = new ShellyPlugin([new ShellyDeviceConfig { Host = "192.168.1.203", Name = "Pince maison" }], reseau,
            new FakeTimeProvider(), NullLogger<ShellyPlugin>.Instance)
        {
            Sortie = m => { envoyes.Enqueue(m); return Task.CompletedTask; },
        };
        return (plugin, reseau, envoyes);
    }

    [Fact]
    public async Task Le_premier_cycle_declare_l_appareil_puis_publie_ses_mesures()
    {
        var (plugin, _, envoyes) = Monter();

        await plugin.CycleAsync(CancellationToken.None);

        var declaration = Assert.IsType<Declare>(envoyes.First());
        Assert.Equal("Pince maison", Assert.Single(declaration.Devices).Name);
        Assert.Contains(envoyes.OfType<StateBatch>().SelectMany(b => b.States), s => s.EntityUniqueId == $"{Mac}:emeter0:apparent_power");
    }

    [Fact]
    public async Task Trois_lectures_ratees_rendent_l_appareil_hors_ligne_puis_la_reponse_suivante_le_rallume()
    {
        var (plugin, reseau, envoyes) = Monter();
        await plugin.CycleAsync(CancellationToken.None);
        reseau.Joignable = false;

        await plugin.CycleAsync(CancellationToken.None);
        await plugin.CycleAsync(CancellationToken.None);
        Assert.Empty(envoyes.OfType<Availability>());

        await plugin.CycleAsync(CancellationToken.None);
        var horsLigne = Assert.Single(envoyes.OfType<Availability>());
        Assert.Equal((Mac, false), (horsLigne.DeviceUniqueId, horsLigne.Online));
        Assert.Contains(plugin.Snapshot().OfType<Availability>(), a => a.DeviceUniqueId == Mac && !a.Online);

        reseau.Joignable = true;
        await plugin.CycleAsync(CancellationToken.None);
        Assert.True(envoyes.OfType<Availability>().Last().Online);
    }

    [Fact]
    public async Task Un_appareil_injoignable_au_demarrage_n_est_declare_qu_a_sa_premiere_reponse()
    {
        var (plugin, reseau, envoyes) = Monter();
        reseau.Joignable = false;

        await plugin.CycleAsync(CancellationToken.None);
        Assert.Empty(envoyes);
        Assert.Empty(Assert.IsType<Declare>(plugin.Snapshot()[0]).Devices);

        reseau.Joignable = true;
        await plugin.CycleAsync(CancellationToken.None);
        Assert.IsType<Declare>(envoyes.First());
    }

    [Fact]
    public async Task Un_ordre_publie_l_etat_resultant_avant_de_repondre()
    {
        var (plugin, reseau, envoyes) = Monter();
        await plugin.CycleAsync(CancellationToken.None);
        envoyes.Clear();

        var resultat = await plugin.ExecuteAsync(new CommandRequest { Id = "1", EntityUniqueId = $"{Mac}:relay0", Command = StandardCommands.TurnOn }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Accepted, resultat.Outcome);
        Assert.Contains("/relay/0?turn=on", reseau.Appels);
        var relais = envoyes.OfType<StateBatch>().SelectMany(b => b.States).Single(s => s.EntityUniqueId == $"{Mac}:relay0");
        Assert.True(relais.Value.GetBoolean());
    }

    [Fact]
    public async Task Un_ordre_vers_un_appareil_injoignable_ou_une_entite_inconnue_le_dit()
    {
        var (plugin, reseau, _) = Monter();
        await plugin.CycleAsync(CancellationToken.None);

        Assert.Equal(CommandOutcome.Failed, (await plugin.ExecuteAsync(
            new CommandRequest { Id = "1", EntityUniqueId = $"{Mac}:emeter0:power", Command = StandardCommands.TurnOn }, CancellationToken.None)).Outcome);

        reseau.Joignable = false;
        Assert.Equal(CommandOutcome.Unreachable, (await plugin.ExecuteAsync(
            new CommandRequest { Id = "2", EntityUniqueId = $"{Mac}:relay0", Command = StandardCommands.TurnOff }, CancellationToken.None)).Outcome);
    }
}
