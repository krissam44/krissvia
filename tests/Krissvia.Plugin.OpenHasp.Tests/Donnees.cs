using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>Les réglages et les états de l'appartement, tels qu'une platine les reçoit.</summary>
internal static class Donnees
{
    public const string Thermostat = "climate.salon";
    public const string Lampe = "light.lampe_salon";
    public const string Prise = "switch.prise_bureau";
    public const string Exterieure = "sensor.terrasse_temperature";

    public static Platine Salon() => new()
    {
        Noeud = "appart1",
        Piece = "Salon",
        Thermostat = Thermostat,
        Interrupteurs = [new Interrupteur { Entite = Lampe, Nom = "Lampe" }, new Interrupteur { Entite = Prise }],
    };

    public static Reglages Reglages(params Platine[] platines) => new()
    {
        Platines = platines.Length > 0 ? [.. platines] : [Salon()],
        Sondes =
        [
            new SondeAffichee { Entite = "sensor.salon_temperature", Nom = "Salon" },
            new SondeAffichee { Entite = "sensor.chambre_rdc_temperature", Nom = "Chbre RdC" },
            new SondeAffichee { Entite = "sensor.chambre_etage_temperature", Nom = "Chbre Etage" },
            new SondeAffichee { Entite = "sensor.sdb_temperature", Nom = "Salle de Bain" },
            new SondeAffichee { Entite = Exterieure, Nom = "Terrasse", Min = -10, Max = 40 },
        ],
        Affichage = new Affichage { Exterieure = Exterieure },
    };

    public static EntityStateUpdate Etat(string entite, object? valeur, bool disponible = true, string nom = "Entité", EntityKind genre = EntityKind.Sensor) => new()
    {
        EntityId = entite,
        Name = nom,
        Kind = genre,
        Available = disponible,
        Value = valeur is null ? null : JsonSerializer.SerializeToElement(valeur),
    };

    public static EntityStateUpdate Climat(string mode = "comfort", bool ajustee = false, bool planning = true, bool chauffe = true,
        string statut = "heating", object? prochaine = null) =>
        Etat(Thermostat, new Dictionary<string, object?>
        {
            ["temperature"] = 19.24, ["target"] = 20.0, ["mode"] = mode, ["target_adjusted"] = ajustee,
            ["schedule_active"] = planning, ["heating"] = chauffe, ["status"] = statut, ["status_label"] = "Chauffe",
            ["next_change"] = prochaine,
        }, nom: "Thermostat salon", genre: EntityKind.Climate);

    public static Dictionary<string, EntityStateUpdate> Etats(params EntityStateUpdate[] etats) =>
        etats.ToDictionary(e => e.EntityId, StringComparer.Ordinal);
}
