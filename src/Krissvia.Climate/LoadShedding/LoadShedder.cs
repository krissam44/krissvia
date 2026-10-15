using Krissvia.Climate;
using Krissvia.Contracts;
using Krissvia.Core.Commands;

namespace Krissvia.Climate.LoadShedding;

/// <summary>
/// Moteur de délestage. Reprend l'algorithme de DomoMud (TESTS-CARACTERISATION.md, D1 à D12) avec les
/// corrections décidées :
/// <list type="bullet">
/// <item>D4 : après une action, aucune nouvelle décision avant une mesure postérieure à l'effet attendu ;</item>
/// <item>D5 : un seul rétablissement à la fois, et seulement si la puissance mesurée plus la puissance
/// nominale de l'équipement reste sous le seuil d'alerte (le premier équipement éligible qui tient) ;</item>
/// <item>D6 : une mesure absente ou périmée n'est jamais lue comme zéro : le délesteur ne décide rien et
/// signale <see cref="LoadSheddingTick.SourceUnavailable"/> ;</item>
/// <item>D7 : une coupure dont l'ordre n'est pas accepté ne compte pas comme économie, l'équipement suivant
/// est délesté, la coupure est retentée à chaque tick ;</item>
/// <item>D8 : un équipement retiré pendant un délestage est rétabli ;</item>
/// <item>D11 : un équipement lié à un thermostat est coupé par une inhibition immédiate.</item>
/// </list>
/// </summary>
/// <remarks>
/// Un seul appelant de <see cref="TickAsync"/> à la fois est attendu (la boucle du moteur) ;
/// <see cref="ConfigureAsync"/> peut venir de l'interface. Les deux se sérialisent sur un verrou qui n'est
/// jamais imbriqué ni tenu pendant la levée d'un événement.
/// </remarks>
public sealed class LoadShedder(IEntityCommander commander, ThermostatInhibitor inhibitor, TimeProvider clock)
{
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private readonly Dictionary<string, DeviceState> _etats = new(StringComparer.Ordinal);

    private LoadSheddingSettings _reglages = LoadSheddingSettings.Disabled;
    private IReadOnlyList<SheddableDevice> _equipements = [];
    private DateTimeOffset? _derniereAction;
    private int _nbDelestesPrecedent;

    private sealed class DeviceState(DateTimeOffset delesteLe, bool confirme)
    {
        public DateTimeOffset DelesteLe { get; } = delesteLe;

        /// <summary>Vrai quand la coupure a été acceptée ; faux tant qu'elle est retentée.</summary>
        public bool Confirme { get; set; } = confirme;
    }

    // ── Configuration ────────────────────────────────────────────────────────

    public async Task ConfigureAsync(
        LoadSheddingSettings settings,
        IReadOnlyList<SheddableDevice> devices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);

        if (settings.Enabled && settings.RestoreThreshold >= settings.AlertThreshold)
            throw new ArgumentException("Le seuil de restauration doit être inférieur au seuil d'alerte.", nameof(settings));

        if (devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != devices.Count)
            throw new ArgumentException("Deux équipements ont le même identifiant.", nameof(devices));

