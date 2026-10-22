namespace Krissvia.Climate.Alarm;

/// <summary>Les états de la centrale.</summary>
public enum AlarmMode
{
    Disarmed,

    /// <summary>Décompte de sortie : on quitte les lieux, les détections sont normales.</summary>
    ExitDelay,

    Armed,

    /// <summary>Décompte d'entrée : un capteur temporisé a détecté, le code est attendu.</summary>
    EntryDelay,

    /// <summary>Intrusion constatée. L'état reste jusqu'au désarmement, même les sonneries finies.</summary>
    Triggered,
}

/// <summary>Le rôle d'un capteur dans l'alarme.</summary>
public enum SensorRole
{
    /// <summary>Déclenche aussitôt.</summary>
    Immediate,

    /// <summary>Ouvre la temporisation d'entrée : la porte par laquelle on rentre. Jamais une fenêtre.</summary>
    Delayed,

    /// <summary>Contrôlé à l'armement (resté ouvert, muet), mais ne déclenche jamais.</summary>
    FaultOnly,
}

/// <summary>L'état de la centrale. <c>default</c> vaut désarmée.</summary>
/// <param name="Countdown">
/// Secondes restantes du décompte en cours ; en <see cref="AlarmMode.Triggered"/>, de la sonnerie ou de la pause.
/// </param>
/// <param name="Sensor">Le capteur qui a ouvert l'entrée ou déclenché.</param>
/// <param name="Ring">En <see cref="AlarmMode.Triggered"/> : n° de la sonnerie en cours (1, 2…), 0 une fois les sonneries finies.</param>
/// <param name="Ringing">La sirène sonne (sinon : pause entre deux sonneries).</param>
public readonly record struct AlarmState(AlarmMode Mode, int Countdown, DateTimeOffset? TriggeredAt, string? Sensor, int Ring = 0, bool Ringing = false)
{
    public static readonly AlarmState Disarmed = default;
}

/// <summary>Ce qui arrive à la centrale.</summary>
public abstract record AlarmEvent
{
    public sealed record Arm(string User) : AlarmEvent;

    public sealed record Disarm(string User) : AlarmEvent;

    /// <summary>Une seconde s'est écoulée.</summary>
    public sealed record Second : AlarmEvent;

    /// <summary>Un capteur est passé au repos → actif.</summary>
    public sealed record Detection(string Sensor, bool Delayed) : AlarmEvent;
}

public enum AlarmOrderType
{
    /// <summary>Le nouvel état est à publier (écran, entité de l'alarme).</summary>
    State,

    /// <summary>Le résultat du contrôle des défauts, « OK » ou la liste.</summary>
    Faults,

    /// <summary>Sirènes en marche (« 1 ») ou à l'arrêt (« 0 »).</summary>
    Siren,

    Notification,
    Journal,
}

/// <summary>Un ordre à exécuter par le moteur : la machine ne parle à personne elle-même.</summary>
public readonly record struct AlarmOrder(AlarmOrderType Type, string Value)
{
    public static AlarmOrder State(AlarmState s) => new(AlarmOrderType.State, AlarmMachine.Label(s));
    public static AlarmOrder Faults(string v) => new(AlarmOrderType.Faults, v);
    public static AlarmOrder Siren(bool marche) => new(AlarmOrderType.Siren, marche ? "1" : "0");
    public static AlarmOrder Notification(string v) => new(AlarmOrderType.Notification, v);
    public static AlarmOrder Journal(string v) => new(AlarmOrderType.Journal, v);
}

/// <summary>Les réglages dont la machine a besoin.</summary>
/// <param name="RingSeconds">Durée d'une sonnerie.</param>
/// <param name="RingCount">Nombre de sonneries.</param>
/// <param name="PauseSeconds">Silence entre deux sonneries.</param>
public sealed record AlarmTimings(
    int ExitDelaySeconds, int EntryDelaySeconds, int RingSeconds, int RingCount, int PauseSeconds, bool NotifyArming, bool NotifyTrigger);

