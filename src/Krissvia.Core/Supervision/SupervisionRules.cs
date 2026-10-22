using System.Globalization;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Core.Supervision;

public enum SupervisionCategory { Internet, Plugin, Presence, Disk, Sensor, Battery }

/// <summary>L'état d'un plugin activé, tel que la supervision le voit.</summary>
public sealed record PluginHealth(string Id, string Name, bool Connected);

/// <summary>Ce que la supervision regarde, à un instant.</summary>
/// <param name="Plugins">Les plugins ACTIVÉS (un plugin désactivé exprès n'est pas une panne).</param>
/// <param name="PresenceLost">Détection de présence perdue ; null : aucun appareil suivi, rien à surveiller.</param>
/// <param name="DiskFreeBytes">Espace libre à côté de la base ; null : inconnu.</param>
/// <param name="Zone">Le fuseau des heures écrites dans les messages ; null : celui du serveur.</param>
public sealed record SupervisionInput(
    DateTimeOffset Now,
    IReadOnlyList<RegisteredEntity> Entities,
    IEntityStateReader States,
    IReadOnlyList<PluginHealth> Plugins,
    bool? PresenceLost,
    long? DiskFreeBytes,
    TimeZoneInfo? Zone = null);

/// <summary>Un point surveillé, en panne ou non.</summary>
/// <param name="Key">La clé de l'alerte : un incident par clé.</param>
/// <param name="Problem">En panne, au sens de la règle (le délai de grâce s'applique ensuite).</param>
/// <param name="Since">Depuis quand, quand la donnée le dit ; sinon le premier constat de la supervision.</param>
/// <param name="GraceMinutes">Durée de panne avant l'alerte : une box qui redémarre n'est pas une coupure.</param>
public sealed record SupervisionItem(
    string Key, SupervisionCategory Category, string Name, bool Problem, string Detail,
    DateTimeOffset? Since = null, int GraceMinutes = 0);

/// <summary>
/// Les règles de la supervision (familles d'alertes de DomoMud), en fonction pure : ce qui est en panne maintenant.
/// Le délai de grâce, les alertes et leur fin sont l'affaire de <see cref="SupervisionWatch"/>.
/// </summary>
public static class SupervisionRules
{
    /// <summary>
    /// Les capteurs dont le SILENCE dit une panne : ils publient d'eux-mêmes, régulièrement. Un interrupteur, une
    /// lumière ou un bouton ne parlent qu'à un changement — deux heures de silence y sont normales, et DomoMud noyait
    /// les vraies alertes sous celles-là.
    /// </summary>
    private static readonly HashSet<string> ClassesBavardes = new(StringComparer.Ordinal)
    {
        DeviceClasses.Temperature, DeviceClasses.Humidity, DeviceClasses.Power, DeviceClasses.ApparentPower,
        DeviceClasses.Pressure, DeviceClasses.Illuminance, DeviceClasses.CarbonDioxide,
        DeviceClasses.Opening, DeviceClasses.Occupancy,
    };

    /// <summary>Marge de sortie d'une alerte de pile : une valeur qui oscille autour du seuil ne clignote pas.</summary>
    public const int BatteryHysteresis = 5;

    public static IReadOnlyList<SupervisionItem> Evaluate(SupervisionInput entree, Notifications.AlertSettings r, bool capteursEnRepit)
    {
        var items = new List<SupervisionItem>();
        var plugins = entree.Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);

        // Les entités d'un plugin arrêté se taisent toutes : l'alerte du plugin les couvre, une par capteur noierait tout.
        bool PluginEnPanne(RegisteredEntity e) => plugins.TryGetValue(e.IntegrationId, out var p) && !p.Connected;
        var entites = entree.Entities.Where(e => e is { Enabled: true, Missing: false } && !CoreIntegrations.IsCore(e.IntegrationId)).ToList();

