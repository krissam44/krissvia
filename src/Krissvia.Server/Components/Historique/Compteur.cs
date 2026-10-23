using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Historique;

/// <summary>
/// Un compteur de l'écran Électricité : son index en kWh, ses index partiels (heures pleines / creuses d'un Linky), sa
/// puissance (W, ou VA pour un Linky) et sa tension, si l'appareil les publie.
/// </summary>
public sealed record Compteur(string Nom, RegisteredEntity Index, IReadOnlyList<RegisteredEntity> Partiels,
    RegisteredEntity? Puissance, RegisteredEntity? Tension)
{
    /// <summary>
    /// Les compteurs de l'installation. Un appareil qui a un index « total » (Linky : index total, index 1, index 2) est
    /// UN compteur, ses autres index en sont les parts ; sinon chaque index est un compteur (les deux voies d'un Shelly EM),
    /// avec la puissance de la même voie — celle dont l'identifiant matériel ressemble le plus au sien.
    /// </summary>
    public static List<Compteur> Trouver(IReadOnlyList<RegisteredEntity> entites, IReadOnlyList<RegisteredDevice> appareils)
    {
        var actives = entites.Where(e => e is { Enabled: true, Missing: false } && e.Kind == EntityKind.Sensor).ToList();
        var compteurs = new List<Compteur>();
        foreach (var groupe in actives.Where(e => e.DeviceClass == DeviceClasses.Energy).GroupBy(e => e.DeviceId))
        {
            var appareil = appareils.FirstOrDefault(d => d.DeviceId == groupe.Key);
            var memeAppareil = actives.Where(e => e.DeviceId == groupe.Key).ToList();
            var puissances = memeAppareil.Where(e => e.DeviceClass == DeviceClasses.Power).ToList();
            if (puissances.Count == 0) puissances = [.. memeAppareil.Where(e => e.DeviceClass == DeviceClasses.ApparentPower)];
            var tension = memeAppareil.FirstOrDefault(e => e.DeviceClass == DeviceClasses.Voltage);
            var index = groupe.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList();
            var nomAppareil = appareil?.DisplayName ?? index[0].DisplayName;

            if (index.FirstOrDefault(e => e.DisplayName.Contains("total", StringComparison.OrdinalIgnoreCase)) is { } total)
            {
                compteurs.Add(new Compteur(nomAppareil, total, [.. index.Where(e => e != total)], puissances.FirstOrDefault(), tension));
                continue;
            }

            foreach (var e in index)
                compteurs.Add(new Compteur(index.Count > 1 ? e.DisplayName : nomAppareil, e, [],
                    puissances.OrderByDescending(p => PrefixeCommun(p.UniqueId, e.UniqueId)).FirstOrDefault(), tension));
        }

        return [.. compteurs.OrderBy(c => c.Nom, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static int PrefixeCommun(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }
}