/// <summary>
/// Machine à états de l'alarme (DomoMud), en fonction pure : (état, événement, défauts) → (nouvel état, ordres).
/// </summary>
/// <remarks>
/// Elle ignore les entités, les plugins et ntfy : le moteur prélève les défauts et exécute les ordres. C'est ce qui
/// permet de vérifier au banc les scénarios qu'on ne rejoue pas chez soi sans forcer une porte : sirène qui se tait au
/// bout de trois minutes, temporisation d'entrée qui expire, porte restée ouverte en fin de décompte.
/// </remarks>
public static class AlarmMachine
{
    public static (AlarmState State, IReadOnlyList<AlarmOrder> Orders) Process(
        AlarmState etat, AlarmEvent evenement, AlarmTimings reglages, IReadOnlyList<string> defauts, DateTimeOffset maintenant)
    {
        var ordres = new List<AlarmOrder>();
        var suivant = evenement switch
        {
            AlarmEvent.Arm a => Armer(etat, a.User, reglages, defauts, ordres),
            AlarmEvent.Disarm d => Desarmer(etat, d.User, ordres),
            AlarmEvent.Second => Seconde(etat, reglages, defauts, maintenant, ordres),
            AlarmEvent.Detection x => Detection(etat, x, reglages, maintenant, ordres),
            _ => etat,
        };
        return (suivant, ordres);
    }

    private static AlarmState Armer(AlarmState etat, string qui, AlarmTimings r, IReadOnlyList<string> defauts, List<AlarmOrder> ordres)
    {
        // Déjà en route : republier l'état réel. Un clavier qui demande l'armement se croyait désarmé, et le silence
        // laisserait durer la désynchronisation.
        if (etat.Mode != AlarmMode.Disarmed)
        {
            ordres.Add(AlarmOrder.State(etat));
            return etat;
        }

        if (defauts.Count > 0)
        {
            var message = AlarmFaults.Message(defauts);
            ordres.Add(AlarmOrder.Faults(message));
            ordres.Add(AlarmOrder.State(etat));
            ordres.Add(AlarmOrder.Journal($"Armement refusé ({qui}) : {message}"));
            return etat;
        }

        ordres.Add(AlarmOrder.Faults(AlarmFaults.None));
        if (r.ExitDelaySeconds <= 0)
        {
            var arme = new AlarmState(AlarmMode.Armed, 0, null, null);
            ordres.Add(AlarmOrder.State(arme));
            ordres.Add(AlarmOrder.Journal($"Armement immédiat ({qui})"));
            if (r.NotifyArming) ordres.Add(AlarmOrder.Notification("Alarme armée"));
            return arme;
        }

        var sortie = new AlarmState(AlarmMode.ExitDelay, r.ExitDelaySeconds, null, null);
        ordres.Add(AlarmOrder.State(sortie));
        ordres.Add(AlarmOrder.Journal($"Armement demandé ({qui})"));
        return sortie;
    }

    private static AlarmState Desarmer(AlarmState etat, string qui, List<AlarmOrder> ordres)
    {
        // Couper la sirène SANS condition, même si la machine la croit silencieuse : l'état interne n'est qu'une
        // croyance, et c'est le relais qui réveille le voisinage (même raison que la coupure garantie du thermostat).
        ordres.Add(AlarmOrder.Siren(false));
        ordres.Add(AlarmOrder.State(AlarmState.Disarmed));
        ordres.Add(AlarmOrder.Journal(etat.Mode == AlarmMode.Triggered
            ? $"Désarmement après déclenchement ({qui}) : {etat.Sensor}"
            : $"Désarmement ({qui})"));
        return AlarmState.Disarmed;
    }

