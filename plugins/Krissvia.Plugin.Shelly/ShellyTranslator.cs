using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.Shelly;

/// <summary>Un appareil Shelly reconnu par <c>/shelly</c> : son identité et ce qu'il porte.</summary>
internal sealed record ShellyIdentity(
    string Mac,
    int Generation,
    string Model,
    int Emeters,
    int Outputs,
    IReadOnlyList<string> Gen2Switches,
    IReadOnlyList<string> Gen2Em1);

/// <summary>
/// Traduction pure Shelly ↔ contrat. Génération 1 (<c>/status</c>, <c>/relay/{i}</c>) vérifiée sur un Shelly EM ;
/// génération 2 (<c>/rpc/…</c>) écrite d'après la documentation de Shelly.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>L'identité est l'adresse MAC : changer l'IP de l'appareil ne change aucune entité.</item>
/// <item>Puissance apparente : mesurée par les modèles qui la donnent (<c>aprt_power</c>), sinon calculée
/// √(P² + Q²) quand la réactive est connue. C'est elle que compare l'abonnement : bien plus juste qu'une
/// majoration forfaitaire de la puissance active (sur un Shelly EM : 2 029 VA mesurés contre 2 209 estimés).</item>
/// <item>Une voie marquée invalide (<c>is_valid: false</c>, pince débranchée) ne publie rien : pas de zéro.</item>
/// </list>
/// </remarks>
internal static class ShellyTranslator
{
    /// <summary>Lit la réponse de <c>/shelly</c> (génération 1) complétée, en génération 2, par <c>Shelly.GetStatus</c>.</summary>
    public static ShellyIdentity? Identify(JsonElement shelly, JsonElement? statut2)
    {
        if (Chaine(shelly, "mac") is not { Length: > 0 } mac) return null;

        if (shelly.TryGetProperty("gen", out var gen) && gen.TryGetInt32(out var g) && g >= 2)
        {
            var cles = statut2 is { ValueKind: JsonValueKind.Object } s
                ? s.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            return new ShellyIdentity(mac.ToLowerInvariant(), g, Chaine(shelly, "model") ?? Chaine(shelly, "app") ?? "Shelly",
                0, 0,
                cles.Where(c => c.StartsWith("switch:", StringComparison.Ordinal)).Order().ToList(),
                cles.Where(c => c.StartsWith("em1:", StringComparison.Ordinal)).Order().ToList());
        }

        return new ShellyIdentity(mac.ToLowerInvariant(), 1, Chaine(shelly, "type") ?? "Shelly",
            Entier(shelly, "num_emeters"), Entier(shelly, "num_outputs"), [], []);
    }

    /// <summary>Chemin de la lecture d'état : une seule requête par cycle.</summary>
    public static string StatusPath(ShellyIdentity id) => id.Generation >= 2 ? "/rpc/Shelly.GetStatus" : "/status";

    public static DeviceDescriptor Describe(ShellyIdentity id, string nom)
    {
        var entites = new List<EntityDescriptor>();

        foreach (var voie in VoiesDeMesure(id))
        {
            var multiples = VoiesDeMesure(id).Count() > 1;
            string? Nom(string quoi) => multiples ? $"{quoi} {voie}" : null;

            entites.Add(Mesure(id, voie, "power", Nom("power"), DeviceClasses.Power, Units.Watt, StateClass.Measurement));
            entites.Add(Mesure(id, voie, "apparent_power", Nom("apparent_power"), DeviceClasses.ApparentPower, Units.VoltAmpere, StateClass.Measurement));
            entites.Add(Mesure(id, voie, "voltage", Nom("voltage"), DeviceClasses.Voltage, Units.Volt, StateClass.Measurement));
            if (id.Generation == 1)
                entites.Add(Mesure(id, voie, "energy", Nom("energy"), DeviceClasses.Energy, Units.KilowattHour, StateClass.TotalIncreasing));
            else
                entites.Add(Mesure(id, voie, "current", Nom("current"), DeviceClasses.Current, Units.Ampere, StateClass.Measurement));
        }

        foreach (var relais in Relais(id))
        {
            entites.Add(new EntityDescriptor
            {
                UniqueId = $"{id.Mac}:relay{relais}",
                Kind = EntityKind.Switch,
                Name = Relais(id).Count() > 1 ? $"relais {relais}" : null,
                Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle],
            });
        }

