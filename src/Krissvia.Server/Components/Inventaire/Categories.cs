using System.Globalization;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;

namespace Krissvia.Server.Components.Inventaire;

/// <summary>Une catégorie d'entités : filtre de l'écran, libellé du type, icône et couleur (palette de DomoMud).</summary>
internal sealed record Categorie(string Cle, string Libelle, string Icone, string Couleur);

/// <summary>Classement et présentation des entités : une seule source pour le tableau, les filtres et le dialogue.</summary>
internal static class Categories
{
    public static readonly Categorie Temperature = new("temperature", "Température", Icons.Material.Filled.Thermostat, "#66bb6a");
    public static readonly Categorie Humidite = new("humidite", "Humidité", Icons.Material.Filled.WaterDrop, "#26c6da");
    public static readonly Categorie Interrupteur = new("switch", "Interrupteur", Icons.Material.Filled.ToggleOn, "#ffa726");
    public static readonly Categorie Lumiere = new("lumiere", "Lumière", Icons.Material.Filled.Lightbulb, "#ffd60a");
    public static readonly Categorie Prise = new("prise", "Prise", Icons.Material.Filled.Power, "#29b6f6");
    public static readonly Categorie Compteur = new("compteur", "Compteur", Icons.Material.Filled.ElectricMeter, "#ffb300");
    public static readonly Categorie Ouverture = new("ouverture", "Ouverture", Icons.Material.Filled.DoorFront, "#42a5f5");
    public static readonly Categorie Mouvement = new("mouvement", "Mouvement", Icons.Material.Filled.DirectionsRun, "#ab47bc");
    public static readonly Categorie Puissance = new("puissance", "Puissance / énergie", Icons.Material.Filled.Bolt, "#ffb300");
    public static readonly Categorie Batterie = new("batterie", "Batterie", Icons.Material.Filled.BatteryStd, "#8d6e63");
    public static readonly Categorie Evenement = new("evenement", "Bouton / événement", Icons.Material.Filled.RadioButtonChecked, "#ce93d8");
    public static readonly Categorie Thermostat = new("thermostat", "Thermostat", Icons.Material.Filled.DeviceThermostat, "#ef5350");
    public static readonly Categorie Radiateur = new("radiateur", "Radiateur", Icons.Material.Filled.Whatshot, "#ff7043");
    public static readonly Categorie Meteo = new("meteo", "Météo", Icons.Material.Filled.WbSunny, "#ffb300");
    public static readonly Categorie Presence = new("presence", "Présence", Icons.Material.Filled.Person, "#4caf50");
    public static readonly Categorie Reseau = new("reseau", "Réseau / Internet", Icons.Material.Filled.Router, "#26a69a");
    public static readonly Categorie Probleme = new("probleme", "Problème", Icons.Material.Filled.ReportProblem, "#ef5350");
    public static readonly Categorie Machine = new("machine", "Machine / serveur", Icons.Material.Filled.Dns, "#5c6bc0");
    public static readonly Categorie Autre = new("autre", "Autre mesure", Icons.Material.Filled.Sensors, "#78909c");

    /// <summary>
    /// Un nœud Proxmox (<c>DeviceClasses.HypervisorNode</c>, ajouté par la PR du plugin) : écrit en clair ici pour que
    /// les deux changements ne se marchent pas dessus.
    /// </summary>
    private const string NoeudHyperviseur = "hypervisor_node";

    /// <summary>L'ordre des pastilles de filtre.</summary>
    public static readonly Categorie[] Toutes = [Thermostat, Radiateur, Temperature, Humidite, Interrupteur, Prise, Compteur, Lumiere, Ouverture, Mouvement, Puissance, Batterie, Evenement, Presence, Reseau, Meteo, Probleme, Machine, Autre];

    public static Categorie De(RegisteredEntity e) => e.Kind switch
    {
        EntityKind.Switch when e.Usage == EntityUsages.Radiator => Radiateur,
        EntityKind.Switch when e.Usage == EntityUsages.Lighting => Lumiere,
        EntityKind.Switch when e.Usage == EntityUsages.Plug => Prise,
        EntityKind.Switch when e.Usage == EntityUsages.Meter => Compteur,
        EntityKind.Switch => Interrupteur,
        EntityKind.Light => Lumiere,
        EntityKind.Event => Evenement,
        EntityKind.Climate => Thermostat,
        EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Occupancy => Mouvement,
        EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Presence => Presence,
        EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Connectivity => Reseau,
        // Un « arrêt inattendu », une sous-tension : rien à voir avec une porte, alors que c'était le classement par défaut.
        EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Problem => Probleme,
        EntityKind.BinarySensor => Ouverture,
        _ => e.DeviceClass switch
        {
            DeviceClasses.Temperature => Temperature,
            DeviceClasses.Humidity => Humidite,
            DeviceClasses.Power or DeviceClasses.ApparentPower or DeviceClasses.Energy
                or DeviceClasses.Voltage or DeviceClasses.Current or DeviceClasses.LoadShedding => Puissance,
            DeviceClasses.Battery => Batterie,
            DeviceClasses.Weather => Meteo,
            DeviceClasses.HomePresence => Presence,
            DeviceClasses.PublicIp or DeviceClasses.DataRate or DeviceClasses.InternetConnection or DeviceClasses.NetworkHosts => Reseau,
            DeviceClasses.VirtualMachine or DeviceClasses.SystemHealth or DeviceClasses.DeviceTemperature or NoeudHyperviseur => Machine,
            _ => Autre,
        },
    };