    private static AlarmState Seconde(AlarmState etat, AlarmTimings r, IReadOnlyList<string> defauts, DateTimeOffset maintenant, List<AlarmOrder> ordres)
    {
        switch (etat.Mode)
        {
            case AlarmMode.ExitDelay:
            {
                if (etat.Countdown > 1)
                {
                    var suite = etat with { Countdown = etat.Countdown - 1 };
                    ordres.Add(AlarmOrder.State(suite));
                    return suite;
                }

                // Le contrôle qui compte : la porte par laquelle on vient de sortir doit être refermée MAINTENANT.
                // Contrôler au départ seulement laisserait armer porte ouverte ; abandonner dès qu'elle bouge
                // empêcherait de sortir.
                if (defauts.Count > 0)
                {
                    var message = AlarmFaults.Message(defauts);
                    ordres.Add(AlarmOrder.Faults(message));
                    ordres.Add(AlarmOrder.State(AlarmState.Disarmed));
                    ordres.Add(AlarmOrder.Journal($"Armement abandonné : {message}"));
                    ordres.Add(AlarmOrder.Notification($"Armement abandonné : {message}"));
                    return AlarmState.Disarmed;
                }

                var arme = new AlarmState(AlarmMode.Armed, 0, null, null);
                ordres.Add(AlarmOrder.Faults(AlarmFaults.None));
                ordres.Add(AlarmOrder.State(arme));
                ordres.Add(AlarmOrder.Journal("Alarme armée"));
                if (r.NotifyArming) ordres.Add(AlarmOrder.Notification("Alarme armée"));
                return arme;
            }

            case AlarmMode.EntryDelay:
            {
                if (etat.Countdown > 1)
                {
                    var suite = etat with { Countdown = etat.Countdown - 1 };
                    ordres.Add(AlarmOrder.State(suite));
                    return suite;
                }

                return Declencher(etat.Sensor ?? "?", r, maintenant, ordres);
            }

            case AlarmMode.Triggered when etat.Ring > 0:
            {
                if (etat.Countdown > 1) return etat with { Countdown = etat.Countdown - 1 };

                // Fin d'une pause : la sonnerie suivante.
                if (!etat.Ringing)
                {
                    ordres.Add(AlarmOrder.Siren(true));
                    return etat with { Ring = etat.Ring + 1, Ringing = true, Countdown = r.RingSeconds };
                }

                // Fin d'une sonnerie : une pause, ou c'était la dernière. Les sonneries finies, l'état reste
                // « déclenchée » : c'est lui qui racontera l'intrusion au retour.
                ordres.Add(AlarmOrder.Siren(false));
                if (etat.Ring < r.RingCount) return etat with { Ringing = false, Countdown = r.PauseSeconds };
                ordres.Add(AlarmOrder.Journal($"Sirène arrêtée ({etat.Ring} sonnerie{(etat.Ring > 1 ? "s" : "")})"));
                return etat with { Ring = 0, Ringing = false, Countdown = 0 };
            }

            default:
                return etat;
        }
    }

    private static AlarmState Detection(AlarmState etat, AlarmEvent.Detection d, AlarmTimings r, DateTimeOffset maintenant, List<AlarmOrder> ordres)
    {
        // Pendant la sortie, tout bouge : on traverse la maison pour sortir. Pendant l'entrée, on tolère de même ce qui
        // bouge jusqu'à l'expiration, sinon le détecteur du couloir rendrait la temporisation inutile. Le compromis
        // habituel des centrales ; un « chemin d'entrée » se distinguerait ici et nulle part ailleurs.
        if (etat.Mode != AlarmMode.Armed) return etat;

        if (d.Delayed && r.EntryDelaySeconds > 0)
        {
            var entree = new AlarmState(AlarmMode.EntryDelay, r.EntryDelaySeconds, null, d.Sensor);
            ordres.Add(AlarmOrder.State(entree));
            ordres.Add(AlarmOrder.Journal($"Entrée temporisée : {d.Sensor}"));
            return entree;
        }

        return Declencher(d.Sensor, r, maintenant, ordres);
    }

    private static AlarmState Declencher(string capteur, AlarmTimings r, DateTimeOffset maintenant, List<AlarmOrder> ordres)
    {
        var declenchee = new AlarmState(AlarmMode.Triggered, Math.Max(1, r.RingSeconds), maintenant, capteur, Ring: 1, Ringing: true);
        ordres.Add(AlarmOrder.State(declenchee));
        ordres.Add(AlarmOrder.Siren(true));
        ordres.Add(AlarmOrder.Journal($"ALARME déclenchée : {capteur}"));
        if (r.NotifyTrigger) ordres.Add(AlarmOrder.Notification($"Alarme déclenchée : {capteur}"));
        return declenchee;
    }