        if (r.Plugins.Enabled)
        {
            foreach (var p in entree.Plugins.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                items.Add(new SupervisionItem($"plugin:{p.Id}", SupervisionCategory.Plugin, p.Name, !p.Connected,
                    p.Connected ? "connecté" : "activé mais pas connecté au cœur", GraceMinutes: r.Plugins.GraceMinutes));
        }

        if (r.Internet.Enabled)
        {
            foreach (var e in entites.Where(e => e is { Kind: EntityKind.BinarySensor, DeviceClass: DeviceClasses.Connectivity }))
            {
                // Indisponible : c'est le plugin qui ne répond plus, pas la connexion. Son alerte à lui le dit.
                if (entree.States.Get(e.EntityId) is not { } s || !s.TryGetBool(out var connecte)) continue;
                items.Add(new SupervisionItem($"internet:{e.EntityId}", SupervisionCategory.Internet, e.DisplayName, !connecte,
                    connecte ? "connexion établie" : "connexion coupée", GraceMinutes: r.Internet.GraceMinutes));
            }
        }

        if (r.Presence.Enabled && entree.PresenceLost is { } perdue)
            items.Add(new SupervisionItem("presence", SupervisionCategory.Presence, "Détection de présence", perdue,
                perdue ? "plus aucune réponse des appareils suivis : maison tenue pour occupée par sécurité" : "en service"));

        if (r.Disk.Enabled && entree.DiskFreeBytes is { } libre)
        {
            var mo = libre / (1024 * 1024);
            items.Add(new SupervisionItem("disque", SupervisionCategory.Disk, "Espace disque", mo < r.Disk.MinFreeMegabytes,
                $"{Taille(libre)} libres à côté de la base (seuil {Taille(r.Disk.MinFreeMegabytes * 1024L * 1024)})"));
        }

        if (r.Sensors.Enabled && !capteursEnRepit)
        {
            var seuil = TimeSpan.FromMinutes(Math.Max(1, r.Sensors.SilentMinutes));
            foreach (var e in entites.Where(e => e.DeviceClass is { } c && ClassesBavardes.Contains(c) && e.Kind is EntityKind.Sensor or EntityKind.BinarySensor))
            {
                if (PluginEnPanne(e)) continue;
                // Jamais entendu depuis qu'il existe : on ne conclut rien (pas de faux positif au premier démarrage).
                if (entree.States.Get(e.EntityId) is not { } s) continue;
                var muet = entree.Now - s.MeasuredAt > seuil;
                var probleme = muet || !s.Available;
                items.Add(new SupervisionItem($"capteur:{e.EntityId}", SupervisionCategory.Sensor, e.DisplayName, probleme,
                    !s.Available ? $"injoignable, dernière mesure {Heure(s.MeasuredAt, entree.Zone)}"
                    : muet ? $"muet depuis {Heure(s.MeasuredAt, entree.Zone)}" : "émet", s.MeasuredAt));
            }
        }

        if (r.Battery.Enabled)
        {
            foreach (var e in entites.Where(e => e.DeviceClass == DeviceClasses.Battery))
            {
                if (entree.States.Get(e.EntityId) is not { Available: true } s) continue;
                if (e.Kind == EntityKind.Sensor && s.TryGetNumber(out var pct))
                {
                    // Entre le seuil et le seuil + marge : on garde l'état actuel (ni levée, ni fin).
                    if (pct > r.Battery.ThresholdPercent && pct <= r.Battery.ThresholdPercent + BatteryHysteresis) continue;
                    items.Add(new SupervisionItem($"pile:{e.EntityId}", SupervisionCategory.Battery, e.DisplayName,
                        pct <= r.Battery.ThresholdPercent, $"pile à {pct.ToString("0", CultureInfo.InvariantCulture)} %"));
                }
                else if (e.Kind == EntityKind.BinarySensor && s.TryGetBool(out var faible))
                {
                    items.Add(new SupervisionItem($"pile:{e.EntityId}", SupervisionCategory.Battery, e.DisplayName, faible,
                        faible ? "pile faible" : "pile correcte"));
                }
            }
        }

        return items;
    }

    public static string Heure(DateTimeOffset instant, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Local).ToString("dd/MM HH:mm", CultureInfo.GetCultureInfo("fr-FR"));

    private static string Taille(long octets) => octets >= 1024L * 1024 * 1024
        ? $"{(octets / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.GetCultureInfo("fr-FR"))} Go"
        : $"{octets / (1024 * 1024)} Mo";
}
