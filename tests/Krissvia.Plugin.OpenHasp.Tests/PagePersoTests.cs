using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>La page 5, écrite par l'utilisateur en JSONL, et ses liaisons aux entités.</summary>
public class PagePersoTests
{
    private const string Jsonl = """
        // Ma page : une sonde, un arc, un bouton
        {"id":10,"obj":"label","x":20,"y":40,"w":440,"h":40,"text":"Salon"}

        {"page":2,"id":11,"obj":"arc","x":140,"y":100,"w":200,"h":200}
        {"id":12,"obj":"btn","toggle":true,"x":20,"y":320,"w":200,"h":60,"text":"Lampe"}
        """;

    private static Platine AvecPage(string? jsonl = Jsonl) => new()
    {
        Noeud = "appart1",
        Thermostat = Donnees.Thermostat,
        Interrupteurs = [new Interrupteur { Entite = Donnees.Prise }],
        PagePerso = jsonl,
        AffichagesPerso =
        [
            new AffichagePerso { Objet = 10, Entite = "sensor.salon_temperature", Format = "Salon {} °C" },
            new AffichagePerso { Objet = 11, Entite = "sensor.salon_temperature", Attribut = "val" },
        ],
        BoutonsPerso = [new BoutonPerso { Objet = 12, Entite = Donnees.Lampe }],
    };

