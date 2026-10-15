using Krissvia.Contracts;
using Krissvia.Core.Commands;

namespace Krissvia.Climate;

/// <summary>Motifs d'inhibition connus. Chaîne ouverte : un module peut en ajouter.</summary>
public static class InhibitionReasons
{
    public const string Delestage = "delestage";
    public const string FenetreOuverte = "fenetre_ouverte";
    public const string SondePerdue = "sonde_perdue";
}

/// <summary>Issue d'une coupure, par switch : ce qu'a répondu le plugin, et ce qu'a constaté le cœur.</summary>
public sealed record CutResult(IReadOnlyDictionary<string, VerifiedCommandResult> Results)
{
    public IReadOnlyDictionary<string, CommandOutcome> Outcomes =>
        Results.ToDictionary(kv => kv.Key, kv => kv.Value.Outcome, StringComparer.Ordinal);

    /// <summary>
    /// Vrai si au moins un switch a été coupé et que TOUS le sont : coupure confirmée par l'état de l'entité.
    /// Un ordre seulement accepté ne suffit pas (ARCHITECTURE.md §7.3). Un thermostat sans switch à couper
    /// n'est pas une coupure réussie non plus : il n'économise aucune puissance.
    /// </summary>
    public bool FullyCut => Results.Count > 0 && Results.Values.All(Coupures.EstCoupe);
}

/// <summary>Ce qui fait d'un ordre de coupure une coupure acquise.</summary>
public static class Coupures
{
    /// <summary>Délai d'attente de l'état confirmant une coupure.</summary>
    public static readonly TimeSpan DelaiDeVerification = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Confirmée par un état publié après l'ordre ; ou acceptée sur une entité dont l'état ne dit rien de la
    /// marche (rien à vérifier, on ne peut pas faire mieux).
    /// </summary>
    public static bool EstCoupe(VerifiedCommandResult r) =>
        r.Verification == CommandVerification.Confirmed ||
        (r.Outcome == CommandOutcome.Accepted && r.Verification == CommandVerification.NotApplicable);

    /// <summary>Coupure vérifiée, priorité sécurité. Une exception du bus devient un échec ; l'annulation se propage.</summary>
    public static async Task<VerifiedCommandResult> CouperAsync(
        ICommandBus commandes, string entityId, string origine, TimeSpan delai, CancellationToken ct)
    {
        try
        {
            return await commandes.SendVerifiedAsync(
                new EntityCommand(entityId, StandardCommands.TurnOff) { Priority = CommandPriority.Safety, Origin = origine },
                delai, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new VerifiedCommandResult(CommandOutcome.Failed, CommandVerification.NotSent);
        }
    }
}

/// <summary>
/// Inhibitions des thermostats : tant qu'un motif est posé sur un thermostat, il ne chauffe pas.
/// Mécanisme commun au délestage, à la fenêtre ouverte et à la sonde perdue.
///
/// Règles (ARCHITECTURE.md §7 et §8) :
/// <list type="bullet">
/// <item>l'inhibition est posée AVANT d'émettre la coupure, sinon le prochain tick du thermostat peut
/// renvoyer un ordre ON entre les deux ;</item>
/// <item>la coupure part sans condition, même si le thermostat est déjà inhibé : l'état interne n'est
/// qu'une croyance, c'est le relais qui consomme ;</item>
/// <item>un switch n'est considéré coupé que lorsque son nouvel état CONFIRME la coupure (§7.3) : un ordre
/// accepté dont l'effet n'est pas constaté laisse le switch « en attente », retenté par
/// <see cref="RetryPendingAsync"/> ;</item>
/// <item>la consigne, le mode et le planning du thermostat ne sont jamais touchés ;</item>
/// <item>l'état vit en mémoire, il n'est pas persisté : au redémarrage, c'est le module qui décide
/// (délestage : la mesure) qui re-pose ses inhibitions.</item>
/// </list>
/// </summary>
public sealed class ThermostatInhibitor(ICommandBus commandes, TimeSpan? delaiDeVerification = null)
{
    private readonly TimeSpan _delai = delaiDeVerification ?? Coupures.DelaiDeVerification;

    private readonly object _verrou = new();
    private readonly Dictionary<string, Inhibition> _inhibitions = new(StringComparer.Ordinal);

    private sealed class Inhibition
    {
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);

        /// <summary>Switches dont la coupure n'a pas encore été acceptée.</summary>
        public HashSet<string> PendingSwitches { get; } = new(StringComparer.Ordinal);
    }

