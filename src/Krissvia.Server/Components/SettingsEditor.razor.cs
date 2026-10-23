using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components;

/// <summary>Lecture et écriture des valeurs du formulaire dans l'objet JSON des réglages.</summary>
public partial class SettingsEditor
{
    [Parameter, EditorRequired] public SettingField Champ { get; set; } = default!;
    [Parameter, EditorRequired] public JsonObject Valeur { get; set; } = default!;

    /// <summary>Dans une section que le cœur sait remplir (broker commun) : rien n'y est obligatoire, comme à la validation.</summary>
    [Parameter] public bool Partage { get; set; }

    [Inject] private IEntityRegistry Registre { get; set; } = default!;

    /// <summary>
    /// Les entités qu'un champ « entité » propose : celles de la famille et de la classe demandées, actives. La valeur
    /// enregistrée reste proposée même si elle ne correspond plus (renommée, désactivée) : on ne l'efface pas en silence.
    /// </summary>
    private IEnumerable<RegisteredEntity> Entites(EntitySetting reference, string? actuelle) =>
        Registre.Loaded
            .Where(e => e.EntityId == actuelle
                        || (e is { Enabled: true, Missing: false }
                            && (reference.Kinds.Count == 0 || reference.Kinds.Contains(JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Kind.ToString())))
                            && (reference.DeviceClasses.Count == 0 || (e.DeviceClass is { } c && reference.DeviceClasses.Contains(c)))))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    private string NomEntite(string? id) =>
        id is null ? "" : Registre.Loaded.FirstOrDefault(e => e.EntityId == id)?.DisplayName ?? id;

    private static string AideEntite(SettingField champ, EntitySetting reference)
    {
        var droit = reference.Access == EntityAccess.Command ? "le plugin pourra la lire et la commander" : "le plugin pourra la lire";
        return champ.Description is { Length: > 0 } d ? $"{d} ({droit})" : $"{char.ToUpper(droit[0], System.Globalization.CultureInfo.CurrentCulture)}{droit[1..]}.";
    }

    private string? Texte(SettingField champ) =>
        Valeur[champ.Key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private double? Nombre(SettingField champ) =>
        Valeur[champ.Key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private bool Booleen(SettingField champ) => Valeur[champ.Key]?.GetValueKind() == JsonValueKind.True;

    private void Ecrire(SettingField champ, string? texte) =>
        Valeur[champ.Key] = string.IsNullOrEmpty(texte) ? null : JsonValue.Create(texte);

    private void Ecrire(SettingField champ, double? nombre) =>
        Valeur[champ.Key] = nombre is not { } n ? null
            : champ.Type == SettingType.Integer ? JsonValue.Create((long)Math.Round(n)) : JsonValue.Create(n);

    private void Ecrire(SettingField champ, bool oui) => Valeur[champ.Key] = JsonValue.Create(oui);

    private JsonObject SousObjet(SettingField champ)
    {
        if (Valeur[champ.Key] is JsonObject o) return o;
        var nouveau = SettingsSchema.Complete(champ, null);
        Valeur[champ.Key] = nouveau;
        return nouveau;
    }

    private JsonArray Liste(SettingField champ)
    {
        if (Valeur[champ.Key] is JsonArray a) return a;
        var nouvelle = new JsonArray();
        Valeur[champ.Key] = nouvelle;
        return nouvelle;
    }

    /// <summary>Les champs d'une seule valeur, rangés en grille ; les sections et les listes suivent.</summary>
    private IEnumerable<SettingField> ChampsSimples => Champ.Properties.Where(c => c.Type is not (SettingType.Object or SettingType.Array));
}
