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
    public static readonly Categorie Ouverture = new("ouverture", "Ouverture", Icons.Material.Filled.DoorFront, "#42a5f5");
    public static readonly Categorie Mouvement = new("mouvement", "Mouvement", Icons.Material.Filled.DirectionsRun, "#ab47bc");
    public static readonly Categorie Puissance = new("puissance", "Puissance / énergie", Icons.Material.Filled.Bolt, "#ffb300");
    public static readonly Categorie Batterie = new("batterie", "Batterie", Icons.Material.Filled.BatteryStd, "#8d6e63");
    public static readonly Categorie Evenement = new("evenement", "Bouton / événement", Icons.Material.Filled.RadioButtonChecked, "#ce93d8");
    public static readonly Categorie Autre = new("autre", "Autre mesure", Icons.Material.Filled.Sensors, "#78909c");

    /// <summary>L'ordre des pastilles de filtre.</summary>
    public static readonly Categorie[] Toutes = [Temperature, Humidite, Interrupteur, Lumiere, Ouverture, Mouvement, Puissance, Batterie, Evenement, Autre];

    public static Categorie De(RegisteredEntity e) => e.Kind switch
    {
        EntityKind.Switch => Interrupteur,
        EntityKind.Light => Lumiere,
        EntityKind.Event => Evenement,
        EntityKind.BinarySensor when e.DeviceClass == DeviceClasses.Occupancy => Mouvement,
        EntityKind.BinarySensor => Ouverture,
        _ => e.DeviceClass switch
        {
            DeviceClasses.Temperature => Temperature,
            DeviceClasses.Humidity => Humidite,
            DeviceClasses.Power or DeviceClasses.ApparentPower or DeviceClasses.Energy
                or DeviceClasses.Voltage or DeviceClasses.Current => Puissance,
            DeviceClasses.Battery => Batterie,
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
                EntityKind.BinarySensor => b ? "Ouvert" : "Fermé",
                _ => b ? "Oui" : "Non",
            };
        if (etat.TryGetNumber(out var n))
            return n.ToString(Math.Abs(n) >= 100 ? "#,0" : "0.##", CultureInfo.GetCultureInfo("fr-FR")) + (e.Unit is { Length: > 0 } u ? $" {u}" : "");
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return on.GetBoolean() ? "Allumée" : "Éteinte";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        return v.ToString();
    }
}
