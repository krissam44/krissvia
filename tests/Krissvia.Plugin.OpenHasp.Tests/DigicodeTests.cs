using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>La page 4 : ce qu'elle affiche, et le code qui ne voyage que dans l'ordre d'armement.</summary>
public class DigicodeTests
{
    private static readonly DateTime Jeudi = new(2026, 10, 22, 10, 0, 0);

    private static Dictionary<string, string> Attributs(EtatClavier? clavier, params EntityStateUpdate[] etats) =>
        Ecran.Attributs(Donnees.Salon(), Donnees.Reglages(), Donnees.Etats(etats), Jeudi, clavier).ToDictionary(a => a.Attribut, a => a.Valeur);

    [Fact]
    public void Le_decompte_d_entree_s_affiche_avec_le_capteur_et_desarmer_ressort()
    {
        var a = Attributs(null, Donnees.EtatAlarme("entry_delay", 12, "Porte d'entrée"));

        Assert.Equal("Entrée : 12 s", a["p4b1.text"]);
        Assert.Equal("Porte d'entrée", a["p4b2.text"]);
        Assert.Equal("#43A047", a["p4b31.bg_color"]);
        Assert.Equal("#3A3A3C", a["p4b30.bg_color"]);
    }

    [Fact]
    public void Desarmee_c_est_armer_qui_ressort_et_un_defaut_se_dit()
    {
        var a = Attributs(null, Donnees.EtatAlarme("disarmed", defauts: "Fenêtre chambre"));

        Assert.Equal("Désarmée", a["p4b1.text"]);
        Assert.Equal("Défaut : Fenêtre chambre", a["p4b2.text"]);
        Assert.Equal("#0A84FF", a["p4b30.bg_color"]);
    }

    [Fact]
    public void Le_message_du_clavier_passe_devant_et_le_code_reste_masque()
    {
        var a = Attributs(new EtatClavier("* * *", "Code incorrect"), Donnees.EtatAlarme("triggered", capteur: "Radar salon", sonne: true));

        Assert.Equal("ALARME - sirène", a["p4b1.text"]);
        Assert.Equal("Code incorrect", a["p4b2.text"]);
        Assert.Equal("* * *", a["p4b3.text"]);
    }

    [Fact]
    public void Une_alarme_indisponible_le_dit()
    {
        Assert.Equal("Alarme indisponible", Attributs(null)["p4b1.text"]);
    }

    [Theory]
    [InlineData("armed", "entry_delay", true)]
    [InlineData("entry_delay", "triggered", true)]
    [InlineData("entry_delay", "entry_delay", false)]   // le décompte qui avance n'est pas une transition
    [InlineData(null, "triggered", false)]              // premier état après un redémarrage du plugin
    [InlineData("entry_delay", "disarmed", false)]
    [InlineData("disarmed", "exit_delay", false)]
    public void Le_clavier_est_appele_a_l_entree_et_au_declenchement_seulement(string? avant, string apres, bool attendu)
    {
        Assert.Equal(attendu, Ecran.AppelleLeClavier(avant, apres));
    }

    // ── Avec le plugin ──

    private sealed class Broker : IMqttLink
    {
        public ConcurrentQueue<(string Topic, string Charge)> Publications { get; } = new();

        public Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public Task<bool> SubscribeAsync(string filtre, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> PublishAsync(string topic, string charge, CancellationToken ct)
        {
            Publications.Enqueue((topic, charge));
            return Task.FromResult(true);
        }
    }

    private sealed class Banc
    {
        public Broker Broker { get; } = new();
        public FakeTimeProvider Horloge { get; } = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        public OpenHaspPlugin Plugin { get; }
        public ConcurrentQueue<(string Entite, string Commande, string? Args)> Ordres { get; } = new();
        public EntityCommandResponse Reponse { get; set; } = new() { Id = "x", Outcome = CommandOutcome.Accepted };

        public Banc()
        {
            Plugin = new OpenHaspPlugin(Donnees.Reglages(), Broker, Horloge, NullLogger<OpenHaspPlugin>.Instance)
            {
                Commander = (entite, commande, args) =>
                {
                    Ordres.Enqueue((entite, commande, args?.GetRawText()));
                    return Task.FromResult(Reponse);
                },
            };
        }

        public async Task EnLigneAsync()
        {
            await Plugin.MessageAsync("hasp/appart1/LWT", "online");
            await AttendreAsync(() => Broker.Publications.Any(p => p.Topic.EndsWith("/p4b1.text", StringComparison.Ordinal)));
            Broker.Publications.Clear();
        }

