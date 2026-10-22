using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Core.Commands;
using Krissvia.Core.Configuration;
using Krissvia.Core.History;
using Krissvia.Core.Registry;

namespace Krissvia.Climate.Thermostats;

/// <summary>Les trois coefficients qu'ajuste l'auto-TPI.</summary>
public sealed record TpiGains(double Kp, double Ki, double Kint)
{
    public static TpiGains Of(TpiSettings t) => new(t.Kp, t.Ki, t.Kint);
}

/// <summary>
/// Le dernier calcul de l'auto-TPI d'un thermostat : le modèle identifié, le réglage proposé (null si le modèle n'est pas
/// fiable), le réglage en place au moment du calcul, et celui d'avant la dernière application (pour revenir en arrière).
/// </summary>
public sealed record AutoTpiReport(
    DateTimeOffset ComputedAt,
    HeatingType HeatingType,
    ThermalIdentification Model,
    TpiProposal? Proposal,
    TpiGains Current,
    TpiGains? Previous = null,
    DateTimeOffset? AppliedAt = null)
{
    /// <summary>Le réglage proposé est-il déjà en place ?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProposalInPlace => Proposal is { } p && new TpiGains(p.Kp, p.Ki, p.Kint) == Current;
}

/// <summary>
/// Auto-TPI, choix de l'utilisateur du 22/10/2026 : <b>proposé, jamais appliqué seul</b>. Pour chaque thermostat qui l'a
/// demandé (<see cref="ThermostatSettings.AutoTpi"/>), la pièce est identifiée sur 14 jours d'historique ; le réglage
/// qui en découle est proposé, et ne s'applique que par <see cref="ApplyAsync"/> (bouton « Appliquer »). Le réglage
/// remplacé est gardé : <see cref="RevertAsync"/> le remet.
/// </summary>
/// <remarks>
/// Les comptes rendus vivent dans leur propre collection (<c>thermostat_autotpi</c>), comme les pentes du préchauffage :
/// un calcul n'écrit jamais dans les réglages, que seul l'utilisateur modifie.
/// </remarks>
public sealed class AutoTpiService(
    ThermostatService thermostats, IEntityRegistry registry, IHistoryRepository history, IConfigurationStore store, TimeProvider clock)
{
    public const string Collection = "thermostat_autotpi";
    public static readonly TimeSpan Fenetre = TimeSpan.FromDays(14);

    /// <summary>Un calcul par jour environ : l'heure du premier calcul devient l'heure habituelle.</summary>
    public static readonly TimeSpan Periode = TimeSpan.FromHours(20);

    private readonly ConcurrentDictionary<string, AutoTpiReport> _rapports = new(StringComparer.Ordinal);

    public AutoTpiReport? Get(string thermostatId) => _rapports.GetValueOrDefault(thermostatId);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var document in await store.ListAsync(Collection, ct))
        {
            try
            {
                if (JsonSerializer.Deserialize<AutoTpiReport>(document.Json, ThermostatService.Json) is { } r) _rapports[document.Id] = r;
            }
            catch (JsonException)
            {
                // Illisible : le prochain calcul le refait.
            }
        }
    }

    /// <summary>Identifie la pièce sur les 14 derniers jours et propose un réglage. Ne change rien à la régulation.</summary>
    public async Task<AutoTpiReport> ComputeAsync(string thermostatId, CancellationToken ct = default)
    {
        var t = thermostats.Get(thermostatId) ?? throw new ThermostatValidationException($"thermostat « {thermostatId} » inconnu");
        var maintenant = clock.GetUtcNow();
        var depuis = maintenant - Fenetre;

        ThermalIdentification modele;
        if (await registry.FindByUniqueIdAsync(CoreIntegrations.Thermostats, t.Id, ct) is not { } entite)
            modele = Refus("le thermostat n'est pas encore publié en entité : attendre le prochain passage du moteur");
        else if (t.OutdoorTemperatureEntityId is null)
            modele = Refus("pas de sonde extérieure : sans elle, les déperditions ne se mesurent pas (onglet Général)");
        else
        {
            var interieur = await history.ReadAsync(HistoryValues.Serie(entite.EntityId, "temperature"), depuis, maintenant, ct);
            var exterieur = await history.ReadAsync(HistoryValues.Serie(entite.EntityId, "outdoor_temperature"), depuis, maintenant, ct);
            var chauffe = await history.ReadAsync(HistoryValues.Serie(entite.EntityId, "heating"), depuis, maintenant, ct);
            modele = ThermalModel.Identify(ThermalIntervals.Build(interieur, exterieur, chauffe, depuis, maintenant));
        }

        var precedent = Get(thermostatId);
        var rapport = new AutoTpiReport(maintenant, t.HeatingType, modele,
            modele.Usable ? ThermalModel.Derive(modele, t.HeatingType) : null,
            TpiGains.Of(t.Tpi), precedent?.Previous, precedent?.AppliedAt);
        await EnregistrerAsync(thermostatId, rapport, ct);
        return rapport;
    }

    /// <summary>Les thermostats qui ont demandé l'auto-TPI et dont le dernier calcul date de plus de <see cref="Periode"/>.</summary>
    public async Task<int> ComputeDueAsync(CancellationToken ct = default)
    {
        var faits = 0;
        foreach (var t in thermostats.List().Where(t => t.AutoTpi))
        {
            if (Get(t.Id) is { } r && clock.GetUtcNow() - r.ComputedAt < Periode) continue;
            await ComputeAsync(t.Id, ct);
            faits++;
        }

        return faits;
    }

    /// <summary>Met en place le réglage proposé ; celui qu'il remplace est gardé pour <see cref="RevertAsync"/>.</summary>
    public async Task<ThermostatSettings> ApplyAsync(string thermostatId, CancellationToken ct = default)
    {
        var t = thermostats.Get(thermostatId) ?? throw new ThermostatValidationException($"thermostat « {thermostatId} » inconnu");
        if (Get(thermostatId) is not { Proposal: { } p } rapport)
            throw new ThermostatValidationException("aucun réglage proposé : le modèle n'est pas encore fiable");

        var avant = TpiGains.Of(t.Tpi);
        var applique = await thermostats.SaveAsync(t with { Tpi = t.Tpi with { Kp = p.Kp, Ki = p.Ki, Kint = p.Kint } }, ct);
        await EnregistrerAsync(thermostatId, rapport with
        {
            Current = TpiGains.Of(applique.Tpi),
            // Appliquer deux fois de suite ne doit pas faire oublier le réglage d'origine.
            Previous = rapport.AppliedAt is not null && rapport.Previous is { } origine ? origine : avant,
            AppliedAt = clock.GetUtcNow(),
        }, ct);
        return applique;
    }

    /// <summary>Remet le réglage d'avant la dernière application.</summary>
    public async Task<ThermostatSettings> RevertAsync(string thermostatId, CancellationToken ct = default)
    {
        var t = thermostats.Get(thermostatId) ?? throw new ThermostatValidationException($"thermostat « {thermostatId} » inconnu");
        if (Get(thermostatId) is not { Previous: { } avant } rapport)
            throw new ThermostatValidationException("aucun réglage précédent à remettre");

        var remis = await thermostats.SaveAsync(t with { Tpi = t.Tpi with { Kp = avant.Kp, Ki = avant.Ki, Kint = avant.Kint } }, ct);
        await EnregistrerAsync(thermostatId, rapport with { Current = avant, Previous = null, AppliedAt = null }, ct);
        return remis;
    }

    /// <summary>Active ou coupe l'auto-TPI et règle le type de chauffage, sans toucher au reste des réglages.</summary>
    public async Task<ThermostatSettings> SetOptionsAsync(string thermostatId, bool actif, HeatingType type, CancellationToken ct = default)
    {
        var t = thermostats.Get(thermostatId) ?? throw new ThermostatValidationException($"thermostat « {thermostatId} » inconnu");
        return await thermostats.SaveAsync(t with { AutoTpi = actif, HeatingType = type }, ct);
    }

    private static ThermalIdentification Refus(string motif) => new(0, 0, 0, 0, 0, 0, motif);

    private async Task EnregistrerAsync(string id, AutoTpiReport rapport, CancellationToken ct)
    {
        await store.SaveAsync(Collection, new ConfigurationDocument(id, JsonSerializer.Serialize(rapport, ThermostatService.Json)), ct);
        _rapports[id] = rapport;
    }
}
