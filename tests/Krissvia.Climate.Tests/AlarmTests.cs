using System.Net;
using System.Text.Json;
using Krissvia.Climate.Alarm;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Configuration;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// L'alarme : la machine à états de DomoMud au banc (les scénarios qu'on ne rejoue pas chez soi sans forcer une porte),
/// les codes et leur blocage, puis la centrale — on vérifie les ORDRES émis (sirènes), pas ses croyances.
/// </summary>
public class AlarmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly AlarmTimings Reglages = new(ExitDelaySeconds: 3, EntryDelaySeconds: 2, RingSeconds: 2, RingCount: 2, PauseSeconds: 3, NotifyArming: false, NotifyTrigger: true);
    private static readonly IReadOnlyList<string> Aucun = [];

    private static (AlarmState Etat, IReadOnlyList<AlarmOrder> Ordres) Traiter(AlarmState etat, AlarmEvent e, IReadOnlyList<string>? defauts = null) =>
        AlarmMachine.Process(etat, e, Reglages, defauts ?? Aucun, T0);

    private static AlarmState Secondes(AlarmState etat, int n, IReadOnlyList<string>? defauts = null)
    {
        for (var i = 0; i < n; i++) etat = Traiter(etat, new AlarmEvent.Second(), defauts).Etat;
        return etat;
    }

    private static readonly AlarmState Armee = new(AlarmMode.Armed, 0, null, null);

    // ── Machine ──────────────────────────────────────────────────────────────

    [Fact]
    public void L_armement_passe_par_le_decompte_de_sortie()
    {
        var (etat, ordres) = Traiter(AlarmState.Disarmed, new AlarmEvent.Arm("Christian"));

        Assert.Equal(new AlarmState(AlarmMode.ExitDelay, 3, null, null), etat);
        Assert.Contains(AlarmOrder.Journal("Armement demandé (Christian)"), ordres);
        Assert.Equal(AlarmMode.ExitDelay, Secondes(etat, 2).Mode);
        Assert.Equal(Armee, Secondes(etat, 3));
    }

    [Fact]
    public void Une_porte_ouverte_refuse_l_armement()
    {
        var (etat, ordres) = Traiter(AlarmState.Disarmed, new AlarmEvent.Arm("Christian"), ["Porte d'entrée"]);

        Assert.Equal(AlarmState.Disarmed, etat);
        Assert.Contains(AlarmOrder.Faults("Porte d'entrée"), ordres);
    }

    [Fact]
    public void On_sort_par_la_porte_mais_elle_doit_etre_refermee_a_la_fin_du_decompte()
    {
        var etat = Traiter(AlarmState.Disarmed, new AlarmEvent.Arm("Christian")).Etat;

        // Pendant la sortie, la porte s'ouvre : rien ne se passe.
        etat = Secondes(etat, 2, ["Porte d'entrée"]);
        Assert.Equal(AlarmMode.ExitDelay, Traiter(etat, new AlarmEvent.Detection("Porte d'entrée", Delayed: true)).Etat.Mode);

        // Fin du décompte, porte restée ouverte : l'armement est abandonné, et on le fait savoir.
        var (fin, ordres) = Traiter(etat, new AlarmEvent.Second(), ["Porte d'entrée"]);
        Assert.Equal(AlarmState.Disarmed, fin);
        Assert.Contains(ordres, o => o.Type == AlarmOrderType.Notification && o.Value.Contains("abandonné"));
    }

    [Fact]
    public void La_porte_temporisee_laisse_le_temps_de_taper_son_code()
    {
        var (entree, _) = Traiter(Armee, new AlarmEvent.Detection("Porte d'entrée", Delayed: true));
        Assert.Equal(new AlarmState(AlarmMode.EntryDelay, 2, null, "Porte d'entrée"), entree);

        // Le détecteur du couloir pendant l'entrée : toléré.
        Assert.Equal(entree, Traiter(entree, new AlarmEvent.Detection("Couloir", Delayed: false)).Etat);

        var (desarmee, ordres) = Traiter(Secondes(entree, 1), new AlarmEvent.Disarm("Christine"));
        Assert.Equal(AlarmState.Disarmed, desarmee);
        Assert.DoesNotContain(ordres, o => o == AlarmOrder.Siren(true));
    }

    [Fact]
    public void Une_entree_sans_code_declenche_a_l_expiration()
    {
        var entree = Traiter(Armee, new AlarmEvent.Detection("Porte d'entrée", Delayed: true)).Etat;
        var (etat, ordres) = Traiter(Secondes(entree, 1), new AlarmEvent.Second());

        Assert.Equal(new AlarmState(AlarmMode.Triggered, 2, T0, "Porte d'entrée", Ring: 1, Ringing: true), etat);
        Assert.Contains(AlarmOrder.Siren(true), ordres);
        Assert.Contains(AlarmOrder.Notification("Alarme déclenchée : Porte d'entrée"), ordres);
    }

    [Fact]
    public void Un_capteur_immediat_declenche_aussitot()
    {
        var (etat, ordres) = Traiter(Armee, new AlarmEvent.Detection("Fenêtre chambre", Delayed: false));

        Assert.Equal(AlarmMode.Triggered, etat.Mode);
        Assert.Contains(AlarmOrder.Siren(true), ordres);
    }

    [Fact]
    public void Rien_ne_declenche_une_alarme_desarmee_ou_en_sortie()
    {
        Assert.Equal(AlarmState.Disarmed, Traiter(AlarmState.Disarmed, new AlarmEvent.Detection("Fenêtre", false)).Etat);
        var sortie = Traiter(AlarmState.Disarmed, new AlarmEvent.Arm("Christian")).Etat;
        Assert.Equal(sortie, Traiter(sortie, new AlarmEvent.Detection("Fenêtre", false)).Etat);
    }

    [Fact]
    public void La_sirene_sonne_par_sonneries_puis_se_tait_et_l_alarme_reste_declenchee()
    {
        // Deux sonneries de 2 s séparées de 3 s de pause (ARCHITECTURE.md §9) : la sirène, seconde par seconde.
        var etat = Traiter(Armee, new AlarmEvent.Detection("Fenêtre", false)).Etat;
        var sirene = new List<string>();
        for (var s = 1; s <= 10; s++)
        {
            var (suivant, ordres) = Traiter(etat, new AlarmEvent.Second());
            sirene.AddRange(ordres.Where(o => o.Type == AlarmOrderType.Siren).Select(o => $"{s}:{(o.Value == "1" ? "marche" : "arrêt")}"));
            etat = suivant;
        }

        Assert.Equal(["2:arrêt", "5:marche", "7:arrêt"], sirene);
        Assert.Equal(new AlarmState(AlarmMode.Triggered, 0, T0, "Fenêtre"), etat);
    }

    [Fact]
    public void Le_desarmement_coupe_la_sirene_meme_quand_on_la_croit_silencieuse()
    {
        Assert.Contains(AlarmOrder.Siren(false), Traiter(AlarmState.Disarmed, new AlarmEvent.Disarm("Christian")).Ordres);
        Assert.Contains(AlarmOrder.Siren(false), Traiter(Armee, new AlarmEvent.Disarm("Christian")).Ordres);
    }

    [Fact]
    public void Armer_une_alarme_deja_armee_republie_son_etat()
    {
        var (etat, ordres) = Traiter(Armee, new AlarmEvent.Arm("Christian"));

        Assert.Equal(Armee, etat);
        Assert.Equal([AlarmOrder.State(Armee)], ordres);
    }

    [Fact]
    public void Au_redemarrage_une_sortie_redevient_desarmee_une_entree_redevient_armee()
    {
        Assert.Equal(AlarmState.Disarmed, AlarmMachine.AfterRestart(new AlarmState(AlarmMode.ExitDelay, 12, null, null)));
        Assert.Equal(Armee, AlarmMachine.AfterRestart(new AlarmState(AlarmMode.EntryDelay, 5, null, "Porte")));
        Assert.Equal(new AlarmState(AlarmMode.Triggered, 0, T0, "Porte"), AlarmMachine.AfterRestart(new AlarmState(AlarmMode.Triggered, 90, T0, "Porte")));
    }

    // ── Défauts ──────────────────────────────────────────────────────────────

    [Fact]
    public void Un_detecteur_actif_n_est_pas_un_defaut_mais_un_capteur_muet_en_est_un()
    {
        var defauts = AlarmFaults.Check(
        [
            new("Porte", Active: false, LastUpdate: T0.AddHours(-1)),
            new("Fenêtre", Active: true, LastUpdate: T0),
            new("Radar", Active: true, LastUpdate: T0, ActiveIsFault: false),
            new("Garage", Active: false, LastUpdate: T0.AddHours(-13)),
            new("Cave", Active: null, LastUpdate: null),
            new("Radar muet", Active: false, LastUpdate: T0.AddHours(-13), ActiveIsFault: false),
        ], T0, silenceMinutes: 720);

        Assert.Equal(["Fenêtre", "Garage (muet)", "Cave (sans nouvelles)", "Radar muet (muet)"], defauts);
        Assert.DoesNotContain("Garage (muet)", AlarmFaults.Check([new("Garage", false, T0.AddDays(-3))], T0, silenceMinutes: 0));
    }

    [Fact]
    public void Les_reglages_bornent_la_sirene_et_refusent_une_alarme_sans_capteur()
    {
        var bons = new AlarmSettings { Enabled = true, Sensors = [new("binary_sensor.porte", SensorRole.Delayed)] };
        AlarmService.Validate(bons);

        // Le « 0 = illimité » de DomoMud n'existe plus (ARCHITECTURE.md §9).
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { RingSeconds = 0 }));
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { RingSeconds = 31 }));
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { RingCount = 0 }));
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { PauseSeconds = 4 }));
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { Sensors = [] }));
        Assert.Throws<AlarmValidationException>(() => AlarmService.Validate(bons with { Sensors = [.. bons.Sensors, new("binary_sensor.porte")] }));
        AlarmService.Validate(bons with { Enabled = false, Sensors = [] });   // se configurer avant de s'activer
    }

    // ── Codes et blocage ─────────────────────────────────────────────────────

    [Fact]
    public void Un_code_se_verifie_sans_etre_garde_en_clair()
    {
        var code = AlarmCodes.Create("1", "Christian", "4821");

        Assert.DoesNotContain("4821", code.Hash + code.Salt);
        Assert.True(AlarmCodes.Verify("4821", code));
        Assert.False(AlarmCodes.Verify("4822", code));
        Assert.False(AlarmCodes.Verify("4821", code with { Salt = "pas du base64 !" }));
        Assert.Equal("Christian", AlarmCodes.Identify("4821", [AlarmCodes.Create("2", "Christine", "1111"), code]));
        Assert.False(AlarmCodes.IsAcceptable("12a4"));
        Assert.False(AlarmCodes.IsAcceptable("123"));
        Assert.True(AlarmCodes.IsAcceptable("0000"));
    }

    [Fact]
    public void Trois_codes_faux_bloquent_soixante_secondes_puis_le_compteur_repart_de_zero()
    {
        var v = default(AlarmLockState);
        v = AlarmLockout.Failure(AlarmLockout.Failure(v, T0), T0);
        Assert.False(AlarmLockout.IsLocked(v, T0));

        v = AlarmLockout.Failure(v, T0);
        Assert.True(AlarmLockout.IsLocked(v, T0.AddSeconds(59)));
        Assert.Equal(1, AlarmLockout.SecondsLeft(v, T0.AddSeconds(59)));
        Assert.False(AlarmLockout.IsLocked(v, T0.AddSeconds(60)));

        // Après l'expiration, une erreur de frappe ne rebloque pas aussitôt.
        Assert.False(AlarmLockout.IsLocked(AlarmLockout.Failure(v, T0.AddSeconds(61)), T0.AddSeconds(61)));
        Assert.Equal(default, AlarmLockout.Success());
    }

    // ── Centrale ─────────────────────────────────────────────────────────────

    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(Documents.Where(kv => kv.Key.Item1 == collection)
                .Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            lock (Documents) Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(Documents.Remove((collection, id)));
    }

    private sealed class Ecrivain : IEntityStateWriter
    {
        public JsonElement? Dernier { get; private set; }
        public void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null) => Dernier = value;
        public void SetAvailability(string entityId, bool available) { }
    }

    private sealed class FauxNtfy : HttpMessageHandler
    {
        public List<string> Recues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var corps = await request.Content!.ReadAsStringAsync(ct);
            lock (Recues) Recues.Add(corps);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FakeStateReader Etats { get; } = new();
        public ScriptedCommander Commandes { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public Magasin Magasin { get; }
        public Ecrivain Ecrivain { get; } = new();
        public FauxNtfy Ntfy { get; } = new();
        public AlarmService Service { get; }
        public AlarmEngine Centrale { get; }

        public string Porte => Id("porte");
        public string Fenetre => Id("fenetre");
        public string Radar => Id("radar");
        public string Sirene => Id("sirene");

        public Banc(Magasin? magasin = null)
        {
            Magasin = magasin ?? new Magasin();
            var notifications = new NotificationService(Magasin, new HttpClient(Ntfy));
            Service = new AlarmService(Magasin, Horloge);
            Centrale = new AlarmEngine(Service, Registre, Etats, Ecrivain, Commandes, notifications, Horloge);
            Registre.RegisterAsync("z2m",
            [
                Appareil("porte", "Porte d'entrée", EntityKind.BinarySensor, DeviceClasses.Opening),
                Appareil("fenetre", "Fenêtre chambre", EntityKind.BinarySensor, DeviceClasses.Opening),
                Appareil("radar", "Radar salon", EntityKind.BinarySensor, DeviceClasses.Occupancy),
                Appareil("sirene", "Sirène", EntityKind.Switch, null, [StandardCommands.TurnOn, StandardCommands.TurnOff]),
            ]).GetAwaiter().GetResult();
            // Les noms tels que l'utilisateur les a choisis dans l'écran Entités.
            foreach (var (id, nom) in new[] { ("porte", "Porte d'entrée"), ("fenetre", "Fenêtre chambre"), ("radar", "Radar salon"), ("sirene", "Sirène") })
                Registre.RenameAsync(Id(id), nom).GetAwaiter().GetResult();
            foreach (var c in new[] { Porte, Fenetre, Radar }) Etats.Set(c, false, T0);
            if (magasin is null)
            {
                notifications.SaveAsync(new NotificationSettings { Enabled = true, BaseUrl = "http://ntfy.essai", Topic = "alarme" }).GetAwaiter().GetResult();
                Service.SaveSettingsAsync(new AlarmSettings
                {
                    Enabled = true, ExitDelaySeconds = 2, EntryDelaySeconds = 2,
                    Sensors = [new(Porte, SensorRole.Delayed), new(Fenetre), new(Radar)],
                    Sirens = [Sirene],
                }).GetAwaiter().GetResult();
                Service.SetCodeAsync("Christian", "4821").GetAwaiter().GetResult();
            }

            Centrale.StartAsync().GetAwaiter().GetResult();
        }

        private static DeviceDescriptor Appareil(string id, string nom, EntityKind famille, string? classe, IReadOnlyList<string>? commandes = null) => new()
        {
            UniqueId = id, Name = nom,
            Entities = [new EntityDescriptor { UniqueId = id, Kind = famille, DeviceClass = classe, Commands = commandes ?? [] }],
        };

        private string Id(string uniqueId) => Registre.Loaded.Single(e => e.UniqueId == uniqueId).EntityId;

        /// <summary>Le capteur change d'état, comme le publierait son plugin.</summary>
        public Task BasculerAsync(string id, bool actif)
        {
            var avant = Etats.Get(id);
            Etats.Set(id, actif, Horloge.GetUtcNow());
            return Centrale.OnStateChangedAsync(new EntityStateChanged(id, avant, Etats.Get(id)!));
        }

        public async Task SecondesAsync(int n)
        {
            for (var i = 0; i < n; i++)
            {
                Horloge.Advance(TimeSpan.FromSeconds(1));
                await Centrale.TickAsync();
            }
        }

        public async Task ArmerAsync()
        {
            await Centrale.CommandAsync(true, "Christian");
            await SecondesAsync(2);
            Assert.Equal(AlarmMode.Armed, Centrale.State.Mode);
        }

        public async Task<List<(string EntityId, string Command, CommandPriority Priority)>> OrdresAsync()
        {
            await Centrale.WhenIdleAsync();
            return Commandes.Sent;
        }
    }

    [Fact]
    public async Task Une_intrusion_met_la_sirene_en_marche_et_previent()
    {
        var b = new Banc();
        await b.ArmerAsync();

        await b.BasculerAsync(b.Fenetre, true);

        Assert.Equal(AlarmMode.Triggered, b.Centrale.State.Mode);
        Assert.Equal([(b.Sirene, StandardCommands.TurnOn, CommandPriority.Safety)], await b.OrdresAsync());
        Assert.Contains(b.Ntfy.Recues, m => JsonDocument.Parse(m).RootElement.GetProperty("message").GetString() == "Alarme déclenchée : Fenêtre chambre");
        Assert.Equal("triggered", b.Ecrivain.Dernier!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Le_desarmement_coupe_la_sirene_sans_condition()
    {
        var b = new Banc();

        // Désarmée et silencieuse selon la centrale : l'ordre d'arrêt part quand même.
        await b.Centrale.CommandAsync(false, "Christian");

        Assert.Equal([(b.Sirene, StandardCommands.TurnOff, CommandPriority.Safety)], await b.OrdresAsync());
    }

    [Fact]
    public async Task Seuls_les_fronts_comptent_et_jamais_un_premier_etat()
    {
        var b = new Banc();
        await b.ArmerAsync();

        // Premier message après un redémarrage (pas d'état précédent), puis un état restauré encore indisponible.
        await b.Centrale.OnStateChangedAsync(new EntityStateChanged(b.Fenetre, null, new EntityStateSnapshot(JsonSerializer.SerializeToElement(true), T0)));
        var restaure = new EntityStateSnapshot(JsonSerializer.SerializeToElement(false), T0, Available: false);
        await b.Centrale.OnStateChangedAsync(new EntityStateChanged(b.Fenetre, restaure, new EntityStateSnapshot(JsonSerializer.SerializeToElement(true), T0)));
        Assert.Equal(AlarmMode.Armed, b.Centrale.State.Mode);

        // Le radar réaffirme sa détection : true → true n'est pas un front.
        b.Etats.Set(b.Radar, true, T0);
        await b.BasculerAsync(b.Radar, true);
        Assert.Equal(AlarmMode.Armed, b.Centrale.State.Mode);

        await b.BasculerAsync(b.Radar, false);
        await b.BasculerAsync(b.Radar, true);
        Assert.Equal(AlarmMode.Triggered, b.Centrale.State.Mode);
    }

    [Fact]
    public async Task Un_radar_qui_voit_la_personne_n_empeche_pas_d_armer_un_capteur_muet_si()
    {
        var b = new Banc();
        b.Etats.Set(b.Radar, true, T0);
        await b.ArmerAsync();
        await b.Centrale.CommandAsync(false, "Christian");

        b.Horloge.Advance(TimeSpan.FromHours(13));   // plus rien n'a parlé depuis 13 h
        await b.Centrale.CommandAsync(true, "Christian");

        Assert.Equal(AlarmMode.Disarmed, b.Centrale.State.Mode);
        Assert.Contains("Porte d'entrée (muet)", b.Centrale.LastFaults);
    }

    [Fact]
    public async Task Trois_codes_faux_bloquent_meme_le_bon_puis_il_repasse()
    {
        var b = new Banc();

        Assert.Equal(AlarmCodeOutcome.WrongCode, (await b.Centrale.CommandWithCodeAsync(true, "0000")).Outcome);
        Assert.Equal(AlarmCodeOutcome.WrongCode, (await b.Centrale.CommandWithCodeAsync(true, "0001")).Outcome);
        Assert.Equal(AlarmCodeOutcome.Locked, (await b.Centrale.CommandWithCodeAsync(true, "0002")).Outcome);
        Assert.Equal(AlarmCodeOutcome.Locked, (await b.Centrale.CommandWithCodeAsync(true, "4821")).Outcome);
        Assert.Equal(AlarmMode.Disarmed, b.Centrale.State.Mode);

        b.Horloge.Advance(TimeSpan.FromSeconds(60));
        var ok = await b.Centrale.CommandWithCodeAsync(true, "4821");

        Assert.Equal(new AlarmCodeResult(AlarmCodeOutcome.Done, "Christian"), ok);
        Assert.Equal(AlarmMode.ExitDelay, b.Centrale.State.Mode);
        Assert.Contains(b.Service.Journal, l => l.Text == "Armement demandé (Christian)");
    }

    private static Task<CommandOutcome> Ordonner(Banc b, string commande, string? code)
    {
        var entite = b.Registre.Loaded.Single(e => e.IntegrationId == AlarmEngine.IntegrationId);
        var args = code is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(new { code });
        return b.Centrale.HandleAsync(entite, new EntityCommand(entite.EntityId, commande) { Args = args }, CancellationToken.None);
    }

    [Fact]
    public async Task La_carte_arme_et_desarme_avec_un_code_et_rapporte_la_raison_d_un_refus()
    {
        var b = new Banc();
        var entite = b.Registre.Loaded.Single(e => e.IntegrationId == AlarmEngine.IntegrationId);
        Assert.Equal([StandardCommands.Arm, StandardCommands.Disarm], entite.Commands);

        Assert.Equal("code manquant", (await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Arm, null))).Message);
        Assert.Equal("code incorrect", (await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Arm, "0000"))).Message);

        // Porte ouverte : le code est bon, l'armement refusé, et la carte dit pourquoi.
        b.Etats.Set(b.Porte, true, T0);
        var refus = await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Arm, "4821"));
        Assert.Equal("armement refusé : Porte d'entrée", refus.Message);

        b.Etats.Set(b.Porte, false, T0);
        Assert.Equal(CommandOutcome.Accepted, await Ordonner(b, StandardCommands.Arm, "4821"));
        Assert.Equal(AlarmMode.ExitDelay, b.Centrale.State.Mode);
        Assert.Equal(CommandOutcome.Accepted, await Ordonner(b, StandardCommands.Disarm, "4821"));
        Assert.Equal(AlarmMode.Disarmed, b.Centrale.State.Mode);

        // Les codes faux de la carte comptent pour le même blocage que l'API.
        await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Disarm, "1"));
        await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Disarm, "2"));
        Assert.StartsWith("trop de codes faux", (await Assert.ThrowsAsync<ArgumentException>(() => Ordonner(b, StandardCommands.Disarm, "3"))).Message);
        Assert.Equal(AlarmCodeOutcome.Locked, (await b.Centrale.CommandWithCodeAsync(false, "4821")).Outcome);
    }

    [Fact]
    public async Task Un_redemarrage_ne_desarme_pas_l_alarme()
    {
        var b = new Banc();
        await b.ArmerAsync();
        await b.BasculerAsync(b.Porte, true);
        Assert.Equal(AlarmMode.EntryDelay, b.Centrale.State.Mode);

        var apres = new Banc(b.Magasin);

        Assert.Equal(AlarmMode.Armed, apres.Centrale.State.Mode);
        Assert.Equal("armed", apres.Ecrivain.Dernier!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Desactiver_l_alarme_la_desarme_et_coupe_la_sirene()
    {
        var b = new Banc();
        await b.ArmerAsync();
        await b.BasculerAsync(b.Fenetre, true);

        await b.Service.SaveSettingsAsync(b.Service.Settings with { Enabled = false });
        await b.Centrale.OnSettingsChangedAsync();

        Assert.Equal(AlarmMode.Disarmed, b.Centrale.State.Mode);
        Assert.Equal((b.Sirene, StandardCommands.TurnOff, CommandPriority.Safety), (await b.OrdresAsync())[^1]);
        Assert.Equal(AlarmCodeOutcome.Disabled, (await b.Centrale.CommandWithCodeAsync(true, "4821")).Outcome);
    }

    [Fact]
    public async Task Les_codes_sont_enregistres_haches_et_jamais_deux_fois_le_meme()
    {
        var b = new Banc();

        await Assert.ThrowsAsync<AlarmValidationException>(() => b.Service.SetCodeAsync("Christine", "4821"));
        await b.Service.SetCodeAsync("Christine", "1357");
        await b.Service.SetCodeAsync("christian", "2468");   // change le code de Christian, sans doublon

        Assert.Equal(["christian", "Christine"], b.Service.CodeHolders.Select(c => c.Name));
        Assert.Null(await b.Service.IdentifyAsync("4821"));
        Assert.Equal("christian", await b.Service.IdentifyAsync("2468"));
        Assert.DoesNotContain(b.Magasin.Documents.Values, json => json.Contains("2468") || json.Contains("1357"));
    }
}
