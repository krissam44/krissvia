using System.Text.Json;
using System.Text.Json.Serialization;
using Krissvia.Core.Configuration;

namespace Krissvia.Climate.Presence;

/// <summary>
/// Une absence programmée : du départ au retour. Persistée, car un redémarrage en pleine absence ne doit ni la perdre
/// ni réarmer le piège de l'annulation (<see cref="DepartureSeen"/>).
/// </summary>
/// <param name="CancelOnPresence">Rentrer plus tôt que prévu annule la période : on ne rentre pas dans le froid.</param>
/// <param name="Cancelled">Annulée par un retour anticipé ; gardée jusqu'à la date de retour, pour le dire à l'écran.</param>
/// <param name="DepartureSeen">
/// La maison a été vue vide pendant la période. On programme son absence depuis son salon : sans ce constat, la
/// présence du moment annulerait la période au calcul suivant. Un retour anticipé suppose d'être parti d'abord.
/// </param>
public sealed record ScheduledAbsence(
    DateTimeOffset Departure,
    DateTimeOffset Return,
    bool CancelOnPresence = true,
    bool Cancelled = false,
    bool DepartureSeen = false)
{
    /// <summary>En cours maintenant : commencée, pas encore finie, pas annulée.</summary>
    public bool IsActive(DateTimeOffset maintenant) => !Cancelled && maintenant >= Departure && maintenant < Return;

    /// <summary>
    /// La période après un nouveau calcul de présence (fonction pure) : null une fois la date de retour passée, sinon
    /// le départ constaté, puis l'annulation sur retour anticipé.
    /// </summary>
    /// <remarks>
    /// L'annulation exige quelqu'un de réellement <b>présent</b>. Une détection perdue rend la maison « occupée » par
    /// sécurité (<see cref="HomePresence"/>), mais une box injoignable au milieu des vacances n'est pas un retour : elle
    /// annulerait la période et ferait chauffer la maison vide jusqu'à la date prévue.
    /// </remarks>
    public ScheduledAbsence? Evolve(HomePresenceState? presence, DateTimeOffset maintenant)
    {
        if (maintenant >= Return) return null;
        if (!IsActive(maintenant) || presence is null) return this;

        if (!DepartureSeen) return presence is { Occupied: false } ? this with { DepartureSeen = true } : this;
        return CancelOnPresence && presence.Present.Count > 0 ? this with { Cancelled = true } : this;
    }
}

/// <summary>Qui décide de l'absence en ce moment : pour l'afficher sans ambiguïté.</summary>
public enum AbsenceSource
{
    /// <summary>Personne : la maison est occupée.</summary>
    None,

    /// <summary>La détection de présence : tous les appareils suivis sont partis.</summary>
    Detection,

    /// <summary>Une période programmée en cours.</summary>
    Scheduled,

    /// <summary>Forcée à la main, présente ou absente : prend le dessus sur tout.</summary>
    Manual,
}

/// <summary>L'absence du foyer, telle que la voient les thermostats.</summary>
/// <param name="Return">Retour prévu, quand une période programmée est en cours : la remontée en température le vise.</param>
public sealed record HomeAbsenceState(bool Away, AbsenceSource Source, DateTimeOffset? Return)
{
    public static readonly HomeAbsenceState Home = new(false, AbsenceSource.None, null);

    /// <summary>
    /// La hiérarchie de DomoMud : forçage manuel, puis période programmée, puis détection de présence. Sans détection
    /// encore calculée, la maison est occupée : on ne prétend pas savoir qu'elle est vide.
    /// </summary>
    public static HomeAbsenceState Compute(bool? forcee, ScheduledAbsence? periode, HomePresenceState? presence, DateTimeOffset maintenant)
    {
        var retour = periode is { } p && p.IsActive(maintenant) ? p.Return : (DateTimeOffset?)null;
        if (forcee is { } absente) return new HomeAbsenceState(absente, AbsenceSource.Manual, absente ? retour : null);
        if (retour is not null) return new HomeAbsenceState(true, AbsenceSource.Scheduled, retour);
        return presence is { Occupied: false } ? new HomeAbsenceState(true, AbsenceSource.Detection, null) : Home;
    }
}