    /// <summary>
    /// Le libellé d'un état, celui des digicodes DomoMud : <c>DISARMED</c>, <c>EXIT_DELAY:12</c>, <c>ARMED</c>,
    /// <c>ENTRY_DELAY:8</c>, <c>TRIGGERED</c>.
    /// </summary>
    public static string Label(AlarmState etat) => etat.Mode switch
    {
        AlarmMode.ExitDelay => $"EXIT_DELAY:{etat.Countdown}",
        AlarmMode.EntryDelay => $"ENTRY_DELAY:{etat.Countdown}",
        AlarmMode.Armed => "ARMED",
        AlarmMode.Triggered => "TRIGGERED",
        _ => "DISARMED",
    };

    /// <summary>
    /// L'état à reprendre après un redémarrage. Les décomptes ne sont pas restaurés : une sortie interrompue redevient
    /// un désarmement (l'armement n'avait pas abouti), une entrée en cours redevient une alarme armée (elle l'était).
    /// Le déclenchement survit, sonneries finies : c'est le souvenir de l'intrusion.
    /// </summary>
    public static AlarmState AfterRestart(AlarmState enregistre) => enregistre.Mode switch
    {
        AlarmMode.ExitDelay => AlarmState.Disarmed,
        AlarmMode.EntryDelay => new AlarmState(AlarmMode.Armed, 0, null, null),
        _ => enregistre with { Countdown = 0, Ring = 0, Ringing = false },
    };
}

/// <summary>
/// Contrôle des défauts, avant l'armement et à la fin du décompte de sortie (DomoMud).
/// </summary>
public static class AlarmFaults
{
    /// <summary>Le résultat quand tout va bien.</summary>
    public const string None = "OK";

    /// <summary>Un capteur au moment du contrôle.</summary>
    /// <param name="Active">Ouvert (contact), voit quelqu'un (détecteur) ; null : pas de valeur (jamais entendu, indisponible).</param>
    /// <param name="ActiveIsFault">
    /// Faux pour un détecteur de mouvement : il ne peut pas être « resté ouvert ». Un radar qui voit la personne taper
    /// son code refuserait l'armement, puis le contrôle de fin de décompte (ces capteurs tiennent leur détection une
    /// bonne minute) : l'alarme serait inarmable sans que rien n'explique pourquoi. Son SILENCE reste contrôlé.
    /// </param>
    public readonly record struct Sensor(string Name, bool? Active, DateTimeOffset? LastUpdate, bool ActiveIsFault = true);

    /// <summary>
    /// Les défauts, dans l'ordre des capteurs. Un capteur muet en est un : un contact Zigbee ne publie qu'au changement
    /// d'état, donc douze heures de silence valent aussi bien « porte fermée » que « pile morte » — et « on ne sait pas »
    /// n'est pas « fermé ».
    /// </summary>
    /// <param name="silenceMinutes">Au-delà, un capteur compte comme muet. 0 désactive ce contrôle.</param>
    public static IReadOnlyList<string> Check(IEnumerable<Sensor> capteurs, DateTimeOffset maintenant, int silenceMinutes)
    {
        var defauts = new List<string>();
        foreach (var c in capteurs)
        {
            if (c.Active == true && c.ActiveIsFault) defauts.Add(c.Name);
            else if (c.Active is null || c.LastUpdate is null) defauts.Add($"{c.Name} (sans nouvelles)");
            else if (silenceMinutes > 0 && maintenant - c.LastUpdate.Value > TimeSpan.FromMinutes(silenceMinutes)) defauts.Add($"{c.Name} (muet)");
        }

        return defauts;
    }

    /// <summary>Les défauts séparés par des virgules, ou « OK ». Jamais tronqué : cacher le capteur qui bloque serait pire.</summary>
    public static string Message(IReadOnlyList<string> defauts) => defauts.Count == 0 ? None : string.Join(", ", defauts);
}