    private static List<JsonElement> Objets(Platine platine, Reglages reglages) =>
        [.. MiseEnPage.Generer(platine, reglages).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    [Fact]
    public void Chaque_ligne_va_sur_la_page_5_et_les_commentaires_sont_ignores()
    {
        var lue = PagePerso.Lire(Jsonl);

        Assert.Empty(lue.Ecartees);
        Assert.Equal([10, 11, 12], lue.Lignes.Select(l => (int)l["id"]!));
        // La page annoncée (2, ici) est remplacée : posé sur la page 2, l'arc écraserait une sonde générée.
        Assert.All(lue.Lignes, l => Assert.Equal(Objets_.PagePerso, (int)l["page"]!));
    }

    [Theory]
    [InlineData("""{"id":10,"obj":"label" """, "ligne 1 : pas un objet JSON")]
    [InlineData("""[{"id":10}]""", "ligne 1 : pas un objet JSON")]
    [InlineData("""{"obj":"label"}""", "ligne 1 : « id » manquant")]
    [InlineData("""{"id":0,"obj":"label"}""", "« id » manquant ou hors de 1 à 254")]
    [InlineData("""{"id":255,"obj":"label"}""", "« id » manquant ou hors de 1 à 254")]
    [InlineData("""{"id":"10","obj":"label"}""", "« id » manquant")]
    [InlineData("""{"id":50,"obj":"label"}""", "l'id 50 est celui du fond")]
    public void Une_ligne_fausse_est_ecartee_et_dite_sans_perdre_les_autres(string fausse, string raison)
    {
        var lue = PagePerso.Lire(fausse + "\n" + """{"id":20,"obj":"label"}""");

        Assert.Contains(raison, Assert.Single(lue.Ecartees), StringComparison.Ordinal);
        Assert.Equal([20], lue.Lignes.Select(l => (int)l["id"]!));
    }

    [Fact]
    public void La_page_5_vient_apres_les_pages_generees_avec_son_fond_d_abord_et_son_bouton()
    {
        var reglages = Donnees.Reglages(AvecPage());
        var objets = Objets(AvecPage(), reglages);

        Assert.Equal([1, 2, 3, 4, 5], MiseEnPage.PagesPresentes(AvecPage(), reglages));
        var page5 = objets.Where(o => o.GetProperty("page").GetInt32() == 5).Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal([Objets_.Fond, 10, 11, 12], page5);

        var navigation = objets.Where(o => o.GetProperty("page").GetInt32() == 0).ToList();
        Assert.Equal(5, navigation.Count);
        Assert.Equal(("p5", "Perso"), (navigation[4].GetProperty("action").GetString(), navigation[4].GetProperty("text").GetString()));
        // À cinq boutons de 90 px, les noms courts en 16 : « Thermostat » en 20 ne tenait plus.
        Assert.Equal(["Thermo", "Sondes", "Heure", "Alarme", "Perso"], navigation.Select(o => o.GetProperty("text").GetString()));
        Assert.All(navigation, o => Assert.Equal(16, o.GetProperty("text_font").GetInt32()));
    }

    [Fact]
    public void Le_bouton_de_la_page_perso_prend_son_nom_et_jamais_d_icone()
    {
        var platine = new Platine { Noeud = "appart2", PagePerso = """{"id":10,"obj":"label"}""", NomPagePerso = "Volets" };

        var navigation = Objets(platine, new Reglages { Platines = [platine] }).Where(o => o.GetProperty("page").GetInt32() == 0).ToList();

        // Deux pages : les icônes reviennent devant les noms, sauf pour la page perso.
        Assert.Equal([" Horloge", "Volets"], navigation.Select(o => o.GetProperty("text").GetString()));
        Assert.All(navigation, o => Assert.Equal(20, o.GetProperty("text_font").GetInt32()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Sans_JSONL_pas_de_page_5_ni_de_liaison(string? jsonl)
    {
        var platine = AvecPage(jsonl);
        var reglages = Donnees.Reglages(platine);

        Assert.DoesNotContain(Objets_.PagePerso, MiseEnPage.PagesPresentes(platine, reglages));
        Assert.DoesNotContain(Objets(platine, reglages), o => o.GetProperty("page").GetInt32() == 5);
        Assert.DoesNotContain(Donnees.Lampe, reglages.Entites().Where(e => e == Donnees.Lampe && !platine.Interrupteurs.Any(i => i.Entite == e)));
        Assert.DoesNotContain(Ecran.Attributs(platine, reglages, Donnees.Etats(), DateTime.Now), a => a.Attribut.StartsWith("p5", StringComparison.Ordinal));
        Assert.Null(Evenements.Interpreter(platine, reglages, "p5b12", """{"event":"up"}""", Donnees.Etats()));
    }

    [Fact]
    public void Les_entites_de_la_page_perso_sont_suivies()
    {
        var entites = Donnees.Reglages(AvecPage()).Entites();

        Assert.Contains("sensor.salon_temperature", entites);
        Assert.Contains(Donnees.Lampe, entites);
        Assert.Equal(entites.Count, entites.Distinct().Count());
    }

    [Theory]
    [InlineData(21.46, "Salon {} °C", "Salon 21,5 °C")]
    [InlineData(21.0, null, "21")]
    [InlineData(21.0, "°C", "21 °C")]   // pas de {} : le format suit la valeur
    public void Une_valeur_numerique_se_lit_a_la_francaise(double valeur, string? format, string attendu) =>
        Assert.Equal(attendu, PagePerso.Valeur(new AffichagePerso { Format = format }, Donnees.Etat("sensor.t", valeur)));

    [Fact]
    public void Une_valeur_inconnue_ne_passe_jamais_pour_une_mesure()
    {
        var a = new AffichagePerso { Format = "{} °C" };

        Assert.Equal("-- °C", PagePerso.Valeur(a, null));
        Assert.Equal("-- °C", PagePerso.Valeur(a, Donnees.Etat("sensor.t", 21.5, disponible: false)));
        Assert.Equal("0", PagePerso.Valeur(new AffichagePerso { Attribut = "val" }, Donnees.Etat("sensor.t", 21.5, disponible: false)));
    }

    [Fact]
    public void Booleens_textes_champs_et_valeurs_entieres()
    {
        Assert.Equal("Allumé", PagePerso.Valeur(new AffichagePerso(), Donnees.Etat(Donnees.Prise, true, genre: EntityKind.Switch)));
        Assert.Equal("Éteint", PagePerso.Valeur(new AffichagePerso(), Donnees.Etat(Donnees.Lampe, new { on = false }, genre: EntityKind.Light)));
        Assert.Equal("Oui", PagePerso.Valeur(new AffichagePerso(), Donnees.Etat("binary_sensor.porte", true, genre: EntityKind.BinarySensor)));
        Assert.Equal("HC", PagePerso.Valeur(new AffichagePerso(), Donnees.Etat("sensor.tarif", "HC")));
        Assert.Equal("19,2", PagePerso.Valeur(new AffichagePerso { Champ = "temperature" }, Donnees.Climat()));
        Assert.Equal("--", PagePerso.Valeur(new AffichagePerso { Champ = "absent" }, Donnees.Climat()));
        Assert.Equal("22", PagePerso.Valeur(new AffichagePerso { Attribut = "val" }, Donnees.Etat("sensor.t", 21.6)));
        Assert.Equal("1", PagePerso.Valeur(new AffichagePerso { Attribut = "val" }, Donnees.Etat(Donnees.Prise, true, genre: EntityKind.Switch)));
        Assert.Equal("text", PagePerso.Attribut(new AffichagePerso { Attribut = "bg_color" }));   // rien d'autre que text ou val
    }

    [Fact]
    public void L_ecran_peint_les_objets_relies_de_la_page_5()
    {
        var platine = AvecPage();
        var etats = Donnees.Etats(Donnees.Etat("sensor.salon_temperature", 21.46), Donnees.Etat(Donnees.Lampe, new { on = true }, genre: EntityKind.Light));

        var a = Ecran.Attributs(platine, Donnees.Reglages(platine), etats, DateTime.Now).ToDictionary(x => x.Attribut, x => x.Valeur);

        Assert.Equal("Salon 21,5 °C", a["p5b10.text"]);
        Assert.Equal("21", a["p5b11.val"]);
        Assert.Equal("1", a["p5b12.val"]);   // bouton toggle enfoncé : la lampe est allumée
        Assert.Equal("0", Ecran.Attributs(platine, Donnees.Reglages(platine), Donnees.Etats(), DateTime.Now).ToDictionary(x => x.Attribut, x => x.Valeur)["p5b12.val"]);
    }

    [Theory]
    [InlineData(true, StandardCommands.TurnOff)]
    [InlineData(false, StandardCommands.TurnOn)]
    [InlineData(null, StandardCommands.TurnOn)]   // inconnu : allumer, comme les interrupteurs de la page horloge
    public void Un_bouton_relie_allume_ou_eteint_selon_l_etat_connu(bool? allume, string commande)
    {
        var platine = AvecPage();
        var etats = allume is { } a ? Donnees.Etats(Donnees.Etat(Donnees.Lampe, new { on = a }, genre: EntityKind.Light)) : Donnees.Etats();

        Assert.Equal(new Ordre(Donnees.Lampe, commande), Evenements.Interpreter(platine, Donnees.Reglages(platine), "p5b12", """{"event":"up"}""", etats));
        Assert.Null(Evenements.Interpreter(platine, Donnees.Reglages(platine), "p5b12", """{"event":"down"}""", etats));
        Assert.Null(Evenements.Interpreter(platine, Donnees.Reglages(platine), "p5b10", """{"event":"up"}""", etats));   // un affichage ne commande rien
    }

    /// <summary>Raccourci : <c>Objets</c> est aussi le nom de la méthode d'aide de cette classe.</summary>
    private static class Objets_
    {
        public const int PagePerso = OpenHasp.Objets.PagePerso;
        public const int Fond = OpenHasp.Objets.Fond;
    }
}

/// <summary>Repeindre et redémarrer une platine, depuis l'entité « Écran » et sa carte.</summary>
public class RepeindreRedemarrerTests
{
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

    private static async Task<(OpenHaspPlugin Plugin, Broker Broker, FakeTimeProvider Horloge)> EnLigneAsync(bool enLigne = true)
    {
        var broker = new Broker();
        var horloge = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));
        var plugin = new OpenHaspPlugin(Donnees.Reglages(), broker, horloge, NullLogger<OpenHaspPlugin>.Instance);
        await plugin.ConnecteAsync();
        if (enLigne)
        {
            await plugin.MessageAsync("hasp/appart1/LWT", "online");
            // La première peinture part sur une autre tâche : on attend qu'elle ait fini, puis on oublie ce qu'elle a publié.
            var limite = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limite && !broker.Publications.Any(p => p.Topic.EndsWith("/page", StringComparison.Ordinal))) await Task.Delay(20);
            await Task.Delay(100);
            broker.Publications.Clear();
        }

        return (plugin, broker, horloge);
    }

    private static CommandRequest Ordre(string commande, string entite = "appart1:ecran") =>
        new() { Id = "1", EntityUniqueId = entite, Command = commande };

    [Fact]
    public void L_ecran_est_une_entite_display_panel_qui_porte_les_deux_commandes()
    {
        var plugin = new OpenHaspPlugin(Donnees.Reglages(), new Broker(), TimeProvider.System, NullLogger<OpenHaspPlugin>.Instance);

        var entites = Assert.Single(Assert.IsType<Declare>(plugin.Snapshot()[0]).Devices).Entities;

        var ecran = Assert.Single(entites, e => e.UniqueId == OpenHaspPlugin.EcranId("appart1"));
        Assert.Equal((EntityKind.Sensor, DeviceClasses.DisplayPanel), (ecran.Kind, ecran.DeviceClass));
        Assert.Equal([OpenHaspPlugin.Repaint, OpenHaspPlugin.Reboot], ecran.Commands);
        // La connexion reste la même, classe comprise : la supervision la suit déjà.
        Assert.Equal(DeviceClasses.Connectivity, Assert.Single(entites, e => e.UniqueId == "appart1").DeviceClass);
    }

    [Fact]
    public async Task Repeindre_efface_la_platine_et_lui_renvoie_ses_pages_et_ses_valeurs()
    {
        var (plugin, broker, horloge) = await EnLigneAsync();
        horloge.Advance(TimeSpan.FromMinutes(3));

        var r = await plugin.ExecuteAsync(Ordre(OpenHaspPlugin.Repaint), CancellationToken.None);

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        var publiees = broker.Publications.ToList();
        Assert.Equal(("hasp/appart1/command/clearpage", "all"), publiees[0]);
        Assert.Equal(MiseEnPage.Generer(Donnees.Salon(), Donnees.Reglages()).Count, publiees.Count(p => p.Topic == "hasp/appart1/command/jsonl"));
        // Tout l'affichage, même inchangé depuis la dernière peinture : la platine a été effacée.
        Assert.Contains(("hasp/appart1/command/p1b1.text", "Salon"), publiees);

        var ecran = Assert.IsType<StateBatch>(plugin.Snapshot()[1]).States.Single(s => s.EntityUniqueId == "appart1:ecran").Value;
        Assert.True(ecran.GetProperty("online").GetBoolean());
        Assert.Equal(horloge.GetUtcNow(), ecran.GetProperty("last_paint").GetDateTimeOffset());
        Assert.Equal(4, ecran.GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task Redemarrer_envoie_reboot_et_rien_d_autre()
    {
        var (plugin, broker, _) = await EnLigneAsync();

        var r = await plugin.ExecuteAsync(Ordre(OpenHaspPlugin.Reboot), CancellationToken.None);

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        // Sur le topic de commande, avec « reboot » en charge : jamais une charge vide, qui serait une lecture.
        Assert.Equal([("hasp/appart1/command", "reboot")], broker.Publications.ToList());
    }

    [Theory]
    [InlineData(OpenHaspPlugin.Repaint)]
    [InlineData(OpenHaspPlugin.Reboot)]
    public async Task Une_platine_hors_ligne_ne_recoit_rien(string commande)
    {
        var (plugin, broker, _) = await EnLigneAsync(enLigne: false);

        var r = await plugin.ExecuteAsync(Ordre(commande), CancellationToken.None);

        Assert.Equal(CommandOutcome.Unreachable, r.Outcome);
        Assert.Contains("hors ligne", r.Detail, StringComparison.Ordinal);
        Assert.Empty(broker.Publications);
    }

    [Fact]
    public async Task Broker_perdu_les_deux_commandes_le_disent()
    {
        var (plugin, broker, _) = await EnLigneAsync();
        await plugin.DeconnecteAsync();

        var r = await plugin.ExecuteAsync(Ordre(OpenHaspPlugin.Repaint), CancellationToken.None);

        Assert.Equal((CommandOutcome.Unreachable, "broker injoignable"), (r.Outcome, r.Detail));
        Assert.Empty(broker.Publications);
    }

    [Theory]
    [InlineData("turn_on", "appart1:ecran", "inconnue")]
    [InlineData(OpenHaspPlugin.Repaint, "appart1", "ne se commande pas")]   // la connexion, pas l'écran
    [InlineData(OpenHaspPlugin.Repaint, "appart9:ecran", "ne se commande pas")]
    public async Task Le_reste_est_refuse(string commande, string entite, string motif)
    {
        var (plugin, broker, _) = await EnLigneAsync();

        var r = await plugin.ExecuteAsync(Ordre(commande, entite), CancellationToken.None);

        Assert.Equal(CommandOutcome.Failed, r.Outcome);
        Assert.Contains(motif, r.Detail, StringComparison.Ordinal);
        Assert.Empty(broker.Publications);
    }
}