        await _verrou.WaitAsync(ct);
        try
        {
            var presents = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

            // D8 : un équipement retiré (ou désactivé) pendant un délestage est rétabli, sinon son
            // thermostat resterait inhibé ou son switch coupé sans que plus rien ne s'en occupe.
            foreach (var id in _etats.Keys.Where(k => !presents.Contains(k)).ToList())
            {
                var ancien = _equipements.FirstOrDefault(d => d.Id == id);
                if (ancien is not null) await RetablirAsync(ancien, ct);
                _etats.Remove(id);
            }

            _reglages = settings;
            _equipements = devices.ToList();
        }
        finally
        {
            _verrou.Release();
        }
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    public async Task<LoadSheddingTick> TickAsync(PowerReading? reading, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            return await TickSousVerrouAsync(reading, ct);
        }
        finally
        {
            _verrou.Release();
        }
    }

    private async Task<LoadSheddingTick> TickSousVerrouAsync(PowerReading? reading, CancellationToken ct)
    {
        var maintenant = clock.GetUtcNow();
        var delestes = new List<string>();
        var retablis = new List<string>();

        // D9 : délesteur désactivé, tout est rétabli (et retenté tant que ça échoue).
        if (!_reglages.Enabled)
        {
            await RetablirToutAsync(retablis, ct);
            _derniereAction = null;
            return Terminer(delestes, retablis, sourceIndisponible: false);
        }

        await RetenterCoupuresAsync(ct);

        // D6 : jamais « mesure absente = 0 ».
        if (reading is not { } mesure || maintenant - mesure.MeasuredAt > _reglages.MaxReadingAge)
            return Terminer(delestes, retablis, sourceIndisponible: true);

        // D4 : la mesure doit être postérieure à l'effet de la dernière action.
        var enAttente = _derniereAction is { } derniere && mesure.MeasuredAt < derniere + _reglages.SettleDelay;

        if (!enAttente)
        {
            if (mesure.Value > _reglages.AlertThreshold)
                await DelesterAsync(mesure.Value, maintenant, delestes, ct);
            else if (mesure.Value < _reglages.RestoreThreshold)
                await RetablirUnAsync(mesure.Value, maintenant, retablis, ct);
            // Entre les deux seuils (D3) : rien ne bouge.
        }

        return Terminer(delestes, retablis, sourceIndisponible: false);
    }

    // ── Délestage ────────────────────────────────────────────────────────────

    private async Task DelesterAsync(double valeur, DateTimeOffset maintenant, List<string> delestes, CancellationToken ct)
    {
        // Comme DomoMud : on vise à repasser sous le seuil de RESTAURATION.
        var excedent = valeur - _reglages.RestoreThreshold;
        var economie = 0.0;

        // OrderBy est stable : à priorité égale, l'ordre de la liste fournie est conservé.
        foreach (var equipement in _equipements.Where(e => !_etats.ContainsKey(e.Id)).OrderBy(e => e.Priority))
        {
            if (economie >= excedent) break;

            var confirme = await CouperAsync(equipement, ct);
            _etats[equipement.Id] = new DeviceState(maintenant, confirme);
            delestes.Add(equipement.Id);

            // D7 : seule une coupure acceptée compte comme économie.
            if (confirme) economie += equipement.NominalPower;
        }

        if (delestes.Count > 0) _derniereAction = maintenant;
    }

    private Task<bool> CouperAsync(SheddableDevice equipement, CancellationToken ct) =>
        equipement.ThermostatId is { } thermostat
            ? CouperThermostatAsync(thermostat, equipement, ct)
            : EnvoyerATousAsync(equipement.SwitchEntityIds, StandardCommands.TurnOff, CommandPriority.Safety, ct);

    private async Task<bool> CouperThermostatAsync(string thermostat, SheddableDevice equipement, CancellationToken ct)
    {
        var resultat = await inhibitor.InhibitAsync(thermostat, equipement.SwitchEntityIds, InhibitionReasons.Delestage, ct);
        return resultat.FullyCut;
    }

    private async Task RetenterCoupuresAsync(CancellationToken ct)
    {
        var nonConfirmes = _equipements
            .Where(e => _etats.TryGetValue(e.Id, out var etat) && !etat.Confirme)
            .ToList();
        if (nonConfirmes.Count == 0) return;

        await inhibitor.RetryPendingAsync(ct);

        foreach (var equipement in nonConfirmes)
        {
            var confirme = equipement.ThermostatId is { } thermostat
                ? equipement.SwitchEntityIds.Count > 0 && inhibitor.GetPendingSwitches(thermostat).Count == 0
                : await EnvoyerATousAsync(equipement.SwitchEntityIds, StandardCommands.TurnOff, CommandPriority.Safety, ct);

            if (confirme) _etats[equipement.Id].Confirme = true;
        }
    }

    // ── Rétablissement ───────────────────────────────────────────────────────

    /// <summary>D2 et D5 : priorité décroissante, délai écoulé, marge sous le seuil d'alerte, un seul à la fois.</summary>
    private async Task RetablirUnAsync(double valeur, DateTimeOffset maintenant, List<string> retablis, CancellationToken ct)
    {
        foreach (var equipement in _equipements.Where(e => _etats.ContainsKey(e.Id)).OrderByDescending(e => e.Priority))
        {
            if (maintenant - _etats[equipement.Id].DelesteLe < equipement.RestoreDelay) continue;
            if (valeur + equipement.NominalPower > _reglages.AlertThreshold) continue;

            if (!await RetablirAsync(equipement, ct)) continue;

            _etats.Remove(equipement.Id);
            retablis.Add(equipement.Id);
            _derniereAction = maintenant;
            return;
        }
    }

    private async Task RetablirToutAsync(List<string> retablis, CancellationToken ct)
    {
        foreach (var equipement in _equipements.Where(e => _etats.ContainsKey(e.Id)).ToList())
        {
            if (!await RetablirAsync(equipement, ct)) continue;

            _etats.Remove(equipement.Id);
            retablis.Add(equipement.Id);
        }
    }

    /// <summary>Lever l'inhibition ne peut pas échouer ; un ordre de remise sous tension, si : il sera retenté.</summary>
    private async Task<bool> RetablirAsync(SheddableDevice equipement, CancellationToken ct)
    {
        if (equipement.ThermostatId is { } thermostat)
        {
            // Seul le motif « délestage » est levé : une fenêtre ouverte ou une sonde perdue continue de couper.
            inhibitor.Lift(thermostat, InhibitionReasons.Delestage);
            return true;
        }

        return await EnvoyerATousAsync(equipement.SwitchEntityIds, StandardCommands.TurnOn, CommandPriority.Normal, ct);
    }

    // ── Utilitaires ──────────────────────────────────────────────────────────

    private async Task<bool> EnvoyerATousAsync(
        IReadOnlyList<string> entityIds, string commande, CommandPriority priorite, CancellationToken ct)
    {
        if (entityIds.Count == 0) return false;

        var envois = entityIds.Select(async id =>
        {
            try
            {
                return await commander.SendAsync(id, commande, priorite, null, ct) == CommandOutcome.Accepted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        });

        var resultats = await Task.WhenAll(envois);
        return resultats.All(ok => ok);
    }

    private LoadSheddingTick Terminer(List<string> delestes, List<string> retablis, bool sourceIndisponible)
    {
        var nombre = _etats.Count;
        var transition = _nbDelestesPrecedent == 0 && nombre > 0 ? ShedTransition.Started
            : _nbDelestesPrecedent > 0 && nombre == 0 ? ShedTransition.Ended
            : ShedTransition.None;
        _nbDelestesPrecedent = nombre;

        var nonConfirmes = _etats
            .Where(kv => !kv.Value.Confirme)
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new LoadSheddingTick(delestes, retablis, nonConfirmes, transition, sourceIndisponible);
    }
}
