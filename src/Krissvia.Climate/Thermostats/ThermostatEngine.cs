using System.Collections.Concurrent;
using Krissvia.Climate.Presence;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Climate.Thermostats;

/// <summary>Ce que montre un thermostat, par ordre de gravité (le premier qui s'applique l'emporte).</summary>
public enum ThermostatStatus
{
    /// <summary>Sonde intérieure muette ou indisponible : chauffage coupé (failsafe).</summary>
    SensorLost,

    /// <summary>
    /// Un radiateur visé n'existe pas (ou plus) au registre : le moteur régule dans le vide. Faute de
    /// configuration, plus grave que tout état de régulation (le « Chbre » de DomoMud).
    /// </summary>
    UnknownSwitch,

    /// <summary>Coupé par une inhibition extérieure (délestage).</summary>
    Inhibited,

    WindowOpen,

    /// <summary>Au-dessus de la consigne : on laisse l'inertie redescendre.</summary>
    Coasting,

    Off,
    Heating,
    Idle,

    /// <summary>Planning : on chauffe en avance pour que la consigne du prochain créneau soit atteinte à l'heure.</summary>
    Preheating,
}

/// <summary>État publié d'un thermostat : un instantané, jamais l'objet que le moteur modifie (C6).</summary>
public sealed record ThermostatSnapshot(
    string Id,
    ThermostatStatus Status,
    ThermostatMode Mode,
    double Target,
    double? Temperature,
    double? OutdoorTemperature,
    double Power,
    bool Heating,
    double Integral,
    IReadOnlyCollection<string> InhibitionReasons,
    IReadOnlyList<string> UnknownSwitches,
    double? OutdoorForecast = null,
    HeatingSlope? Slope = null,
    bool Away = false);