/// <summary>Ce que le moteur des thermostats lit de l'absence.</summary>
public interface IHomeAbsence
{
    HomeAbsenceState Current { get; }
}

/// <summary>Configuration refusée, avec la raison à montrer telle quelle.</summary>
public sealed class AbsenceValidationException(string message) : Exception(message);

/// <summary>
/// L'absence du foyer : la période programmée et le forçage manuel (enregistrés), combinés à la présence détectée
/// (<see cref="HomePresence"/>). Les thermostats qui suivent l'absence passent alors dans leur mode d'absence, et
/// remontent en température avant le retour programmé.
/// </summary>
public sealed class AbsenceService(IConfigurationStore store, TimeProvider clock) : IHomeAbsence
{
    public const string Collection = "absence";
    public const string DocumentId = "maison";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Document(ScheduledAbsence? Period, bool? Forced);

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private bool _charge;
    private HomePresenceState? _presence;

    public ScheduledAbsence? Period { get; private set; }

    /// <summary>Forçage manuel : true absente, false présente, null automatique.</summary>
    public bool? Forced { get; private set; }

    public HomeAbsenceState Current { get; private set; } = HomeAbsenceState.Home;

    /// <summary>L'absence, la période ou le forçage ont changé. Levé hors verrou.</summary>
    public event Action? Changed;

    /// <summary>Lit l'enregistrement ; sans effet après la première fois. Avant le premier tick des thermostats.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (_charge) return;
            var document = (await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId);
            if (document is not null && JsonSerializer.Deserialize<Document>(document.Json, Json) is { } d)
            {
                Period = d.Period;
                Forced = d.Forced;
            }

            _charge = true;
            Current = HomeAbsenceState.Compute(Forced, Period, _presence, clock.GetUtcNow());
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Programme une absence, en remplaçant la précédente.</summary>
    public Task ScheduleAsync(DateTimeOffset depart, DateTimeOffset retour, bool annulerSiPresence, CancellationToken ct = default)
    {
        if (retour <= depart) throw new AbsenceValidationException("le retour doit suivre le départ.");
        if (retour <= clock.GetUtcNow()) throw new AbsenceValidationException("le retour est déjà passé.");
        return ModifierAsync(() => Period = new ScheduledAbsence(depart, retour, annulerSiPresence), ct);
    }

    /// <summary>Supprime la période programmée.</summary>
    public Task ClearAsync(CancellationToken ct = default) => ModifierAsync(() => Period = null, ct);

    /// <summary>Force présente (false), absente (true), ou rend la main à la période et à la détection (null).</summary>
    public Task ForceAsync(bool? absente, CancellationToken ct = default) => ModifierAsync(() => Forced = absente, ct);

    /// <summary>Après chaque calcul de présence : fait vivre la période (départ constaté, annulation, fin).</summary>
    public Task UpdateAsync(HomePresenceState presence, CancellationToken ct = default) => ModifierAsync(() =>
    {
        _presence = presence;
        Period = Period?.Evolve(presence, clock.GetUtcNow());
    }, ct);

    private async Task ModifierAsync(Action modifier, CancellationToken ct)
    {
        await LoadAsync(ct);
        bool change;
        await _verrou.WaitAsync(ct);
        try
        {
            var (periode, forcee, avant) = (Period, Forced, Current);
            modifier();
            if (Period != periode || Forced != forcee)
                await store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(new Document(Period, Forced), Json)), ct);

            Current = HomeAbsenceState.Compute(Forced, Period, _presence, clock.GetUtcNow());
            change = Period != periode || Forced != forcee || Current != avant;
        }
        finally
        {
            _verrou.Release();
        }

        if (change) Changed?.Invoke();
    }
}
