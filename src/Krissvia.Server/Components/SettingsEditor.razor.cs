using System.Text.Json;
using System.Text.Json.Nodes;

namespace Krissvia.Server.Components;

/// <summary>Lecture et écriture des valeurs du formulaire dans l'objet JSON des réglages.</summary>
public partial class SettingsEditor
{
    [Parameter, EditorRequired] public SettingField Champ { get; set; } = default!;
    [Parameter, EditorRequired] public JsonObject Valeur { get; set; } = default!;

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

    private void Ajouter(SettingField champ) => Liste(champ).Add(SettingsSchema.Complete(champ, null));
}
