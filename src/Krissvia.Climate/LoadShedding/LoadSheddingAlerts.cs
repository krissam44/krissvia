using System.Globalization;
using Krissvia.Core.Notifications;

namespace Krissvia.Climate.LoadShedding;

/// <summary>
/// Les alertes du délestage, après chaque tick :
/// <list type="bullet">
/// <item><b>Délestage en cours</b> (DomoMud) : tant qu'un équipement est coupé, avec la puissance et la liste ; « terminé »
/// quand tout est rétabli.</item>
/// <item><b>Coupure non confirmée</b>, par équipement : le délesteur a coupé mais aucun état ne le confirme — l'appareil
/// consomme peut-être encore, et le disjoncteur peut sauter malgré le délestage.</item>
/// <item><b>Délestage sans mesure</b> au-delà de <see cref="BlindGrace"/> : sans puissance fraîche, il ne décide plus
/// rien (D6), et cette protection disparaît en silence au moment où on en a besoin.</item>
/// </list>
/// </summary>
/// <remarks>
/// L'épisode se lit sur la liste des équipements coupés, pas sur la bascule du tick : après un redémarrage en plein
/// délestage, la bascule est passée mais l'alerte doit être là. Quarantaine de <see cref="EpisodeQuarantine"/> entre deux
/// épisodes : une soirée de cuisine ne fait pas dix alertes.
/// </remarks>
public sealed class LoadSheddingAlerts(AlertService alerts, TimeProvider clock)
{
    public const string EpisodeKey = "delestage";
    public const string BlindKey = "delestage-aveugle";
    public const string CutKeyPrefix = "delestage-coupure:";

    public static readonly TimeSpan EpisodeQuarantine = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BlindGrace = TimeSpan.FromMinutes(5);

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");
    private DateTimeOffset? _aveugleDepuis;

    public async Task OnTickAsync(LoadSheddingStatus etat, IReadOnlyList<SheddableDeviceConfiguration> equipements, CancellationToken ct = default)
    {
        var reglages = await alerts.GetSettingsAsync(ct);
        if (!reglages.LoadShedding.Enabled || !etat.Enabled)
        {
            // Plus surveillé : oublié sans message, ce n'est pas un retour à la normale.
            foreach (var a in alerts.Active.Where(a => a.Key == EpisodeKey || a.Key == BlindKey || a.Key.StartsWith(CutKeyPrefix, StringComparison.Ordinal)))
                alerts.Dismiss(a.Key);
            _aveugleDepuis = null;
            return;
        }

        string Nom(string id) => equipements.FirstOrDefault(e => e.Id == id) is { Name.Length: > 0 } e ? e.Name : id;

        if (etat.ShedDevices.Count > 0)
        {
            var mesure = etat.Power is { } p ? $"Puissance {VA(p)} au-dessus du seuil ({VA(etat.AlertThreshold)}). " : "";
            await alerts.RaiseAsync(EpisodeKey, new Notification("Délestage en cours",
                $"{mesure}Coupé : {string.Join(", ", etat.ShedDevices.Select(Nom))}.", 4, ["electric_plug"]), EpisodeQuarantine, ct);
        }
        else
        {
            var mesure = etat.Power is { } p ? $"Puissance revenue à {VA(p)} : " : "";
            await alerts.ResolveAsync(EpisodeKey, new Notification("Délestage terminé", $"{mesure}tous les équipements sont rétablis.", 2, ["white_check_mark"]), ct);
        }

        foreach (var id in etat.UnconfirmedDevices)
            await alerts.RaiseAsync(CutKeyPrefix + id, new Notification($"Coupure non confirmée : {Nom(id)}",
                $"Le délestage a coupé {Nom(id)} mais aucun état ne le confirme : l'appareil consomme peut-être encore. Nouvel essai à chaque tick.",
                5, ["warning"]), ct: ct);

        foreach (var a in alerts.Active.Where(a => a.Key.StartsWith(CutKeyPrefix, StringComparison.Ordinal)).ToList())
        {
            var id = a.Key[CutKeyPrefix.Length..];
            if (etat.UnconfirmedDevices.Contains(id)) continue;
            await alerts.ResolveAsync(a.Key, etat.ShedDevices.Contains(id)
                ? new Notification($"Coupure confirmée : {Nom(id)}", $"{Nom(id)} est bien coupé.", 2, ["white_check_mark"])
                : new Notification($"{Nom(id)} rétabli", $"{Nom(id)} n'est plus délesté.", 2, ["white_check_mark"]), ct);
        }

        var maintenant = clock.GetUtcNow();
        if (etat.SourceUnavailable)
        {
            var depuis = _aveugleDepuis ??= etat.MeasuredAt is { } m && m < maintenant ? m : maintenant;
            if (maintenant - depuis >= BlindGrace)
                await alerts.RaiseAsync(BlindKey, new Notification("Délestage sans mesure",
                    $"Aucune mesure de puissance fraîche depuis {TimeZoneInfo.ConvertTime(depuis, clock.LocalTimeZone):HH:mm} : le délestage ne peut plus rien décider.",
                    4, ["warning"]), ct: ct);
        }
        else
        {
            _aveugleDepuis = null;
            await alerts.ResolveAsync(BlindKey, new Notification("Délestage : mesure retrouvée", "Le délestage reçoit de nouveau la puissance.", 2, ["white_check_mark"]), ct);
        }
    }

    private static string VA(double valeur) => $"{valeur.ToString("N0", Francais)} VA";
}