        public async Task AppuyerAsync(string objet)
        {
            await Plugin.MessageAsync($"hasp/appart1/state/{objet}", """{"event":"up"}""");
            await Task.Delay(150);
        }

        public string? Dernier(string attribut) =>
            Broker.Publications.LastOrDefault(p => p.Topic == $"hasp/appart1/command/{attribut}").Charge;
    }

    private static async Task AttendreAsync(Func<bool> condition)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Le_code_tape_part_dans_l_ordre_et_jamais_vers_la_platine()
    {
        var b = new Banc();
        await b.EnLigneAsync();

        foreach (var t in new[] { "p4b11", "p4b12", "p4b13", "p4b14" }) await b.AppuyerAsync(t);
        Assert.Equal("* * * *", b.Dernier("p4b3.text"));
        await b.AppuyerAsync("p4b31");

        Assert.Equal((Donnees.Alarme, StandardCommands.Disarm, """{"code":"1234"}"""), Assert.Single(b.Ordres));
        Assert.DoesNotContain(b.Broker.Publications, p => p.Charge.Contains("1234", StringComparison.Ordinal));
        Assert.Equal(" ", b.Dernier("p4b3.text"));           // oublié aussitôt envoyé (une espace : voir le test suivant)
        Assert.Equal("Code accepté", b.Dernier("p4b2.text"));
    }

    [Fact]
    public async Task Effacer_le_dernier_chiffre_vide_vraiment_l_affichage()
    {
        // openHASP prend une commande à charge vide pour une LECTURE : « p4b3.text » vide laissait le dernier « * ».
        var b = new Banc();
        await b.EnLigneAsync();

        await b.AppuyerAsync("p4b11");
        await b.AppuyerAsync("p4b12");
        await b.AppuyerAsync("p4b21");
        Assert.Equal("*", b.Dernier("p4b3.text"));
        await b.AppuyerAsync("p4b21");

        Assert.Equal(" ", b.Dernier("p4b3.text"));
        Assert.DoesNotContain(b.Broker.Publications, p => p.Topic.EndsWith(".text", StringComparison.Ordinal) && p.Charge.Length == 0);
    }

    [Theory]
    [InlineData("p4b3.text", "", " ")]
    [InlineData("p3b42.text", "", " ")]
    [InlineData("p4b3.text", "* *", "* *")]
    [InlineData("p1b18.hidden", "", "")]
    public void Un_texte_vide_part_en_espace_et_rien_d_autre_ne_change(string attribut, string valeur, string attendu) =>
        Assert.Equal(attendu, OpenHaspPlugin.Charge(attribut, valeur));

    [Fact]
    public async Task Un_refus_du_coeur_s_affiche_tel_quel()
    {
        var b = new Banc { Reponse = new EntityCommandResponse { Id = "x", Outcome = CommandOutcome.Failed, Detail = "code incorrect" } };
        await b.EnLigneAsync();

        await b.AppuyerAsync("p4b19");
        await b.AppuyerAsync("p4b30");

        Assert.Equal(StandardCommands.Arm, Assert.Single(b.Ordres).Commande);
        Assert.Equal("Code incorrect", b.Dernier("p4b2.text"));
    }

    [Fact]
    public async Task Sans_code_aucun_ordre_ne_part()
    {
        var b = new Banc();
        await b.EnLigneAsync();

        await b.AppuyerAsync("p4b31");

        Assert.Empty(b.Ordres);   // un code vide compterait pour le blocage du clavier
        Assert.Equal("Taper le code d'abord", b.Dernier("p4b2.text"));
    }

    [Fact]
    public async Task L_entree_amene_le_clavier_et_rallume_une_platine_endormie()
    {
        var b = new Banc();
        await b.EnLigneAsync();
        await b.Plugin.MessageAsync("hasp/appart1/state/idle", "long");
        await AttendreAsync(() => b.Dernier("backlight") == """{"state":"off"}""");

        b.Plugin.EtatRecu(Donnees.EtatAlarme("armed"));
        await Task.Delay(200);
        Assert.Null(b.Dernier("page"));

        b.Plugin.EtatRecu(Donnees.EtatAlarme("entry_delay", 30, "Porte d'entrée"));
        await AttendreAsync(() => b.Dernier("page") == "4");
        Assert.Equal("""{"state":"on","brightness":200}""", b.Dernier("backlight"));

        b.Broker.Publications.Clear();
        b.Plugin.EtatRecu(Donnees.EtatAlarme("entry_delay", 29, "Porte d'entrée"));
        await AttendreAsync(() => b.Dernier("p4b1.text") == "Entrée : 29 s");
        Assert.Null(b.Dernier("page"));   // le décompte qui avance ne repasse pas la page
    }
}