    public bool IsInhibited(string thermostatId)
    {
        lock (_verrou)
        {
            return _inhibitions.TryGetValue(thermostatId, out var inhibition) && inhibition.Reasons.Count > 0;
        }
    }

    public IReadOnlyCollection<string> GetReasons(string thermostatId)
    {
        lock (_verrou)
        {
            return _inhibitions.TryGetValue(thermostatId, out var inhibition)
                ? inhibition.Reasons.Order(StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    public IReadOnlyCollection<string> GetPendingSwitches(string thermostatId)
    {
        lock (_verrou)
        {
            return _inhibitions.TryGetValue(thermostatId, out var inhibition)
                ? inhibition.PendingSwitches.Order(StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    /// <summary>Pose l'inhibition puis coupe les switches (en parallèle, priorité sécurité).</summary>
    public async Task<CutResult> InhibitAsync(
        string thermostatId,
        IReadOnlyList<string> switchEntityIds,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thermostatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(switchEntityIds);

        var cibles = switchEntityIds.Distinct(StringComparer.Ordinal).ToList();

        // 1. L'inhibition d'abord, les coupures en attente avec elle.
        lock (_verrou)
        {
            if (!_inhibitions.TryGetValue(thermostatId, out var inhibition))
            {
                inhibition = new Inhibition();
                _inhibitions[thermostatId] = inhibition;
            }

            inhibition.Reasons.Add(reason);
            foreach (var id in cibles) inhibition.PendingSwitches.Add(id);
        }

        // 2. Puis la coupure, sans condition.
        var issues = await CutAsync(cibles, reason, ct);

        // 3. Seul un ordre accepté solde la coupure d'un switch.
        lock (_verrou)
        {
            if (_inhibitions.TryGetValue(thermostatId, out var inhibition))
            {
                foreach (var (id, resultat) in issues)
                {
                    if (Coupures.EstCoupe(resultat)) inhibition.PendingSwitches.Remove(id);
                }
            }
        }

        return new CutResult(issues);
    }

    /// <summary>
    /// Retire un motif. Renvoie vrai si le thermostat reste inhibé par un AUTRE motif.
    /// Quand le dernier motif part, les coupures en attente sont abandonnées : on ne coupe plus un
    /// thermostat qui a repris.
    /// </summary>
    public bool Lift(string thermostatId, string reason)
    {
        lock (_verrou)
        {
            if (!_inhibitions.TryGetValue(thermostatId, out var inhibition)) return false;

            inhibition.Reasons.Remove(reason);
            if (inhibition.Reasons.Count > 0) return true;

            _inhibitions.Remove(thermostatId);
            return false;
        }
    }

    /// <summary>
    /// Renvoie la coupure des switches dont l'ordre n'a pas été accepté. À appeler périodiquement par le
    /// moteur. Renvoie le nombre de switches encore en attente.
    /// </summary>
    public async Task<int> RetryPendingAsync(CancellationToken ct = default)
    {
        List<(string ThermostatId, string EntityId)> aRetenter;
        lock (_verrou)
        {
            aRetenter = _inhibitions
                .SelectMany(kv => kv.Value.PendingSwitches.Select(e => (ThermostatId: kv.Key, EntityId: e)))
                .ToList();
        }

        if (aRetenter.Count == 0) return 0;

        var issues = await Task.WhenAll(aRetenter.Select(async c =>
            (c.ThermostatId, c.EntityId, Resultat: await Coupures.CouperAsync(commandes, c.EntityId, "inhibition:relance", _delai, ct))));

        lock (_verrou)
        {
            foreach (var (thermostatId, entityId, resultat) in issues)
            {
                if (Coupures.EstCoupe(resultat) && _inhibitions.TryGetValue(thermostatId, out var inhibition))
                    inhibition.PendingSwitches.Remove(entityId);
            }

            return _inhibitions.Values.Sum(i => i.PendingSwitches.Count);
        }
    }

    private async Task<Dictionary<string, VerifiedCommandResult>> CutAsync(IReadOnlyCollection<string> entityIds, string motif, CancellationToken ct)
    {
        var envois = entityIds.Select(async id =>
            (Id: id, Resultat: await Coupures.CouperAsync(commandes, id, $"inhibition:{motif}", _delai, ct))).ToList();
        var issues = await Task.WhenAll(envois);
        return issues.ToDictionary(i => i.Id, i => i.Resultat, StringComparer.Ordinal);
    }
}