/// <summary>
/// Moteur des thermostats : régulation TPI de DomoMud (terme proportionnel, feed-forward extérieur, intégrale,
/// inertie, délais minimaux, calcul figé par cycle), avec ses protections.
/// </summary>
/// <remarks>
/// Écarts voulus avec DomoMud (TESTS-CARACTERISATION.md, C1 à C7) :
/// <list type="bullet">
/// <item>Les protections fenêtre ouverte et sonde perdue sont des INHIBITIONS du <see cref="ThermostatInhibitor"/>,
/// comme le délestage : coupure immédiate, vérifiée, retentée (ARCHITECTURE.md §7-8). Le moteur pose et lève le
/// motif ; le relais, c'est l'inhibiteur qui le coupe. Tant qu'un motif existe, le moteur n'allume rien.</item>
/// <item>C4/C5 : le moteur ne connaît du relais que le dernier ordre ACCEPTÉ, jamais sa croyance. Inconnu au
/// démarrage et à l'entrée de chaque épisode (Off, inertie, fin d'inhibition) : l'ordre part alors sans
/// condition. Un ordre refusé est renvoyé au tick suivant. Une bascule acceptée n'est pas répétée.</item>
/// <item>Sonde indisponible (plugin mort) = sonde perdue tout de suite, sans attendre le délai : principe 3 de
/// l'architecture. Le délai ne sert qu'à la sonde qui se tait sans que rien ne le dise.</item>
/// <item>C7 : sous inhibition extérieure (délestage), l'intégrale est conservée ; fenêtre ouverte, elle repart
/// de zéro ; sonde perdue, conservée.</item>
/// </list>
/// Portés depuis : prévision météo dans le feed-forward, préchauffage anticipé et apprentissage de la pente,
/// absence (mode d'absence, remontée avant le retour programmé). L'auto-TPI ne passe pas par ici : il propose des
/// réglages (AutoTpiService), que le moteur applique comme tout réglage saisi.
/// </remarks>
public sealed class ThermostatEngine(
    IEntityStateReader states,
    IEntityRegistry registry,
    ICommandBus commands,
    ThermostatInhibitor inhibitor,
    TimeProvider clock,
    IHeatingSlopeStore? slopes = null,
    IHomeAbsence? absence = null)
{
    private readonly IHeatingSlopeStore _magasinPentes = slopes ?? new MemoryHeatingSlopeStore();

    /// <summary>Pentes apprises, par thermostat ; chargées à la première configuration.</summary>
    private readonly Dictionary<string, HeatingSlope> _pentes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeatingSlope> _pentesAEnregistrer = new(StringComparer.Ordinal);
    private bool _pentesChargees;

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Dictionary<string, Runtime> _runtimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ThermostatSnapshot> _instantanes = new(StringComparer.Ordinal);
    private IReadOnlyList<ThermostatSettings> _thermostats = [];

    /// <summary>État interne d'un thermostat, propriété exclusive du moteur (sous verrou).</summary>
    private sealed class Runtime
    {
        /// <summary>Début du cycle en cours ; null = aucun cycle encore calculé.</summary>
        public DateTimeOffset? DebutCycle;
        public double? CibleCycle;
        public double Puissance;
        public int SecondesMarche;
        public double Integrale;
        public bool EnInertie;
        public bool EnArret;

        /// <summary>Dernier ordre accepté sur les radiateurs ; null = inconnu, le prochain ordre part sans condition.</summary>
        public bool? RelaisConnu;

        public bool OuvertureVue;
        public DateTimeOffset? OuvertDepuis;
        public DateTimeOffset? FermeDepuis;
        public bool FenetreSuspendue;

        /// <summary>Fenêtre d'observation de la pente : début et ambiante au début (chauffe à pleine puissance).</summary>
        public DateTimeOffset? PenteDebut;
        public double PenteAmbiante;
    }

    public IReadOnlyCollection<ThermostatSnapshot> Snapshots => _instantanes.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    public ThermostatSnapshot? GetSnapshot(string id) => _instantanes.GetValueOrDefault(id);

    // ── Configuration ────────────────────────────────────────────────────────

    public async Task ConfigureAsync(IReadOnlyList<ThermostatSettings> thermostats, CancellationToken ct = default)
    {
        foreach (var t in thermostats) t.Validate();
        if (thermostats.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != thermostats.Count)
            throw new ArgumentException("Deux thermostats ont le même identifiant.", nameof(thermostats));

        await _verrou.WaitAsync(ct);
        try
        {
            if (!_pentesChargees)
            {
                foreach (var (id, pente) in await _magasinPentes.LoadAsync(ct)) _pentes[id] = pente;
                _pentesChargees = true;
            }

            var presents = thermostats.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var retire in _runtimes.Keys.Where(k => !presents.Contains(k)).ToList())
            {
                // Un thermostat retiré ne doit pas laisser derrière lui une fenêtre ou une sonde qui coupe encore.
                inhibitor.Lift(retire, InhibitionReasons.FenetreOuverte);
                inhibitor.Lift(retire, InhibitionReasons.SondePerdue);
                _runtimes.Remove(retire);
                _instantanes.TryRemove(retire, out _);
            }

            _thermostats = thermostats.ToList();
        }
        finally
        {
            _verrou.Release();
        }
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    /// <summary>Une itération de régulation de tous les thermostats (toutes les 10 s en production).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            await inhibitor.RetryPendingAsync(ct);
            foreach (var t in _thermostats)
            {
                if (!_runtimes.TryGetValue(t.Id, out var rt))
                {
                    rt = new Runtime();
                    _runtimes[t.Id] = rt;
                }

                _instantanes[t.Id] = await TraiterAsync(t, rt, ct);
            }

            foreach (var (id, pente) in _pentesAEnregistrer) await _magasinPentes.SaveAsync(id, pente, ct);
            _pentesAEnregistrer.Clear();
        }
        finally
        {
            _verrou.Release();
        }
    }

    private async Task<ThermostatSnapshot> TraiterAsync(ThermostatSettings t, Runtime rt, CancellationToken ct)
    {
        var maintenant = clock.GetUtcNow();
        var heureLocale = clock.GetLocalNow().DateTime;
        // Absence : le mode d'absence remplace planning et mode manuel, pour les thermostats qui la suivent.
        var foyer = t.FollowAbsence ? absence?.Current : null;
        var absent = foyer is { Away: true };
        var mode = absent ? t.AwayMode : t.ModeAt(heureLocale);
        // En Off, la consigne ajustée ne s'applique pas (Off est ferme) : on montre celle qui l'est réellement. En
        // absence non plus : un « +0,5 » laissé avant de partir chaufferait la maison vide toutes les vacances.
        var cible = mode == ThermostatMode.Off || absent ? t.TargetFor(mode) : t.TargetOverride ?? t.TargetFor(mode);

        var sonde = states.Get(t.TemperatureEntityId);
        double? interieur = sonde is not null && sonde.TryGetNumber(out var ti) ? ti : null;
        double? exterieur = t.OutdoorTemperatureEntityId is { } ext && states.Get(ext) is { } se && se.TryGetNumber(out var te) ? te : null;
        // Anticipation météo : la moyenne prévue sur 2 h remplace la mesure dans le feed-forward, la mesure reste
        // celle qu'on affiche. Sans prévision utilisable, on retombe sur la mesure.
        double? prevue = t.ForecastAnticipation && t.ForecastEntityId is { } prevision
            ? OutdoorForecast.Anticipated(states.Get(prevision), maintenant)
            : null;
        var inconnus = await RadiateursInconnusAsync(t, ct);

        var pente = _pentes.GetValueOrDefault(t.Id) ?? new HeatingSlope(Preheat.DefaultMinutesPerDegree, null);
        double PenteFenetre(double future, TimeSpan avant) =>
            Preheat.CorrectedMinutesPerDegree(pente, t.Tpi.Ki, future, MoyennePrevue(t, maintenant, avant));

        // Remontée avant le retour programmé : calculée AVANT la sortie « Off », qu'elle doit pouvoir lever — sinon un
        // mode d'absence Off ferait rentrer de vacances dans une maison froide, ce qui ôte tout son sens à l'absence
        // programmée (le piège de DomoMud). Un Off choisi hors absence reste ferme.
        var remontee = absent && foyer!.Return is { } retour && interieur is { } ambiante
            ? Preheat.ReturnTarget(t, ambiante, heureLocale, TimeZoneInfo.ConvertTime(retour, clock.LocalTimeZone).DateTime, PenteFenetre)
            : null;

        ThermostatSnapshot Instantane(ThermostatStatus statut) => new(
            t.Id, statut, mode, cible, interieur, exterieur, rt.Puissance, rt.RelaisConnu == true, rt.Integrale,
            inhibitor.GetReasons(t.Id), inconnus, prevue, _pentes.GetValueOrDefault(t.Id), absent);

        void Arreter()
        {
            rt.Puissance = 0;
            rt.SecondesMarche = 0;
            rt.CibleCycle = null;   // la reprise recalcule tout de suite au lieu d'attendre la fin du cycle
            rt.PenteDebut = null;   // la montée observée est interrompue : elle ne dit plus rien de la pièce
        }

        // ── 1. Fenêtre ouverte ───────────────────────────────────────────────
        if (t.OpeningEntityId is { } ouverture)
        {
            SuivreOuverture(t, rt, states.Get(ouverture) is { } so && so.TryGetBool(out var ouvert) && ouvert, maintenant);
            await AppliquerMotifAsync(t, InhibitionReasons.FenetreOuverte, rt.FenetreSuspendue, ct);
            if (rt.FenetreSuspendue) rt.Integrale = 0;   // la pièce s'est vidée de sa chaleur : on repart de zéro
        }

        // ── 2. Sonde perdue (failsafe) ───────────────────────────────────────
        var sondePerdue = t.FailsafeMinutes > 0 &&
                          (interieur is null || maintenant - sonde!.MeasuredAt > TimeSpan.FromMinutes(t.FailsafeMinutes));
        await AppliquerMotifAsync(t, InhibitionReasons.SondePerdue, sondePerdue, ct);

        // ── 3. Inhibé, par n'importe quel motif : l'inhibiteur a coupé, on n'allume rien ─
        var motifs = inhibitor.GetReasons(t.Id);
        if (motifs.Count > 0)
        {
            Arreter();
            rt.RelaisConnu = null;   // coupé par l'inhibiteur : à la levée, l'état voulu repartira sans condition
            var statut = sondePerdue ? ThermostatStatus.SensorLost
                : inconnus.Count > 0 ? ThermostatStatus.UnknownSwitch
                : motifs.Any(m => m is not (InhibitionReasons.FenetreOuverte or InhibitionReasons.SondePerdue)) ? ThermostatStatus.Inhibited
                : ThermostatStatus.WindowOpen;
            return Instantane(statut);
        }

        // ── 4. Mode Off : arrêt sans condition, intégrale remise à zéro. Ferme, même avec une consigne ajustée.
        if (mode == ThermostatMode.Off && remontee is null)
        {
            // C4 : à l'entrée en Off, l'arrêt part même si le moteur croit le radiateur éteint (allumé à la main ?).
            if (!rt.EnArret) rt.RelaisConnu = null;
            rt.EnArret = true;
            Arreter();
            rt.Integrale = 0;
            rt.EnInertie = false;
            await PiloterAsync(t, rt, chauffer: false, ct);
            return Instantane(inconnus.Count > 0 ? ThermostatStatus.UnknownSwitch : ThermostatStatus.Off);
        }

        rt.EnArret = false;

        // ── 5. Sans température (failsafe désactivé) : on ne change rien ─────
        if (interieur is not { } tint)
        {
            rt.PenteDebut = null;
            return Instantane(inconnus.Count > 0 ? ThermostatStatus.UnknownSwitch : ThermostatStatus.Idle);
        }

        // ── 5 bis. Préchauffage : la consigne de l'arrivée, ou celle du prochain créneau, assez tôt pour l'atteindre à
        // l'heure. Pas de créneau pendant l'absence : le planning ne s'applique pas à une maison vide.
        var prechauffe = remontee ?? (absent ? null : Preheat.Target(t, tint, heureLocale, cible, PenteFenetre));
        if (prechauffe is { } cibleFuture) cible = cibleFuture;

        // ── 6. Inertie : double seuil, pour absorber l'inertie du radiateur ──
        if (t.Tpi.CoastingHigh > 0)
        {
            var ecart = tint - cible;
            if (!rt.EnInertie && ecart > t.Tpi.CoastingHigh)
            {
                rt.EnInertie = true;
                rt.RelaisConnu = null;   // C4 : on coupe même si le moteur croit le radiateur déjà éteint
            }
            else if (rt.EnInertie && ecart <= t.Tpi.CoastingLow)
            {
                rt.EnInertie = false;
            }

            if (rt.EnInertie)
            {
                Arreter();
                rt.Integrale *= 0.8;   // dissipée : sinon elle relancerait une chauffe excessive à la reprise
                await PiloterAsync(t, rt, chauffer: false, ct);
                return Instantane(inconnus.Count > 0 ? ThermostatStatus.UnknownSwitch : ThermostatStatus.Coasting);
            }
        }

        // ── 7. Cycle TPI : calculé une fois par cycle, ou tout de suite si la consigne change ─
        var ecoule = rt.DebutCycle is { } debut ? maintenant - debut : TimeSpan.Zero;
        if (rt.DebutCycle is null || ecoule >= TimeSpan.FromSeconds(t.Tpi.CycleSeconds) ||
            rt.CibleCycle is not { } cc || Math.Abs(cible - cc) > 0.01)
        {
            // L'erreur s'intègre au prorata du temps réellement écoulé : un cycle interrompu ne compte pas pour 10 min.
            var cycle = Tpi.ComputeCycle(t.Tpi, cible, tint, prevue ?? exterieur, rt.Integrale, ecoule);
            rt.Integrale = cycle.Integral;
            rt.Puissance = cycle.Power;
            rt.SecondesMarche = cycle.OnSeconds;
            rt.DebutCycle = maintenant;
            rt.CibleCycle = cible;
            ecoule = TimeSpan.Zero;
        }

        var chauffer = rt.SecondesMarche > 0 && ecoule < TimeSpan.FromSeconds(rt.SecondesMarche);
        await PiloterAsync(t, rt, chauffer, ct);

        Apprendre(t, rt, tint, exterieur ?? prevue, maintenant);

        var regime = prechauffe is not null ? ThermostatStatus.Preheating
            : rt.RelaisConnu == true ? ThermostatStatus.Heating
            : ThermostatStatus.Idle;
        return Instantane(inconnus.Count > 0 ? ThermostatStatus.UnknownSwitch : regime);
    }

    /// <summary>
    /// Température extérieure moyenne prévue sur la durée qui reste avant un créneau (une demi-heure au moins), si le
    /// thermostat a une prévision active et utilisable.
    /// </summary>
    private double? MoyennePrevue(ThermostatSettings t, DateTimeOffset maintenant, TimeSpan fenetre) =>
        t.ForecastAnticipation && t.ForecastEntityId is { } prevision
            ? OutdoorForecast.MeanOver(states.Get(prevision), maintenant, fenetre < TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : fenetre)
            : null;

    /// <summary>
    /// Apprentissage de la pente (DomoMud) : pendant une chauffe à pleine puissance (90 % ou plus), dès que la pièce
    /// a gagné 0,5 °C en 5 min au moins, la pente mesurée est fusionnée, puis enregistrée à la fin du tick. Deux
    /// heures sans gagner 0,5 °C (porte ouverte, radiateur trop faible) : la fenêtre repart, rien n'est appris.
    /// </summary>
    private void Apprendre(ThermostatSettings t, Runtime rt, double ambiante, double? exterieur, DateTimeOffset maintenant)
    {
        if (rt.Puissance < 0.9)
        {
            rt.PenteDebut = null;
            return;
        }

        if (rt.PenteDebut is not { } debut)
        {
            rt.PenteDebut = maintenant;
            rt.PenteAmbiante = ambiante;
            return;
        }

        var gain = ambiante - rt.PenteAmbiante;
        var minutes = (maintenant - debut).TotalMinutes;
        if (gain >= 0.5 && minutes >= 5)
        {
            var apprise = Preheat.Learn(_pentes.GetValueOrDefault(t.Id), minutes, gain, exterieur);
            _pentes[t.Id] = apprise;
            _pentesAEnregistrer[t.Id] = apprise;
        }
        else if (minutes <= 120)
        {
            return;
        }

        rt.PenteDebut = maintenant;
        rt.PenteAmbiante = ambiante;
    }

    /// <summary>
    /// Pose ou lève un motif d'inhibition propre au moteur. Poser coupe aussitôt (vérifié, retenté par
    /// l'inhibiteur) ; une fois posé, rien n'est réémis à chaque tick.
    /// </summary>
    private async Task AppliquerMotifAsync(ThermostatSettings t, string motif, bool actif, CancellationToken ct)
    {
        var pose = inhibitor.GetReasons(t.Id).Contains(motif);
        if (actif && !pose) await inhibitor.InhibitAsync(t.Id, t.SwitchEntityIds, motif, ct);
        else if (!actif && pose) inhibitor.Lift(t.Id, motif);
    }

    private static void SuivreOuverture(ThermostatSettings t, Runtime rt, bool ouvert, DateTimeOffset maintenant)
    {
        if (ouvert && !rt.OuvertureVue)
        {
            rt.OuvertureVue = true;
            rt.OuvertDepuis = maintenant;
            rt.FermeDepuis = null;
        }
        else if (!ouvert && rt.OuvertureVue)
        {
            rt.OuvertureVue = false;
            rt.FermeDepuis = maintenant;
            rt.OuvertDepuis = null;
        }

        if (rt.OuvertureVue && !rt.FenetreSuspendue && maintenant - rt.OuvertDepuis >= TimeSpan.FromMinutes(t.OpeningDelayMinutes))
            rt.FenetreSuspendue = true;

        if (!rt.OuvertureVue && rt.FenetreSuspendue && maintenant - rt.FermeDepuis >= TimeSpan.FromMinutes(t.ClosingDelayMinutes))
            rt.FenetreSuspendue = false;
    }

    /// <summary>
    /// Envoie l'état voulu aux radiateurs s'il diffère du dernier ordre ACCEPTÉ (ou si celui-ci est inconnu).
    /// Seul un ordre accepté par tous les radiateurs devient l'état connu : sinon on renvoie au tick suivant.
    /// </summary>
    private async Task PiloterAsync(ThermostatSettings t, Runtime rt, bool chauffer, CancellationToken ct)
    {
        if (rt.RelaisConnu == chauffer) return;

        var commande = chauffer ? StandardCommands.TurnOn : StandardCommands.TurnOff;
        var issues = await Task.WhenAll(t.SwitchEntityIds.Select(async id =>
        {
            try
            {
                return await commands.SendAsync(new EntityCommand(id, commande) { Origin = $"thermostat:{t.Id}" }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CommandOutcome.Failed;
            }
        }));

        rt.RelaisConnu = issues.All(i => i == CommandOutcome.Accepted) ? chauffer : null;
    }

    private async Task<IReadOnlyList<string>> RadiateursInconnusAsync(ThermostatSettings t, CancellationToken ct)
    {
        var inconnus = new List<string>();
        foreach (var id in t.SwitchEntityIds)
            if (await registry.GetAsync(id, ct) is not { Missing: false }) inconnus.Add(id);
        return inconnus;
    }
}