        return new DeviceDescriptor { UniqueId = id.Mac, Name = nom, Manufacturer = "Shelly", Model = id.Model, Entities = entites };
    }

    /// <summary>Valeurs contenues dans une réponse d'état.</summary>
    public static IReadOnlyList<EntityState> ReadStatus(ShellyIdentity id, JsonElement statut)
    {
        var etats = new List<EntityState>();
        void Publier(string suffixe, object valeur) =>
            etats.Add(new EntityState { EntityUniqueId = $"{id.Mac}:{suffixe}", Value = JsonSerializer.SerializeToElement(valeur) });

        if (id.Generation == 1)
        {
            if (statut.TryGetProperty("emeters", out var emeters) && emeters.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var e in emeters.EnumerateArray())
                {
                    var voie = i++;
                    if (e.TryGetProperty("is_valid", out var valide) && valide.ValueKind == JsonValueKind.False) continue;

                    if (Nombre(e, "power") is { } p)
                    {
                        Publier($"emeter{voie}:power", Math.Round(p, 2));
                        if (Nombre(e, "reactive") is { } q) Publier($"emeter{voie}:apparent_power", Math.Round(Math.Sqrt(p * p + q * q), 1));
                    }

                    if (Nombre(e, "voltage") is { } v) Publier($"emeter{voie}:voltage", v);
                    if (Nombre(e, "total") is { } wh && wh >= 0) Publier($"emeter{voie}:energy", Math.Round(wh / 1000, 3));
                }
            }

            if (statut.TryGetProperty("relays", out var relais) && relais.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var r in relais.EnumerateArray())
                {
                    var n = i++;
                    if (r.TryGetProperty("ison", out var ison) && ison.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        Publier($"relay{n}", ison.GetBoolean());
                }
            }

            return etats;
        }

        foreach (var cle in id.Gen2Em1)
        {
            if (!statut.TryGetProperty(cle, out var em)) continue;
            var voie = Indice(cle);
            if (em.TryGetProperty("errors", out var erreurs) && erreurs.ValueKind == JsonValueKind.Array && erreurs.GetArrayLength() > 0) continue;

            if (Nombre(em, "act_power") is { } p) Publier($"emeter{voie}:power", Math.Round(p, 2));
            if (Nombre(em, "aprt_power") is { } s) Publier($"emeter{voie}:apparent_power", Math.Round(s, 1));
            if (Nombre(em, "voltage") is { } v) Publier($"emeter{voie}:voltage", v);
            if (Nombre(em, "current") is { } a) Publier($"emeter{voie}:current", a);
        }

        foreach (var cle in id.Gen2Switches)
        {
            if (statut.TryGetProperty(cle, out var sw) && sw.TryGetProperty("output", out var sortie) &&
                sortie.ValueKind is JsonValueKind.True or JsonValueKind.False)
                Publier($"relay{Indice(cle)}", sortie.GetBoolean());
        }

        return etats;
    }

    /// <summary>Chemin HTTP qui exécute l'ordre, ou null si l'entité ou la commande n'existent pas.</summary>
    public static string? CommandPath(ShellyIdentity id, string entityUniqueId, string commande)
    {
        var prefixe = $"{id.Mac}:relay";
        if (!entityUniqueId.StartsWith(prefixe, StringComparison.Ordinal) ||
            !int.TryParse(entityUniqueId[prefixe.Length..], out var n) || !Relais(id).Contains(n))
            return null;

        return (id.Generation, commande) switch
        {
            (1, StandardCommands.TurnOn) => $"/relay/{n}?turn=on",
            (1, StandardCommands.TurnOff) => $"/relay/{n}?turn=off",
            (1, StandardCommands.Toggle) => $"/relay/{n}?turn=toggle",
            (_, StandardCommands.TurnOn) => $"/rpc/Switch.Set?id={n}&on=true",
            (_, StandardCommands.TurnOff) => $"/rpc/Switch.Set?id={n}&on=false",
            (_, StandardCommands.Toggle) => $"/rpc/Switch.Toggle?id={n}",
            _ => null,
        };
    }

    // ── Utilitaires ──────────────────────────────────────────────────────────

    private static IEnumerable<int> VoiesDeMesure(ShellyIdentity id) =>
        id.Generation == 1 ? Enumerable.Range(0, id.Emeters) : id.Gen2Em1.Select(Indice);

    private static IEnumerable<int> Relais(ShellyIdentity id) =>
        id.Generation == 1 ? Enumerable.Range(0, id.Outputs) : id.Gen2Switches.Select(Indice);

    private static EntityDescriptor Mesure(ShellyIdentity id, int voie, string quoi, string? nom, string classe, string unite, StateClass etat) => new()
    {
        UniqueId = $"{id.Mac}:emeter{voie}:{quoi}",
        Kind = EntityKind.Sensor,
        Name = nom,
        DeviceClass = classe,
        Unit = unite,
        StateClass = etat,
    };

    private static int Indice(string cle) => int.TryParse(cle[(cle.IndexOf(':') + 1)..], out var n) ? n : 0;

    private static string? Chaine(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Entier(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static double? Nombre(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
