using Krissvia.Climate.LoadShedding;
using Krissvia.Contracts;
using Krissvia.Core.Commands;

namespace Krissvia.Climate.DeviceSchedules;

/// <summary>Ce que montre une programmation : son état voulu, s'il a été appliqué, et son prochain changement.</summary>
public sealed record DeviceScheduleSnapshot(string Id, bool Desired, bool Applied, DateTime? NextChange);

/// <summary>
/// Moteur des programmations. Il n'agit qu'aux CHANGEMENTS de créneau (6:00 allume, 8:00 éteint) : un allumage à la
/// main tient jusqu'au changement suivant, comme dans Jeedom ou Home Assistant.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Au démarrage, et après toute modification d'une programmation, l'état attendu est appliqué une fois :
/// sinon un redémarrage à 6:30 laisserait la prise éteinte jusqu'au lendemain.</item>
/// <item>Chaque ordre est vérifié (§7.3) ; non confirmé, il est renvoyé au passage suivant, jusqu'à confirmation.</item>
/// <item>Un switch coupé par un délestage n'est jamais rallumé ici : l'état voulu est publié dans
/// <see cref="DesiredSwitchStates"/>, et c'est le rétablissement du délesteur qui le remettra — allumé ou non.</item>
/// <item>Pas de rattrapage d'un créneau manqué serveur arrêté : seul l'état du moment est appliqué au redémarrage.</item>
/// </list>
/// </remarks>
public sealed class DeviceScheduleEngine(ICommandBus commander, LoadShedder shedder, DesiredSwitchStates desired, TimeProvider clock)
{
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private IReadOnlyList<DeviceSchedule> _programmations = [];
    private readonly Dictionary<string, Runtime> _runtimes = new(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, DeviceScheduleSnapshot> _instantanes = new Dictionary<string, DeviceScheduleSnapshot>();

    private sealed class Runtime(DeviceSchedule configuration)
    {
        public DeviceSchedule Configuration { get; } = configuration;

        /// <summary>Dernier état envoyé ; null tant que rien ne l'a été (démarrage, modification).</summary>
        public bool? Applique;

        public bool Confirme;
    }

    public DeviceScheduleSnapshot? GetSnapshot(string id) => _instantanes.GetValueOrDefault(id);

    public async Task ConfigureAsync(IReadOnlyList<DeviceSchedule> programmations, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var presentes = programmations.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, rt) in _runtimes.Where(kv => !presentes.Contains(kv.Key)).ToList())
            {
                foreach (var s in rt.Configuration.SwitchEntityIds) desired.Clear(s);
                _runtimes.Remove(id);
            }

            foreach (var p in programmations)
            {
                // Une programmation modifiée repart de zéro : son état attendu est réappliqué au passage suivant.
                if (_runtimes.TryGetValue(p.Id, out var rt) && ReferenceEquals(rt.Configuration, p)) continue;
                if (rt is not null)
                    foreach (var s in rt.Configuration.SwitchEntityIds.Except(p.SwitchEntityIds)) desired.Clear(s);
                _runtimes[p.Id] = new Runtime(p);
            }

            _programmations = programmations.ToList();
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Un passage (toutes les 15 s en production, et tout de suite après une modification).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var maintenant = clock.GetLocalNow().DateTime;
            var instantanes = new Dictionary<string, DeviceScheduleSnapshot>(StringComparer.Ordinal);

            foreach (var p in _programmations)
            {
                var rt = _runtimes[p.Id];
                if (!p.Enabled)
                {
                    foreach (var s in p.SwitchEntityIds) desired.Clear(s);
                    rt.Applique = null;
                    continue;
                }

                var voulu = p.StateAt(maintenant);
                foreach (var s in p.SwitchEntityIds) desired.Set(s, voulu);

                // Ni changement de créneau, ni ordre à reprendre : un geste manuel reste tel quel.
                if (rt.Applique != voulu || !rt.Confirme)
                {
                    rt.Confirme = await AppliquerAsync(p, voulu, ct);
                    rt.Applique = voulu;
                }

                instantanes[p.Id] = new DeviceScheduleSnapshot(p.Id, voulu, rt.Confirme, p.NextChange(maintenant));
            }

            _instantanes = instantanes;
        }
        finally
        {
            _verrou.Release();
        }
    }

    private async Task<bool> AppliquerAsync(DeviceSchedule p, bool allumer, CancellationToken ct)
    {
        var commande = allumer ? StandardCommands.TurnOn : StandardCommands.TurnOff;
        var tousConfirmes = true;
        foreach (var s in p.SwitchEntityIds)
        {
            // Délesté : le rétablissement s'en chargera, dans l'état voulu à ce moment-là.
            if (allumer && shedder.IsSwitchShed(s)) continue;

            var resultat = await commander.SendVerifiedAsync(new EntityCommand(s, commande) { Origin = $"programmation:{p.Id}" }, ct: ct);
            tousConfirmes &= resultat.Verification == CommandVerification.Confirmed;
        }

        return tousConfirmes;
    }
}
