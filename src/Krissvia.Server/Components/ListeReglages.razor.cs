using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components;

/// <summary>Une liste de réglages de plugin, en tableau ; chaque ligne se modifie dans <see cref="ElementReglagesDialog"/>.</summary>
public partial class ListeReglages
{
    [Parameter, EditorRequired] public SettingField Champ { get; set; } = default!;
    [Parameter, EditorRequired] public JsonArray Liste { get; set; } = default!;

    /// <summary>Levé après chaque ajout, modification, retrait ou déplacement (voir <see cref="SettingsEditor.Modifie"/>).</summary>
    [Parameter] public EventCallback Modifie { get; set; }

    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [Inject] private PressePapiersReglages PressePapiers { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private List<JsonObject> Elements => [.. Liste.OfType<JsonObject>()];

    /// <summary>
    /// Change à chaque modification de la liste, et reconstruit le tableau (<c>@key</c>). MudDataGrid ne redessine pas
    /// une ligne dont l'élément a été REMPLACÉ à la même place : la ligne modifiée gardait son ancien contenu à l'écran
    /// alors que la valeur était bien prise, et enregistrée — corriger un topic semblait sans effet (29/10/2026).
    /// </summary>
    private int _version;

    private bool Plein => Champ.MaxItems is { } plafond && Liste.Count >= plafond;

    /// <summary>Les colonnes : les champs simples, et le nombre d'éléments d'une sous-liste (les interrupteurs d'une platine).</summary>
    private IEnumerable<SettingField> Colonnes => Champ.Properties.Where(c => c.Type != SettingType.Object);

    /// <summary>« Interrupteurs (page horloge, 3 au plus) » devient « Interrupteurs » : la précision reste dans la fenêtre.</summary>
    private static string Entete(SettingField champ) => champ.Label.Split(" (")[0];

    /// <summary>Le n° de ligne, un nombre (n° d'objet…), les boutons : la largeur de leur contenu, pas plus.</summary>
    private const string Etroite = "width: 1%; white-space: nowrap";

    private string Apercu(SettingField champ, JsonObject element)
    {
        var v = element[champ.Key];
        if (champ.Type == SettingType.Array) return v is JsonArray a ? a.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "0";
        if (v is not JsonValue valeur) return "—";
        if (champ.Secret) return "••••";
        // Un JSONL de quarante lignes ne tient pas dans une cellule : son volume suffit à le reconnaître.
        if (champ.Multiline)
            return valeur.GetValueKind() == JsonValueKind.String && valeur.GetValue<string>() is { Length: > 0 } t
                ? $"{t.Split('\n').Count(l => l.Trim().Length > 0)} ligne(s)"
                : "—";
        return valeur.GetValueKind() switch
        {
            JsonValueKind.String when valeur.GetValue<string>() is { Length: > 0 } texte =>
                champ.Choices.Count > 0 ? champ.ChoiceLabel(texte)
                : champ.Entity is null ? texte : Registre.Loaded.FirstOrDefault(e => e.EntityId == texte)?.DisplayName ?? texte,
            JsonValueKind.Number when SettingsSchema.TryGetNumber(valeur, out var n) => n.ToString(System.Globalization.CultureInfo.CurrentCulture),
            _ => "—",
        };
    }

    private async Task Deplacer(int indice, int sens)
    {
        var cible = indice + sens;
        if (cible < 0 || cible >= Liste.Count) return;
        var element = Liste[indice];
        Liste.RemoveAt(indice);
        Liste.Insert(cible, element);
        _version++;
        await Modifie.InvokeAsync();
    }

    private async Task AjouterAsync()
    {
        if (Plein) return;
        if (await OuvrirAsync(SettingsSchema.Complete(Champ, null), $"Ajouter — {Entete(Champ)}") is { } nouveau)
        {
            Liste.Add(nouveau);
            _version++;
            await Modifie.InvokeAsync();
        }
    }

    private async Task ModifierAsync(JsonObject element)
    {
        var indice = Liste.IndexOf(element);
        if (indice < 0) return;
        // Sur une copie : « Annuler » doit laisser la ligne telle qu'elle était, sous-listes comprises.
        if (await OuvrirAsync((JsonObject)element.DeepClone(), $"{Entete(Champ)} n° {indice + 1}") is { } modifie)
        {
            Liste[indice] = modifie;
            _version++;
            await Modifie.InvokeAsync();
        }
    }

    private void Copier()
    {
        PressePapiers.Copier(Champ, Liste);
        Snackbar.Add($"« {Entete(Champ)} » : {Liste.Count} ligne(s) copiée(s). « Coller » sous la même liste d'un autre élément.", Severity.Info);
    }

    /// <summary>Remplace la liste par ce qui a été copié ; une liste déjà remplie demande confirmation.</summary>
    private async Task CollerAsync()
    {
        var copies = PressePapiers.Coller(Champ);
        if (copies.Count == 0) return;
        if (Liste.Count > 0 && await Dialogues.ShowMessageBoxAsync("Coller",
                $"Remplacer les {Liste.Count} ligne(s) de « {Entete(Champ)} » par les {copies.Count} copiée(s) ?",
                yesText: "Remplacer", cancelText: "Annuler") != true)
            return;
        Liste.Clear();
        foreach (var element in copies) Liste.Add(element);
        _version++;
        await Modifie.InvokeAsync();
    }

    private async Task RetirerAsync(JsonObject element)
    {
        var indice = Liste.IndexOf(element);
        if (indice < 0) return;
        if (await Dialogues.ShowMessageBoxAsync("Retirer", $"Retirer la ligne n° {indice + 1} de « {Entete(Champ)} » ?",
                yesText: "Retirer", cancelText: "Annuler") == true)
        {
            Liste.RemoveAt(indice);
            _version++;
            await Modifie.InvokeAsync();
        }
    }

    private async Task<JsonObject?> OuvrirAsync(JsonObject element, string titre)
    {
        var parametres = new DialogParameters<ElementReglagesDialog>
        {
            { d => d.Champ, Champ },
            { d => d.Element, element },
        };
        // Une ligne qui contient elle-même une liste (les interrupteurs d'une platine) a besoin de place.
        var largeur = Champ.Properties.Any(c => c.Type == SettingType.Array) ? MaxWidth.Large : MaxWidth.Small;
        var dialogue = await Dialogues.ShowAsync<ElementReglagesDialog>(titre, parametres,
            new DialogOptions { MaxWidth = largeur, FullWidth = true, CloseButton = true });
        return await dialogue.Result is { Canceled: false, Data: JsonObject resultat } ? resultat : null;
    }
}