    /// <summary>La valeur du moment, lisible : « 19,4 °C », « Allumé », « Ouvert »…</summary>
    public static string Valeur(RegisteredEntity e, EntityStateSnapshot? etat)
    {
        if (etat is null) return "—";
        if (!etat.Available) return "indisponible";

        var v = etat.Value;
        if (etat.TryGetBool(out var b))
            return e.Kind switch
            {
                EntityKind.Switch => b ? "Allumé" : "Éteint",
                EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Occupancy => b ? "Présence" : "Rien",
                EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Presence => b ? "Présent" : "Absent",
                EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Connectivity => b ? "En ligne" : "Hors ligne",
                EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Problem => b ? "Problème" : "OK",
                EntityKind.BinarySensor => b ? "Ouvert" : "Fermé",
                _ => b ? "Oui" : "Non",
            };
        if (etat.TryGetNumber(out var n))
            return n.ToString(Math.Abs(n) >= 100 ? "#,0" : "0.##", CultureInfo.GetCultureInfo("fr-FR")) + (e.Unit is { Length: > 0 } u ? $" {u}" : "");
        if (e.DeviceClass == DeviceClasses.NetworkHosts && v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("hosts", out var hotes) && hotes.ValueKind == JsonValueKind.Array)
            return $"{hotes.GetArrayLength()} appareils, {hotes.EnumerateArray().Count(h => Vrai(h, "active"))} connectés";
        if (e.DeviceClass == DeviceClasses.InternetConnection && v.ValueKind == JsonValueKind.Object)
            return string.Join(" · ", new[]
            {
                Vrai(v, "up") ? "en ligne" : "hors ligne",
                Texte(v, "public_ip"),
                v.TryGetProperty("down_kbps", out var d) && d.ValueKind == JsonValueKind.Number ? $"↓ {d.GetDouble() / 1000:0.#} Mbit/s" : null,
            }.Where(x => x is not null));
        if (e.DeviceClass == DeviceClasses.HomePresence && v.ValueKind == JsonValueKind.Object)
            return Vrai(v, "detection_lost") ? "occupée (détection perdue)"
                : Vrai(v, "occupied") ? "occupée" : "vide";
        if (e.DeviceClass == DeviceClasses.LoadShedding && v.ValueKind == JsonValueKind.Object)
            return string.Join(" · ", new[]
            {
                Nombre(v, "power", " VA"),
                v.TryGetProperty("shedding", out var d) && d.ValueKind == JsonValueKind.True ? "délestage en cours" : null,
            }.Where(x => x is not null));
        if (e.DeviceClass == DeviceClasses.Weather && v.ValueKind == JsonValueKind.Object)
            return string.Join(" · ", new[] { Nombre(v, "temperature", " °C"), Texte(v, "condition") }.Where(x => x is not null));
        if (e.Kind == EntityKind.Climate && v.ValueKind == JsonValueKind.Object)
            return string.Join(" · ", new[] { Nombre(v, "temperature", " °C"), Texte(v, "status_label") }.Where(x => x is not null));
        if (e.DeviceClass == DeviceClasses.VirtualMachine && v.ValueKind == JsonValueKind.Object)
            return Vrai(v, "unexpected_stop") ? "Arrêt inattendu"
                : Texte(v, "status") != "running" ? "Arrêtée"
                : string.Join(" · ", new[] { "En marche", Nombre(v, "cpu_percent", " % CPU"), Nombre(v, "memory_percent", " % RAM") }.Where(x => x is not null));
        if (e.DeviceClass is DeviceClasses.SystemHealth or NoeudHyperviseur && v.ValueKind == JsonValueKind.Object)
            return v.TryGetProperty("online", out var enLigne) && enLigne.ValueKind == JsonValueKind.False ? "Hors ligne"
                : string.Join(" · ", new[] { Nombre(v, "cpu_percent", " % CPU"), Nombre(v, "memory_percent", " % RAM"), Nombre(v, "disk_percent", " % disque") }
                    .Where(x => x is not null)) is { Length: > 0 } resume ? resume : "—";
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return on.GetBoolean() ? "Allumée" : "Éteinte";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        // Un objet qu'on ne sait pas résumer : sa carte du tableau de bord le montre, le JSON brut n'aide personne ici.
        if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return "données détaillées (voir sa carte)";
        return v.ToString();
    }

    private static string? Nombre(JsonElement objet, string propriete, string unite) =>
        objet.TryGetProperty(propriete, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble().ToString("0.0", CultureInfo.GetCultureInfo("fr-FR")) + unite
            : null;

    private static bool Vrai(JsonElement objet, string propriete) =>
        objet.TryGetProperty(propriete, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Texte(JsonElement objet, string propriete) =>
        objet.TryGetProperty(propriete, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
