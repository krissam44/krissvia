namespace Krissvia.Server.Components.Layout;

/// <summary>Ordre et masquage des entrées du menu latéral, pour les pages et les services activés.</summary>
public partial class PersonnaliserMenu
{
    [Inject] private MenuService Menu { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [CascadingParameter] private IMudDialogInstance Dialogue { get; set; } = default!;

    private List<MenuEntry> _entrees = [];

    // Un dictionnaire et non une propriété des entrées : les interrupteurs dans une boucle se lient ainsi sans que
    // MudBlazor recrée les composants (piège déjà rencontré dans DomoMud).
    private readonly Dictionary<string, bool> _visibles = new(StringComparer.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        _entrees = [.. await Menu.GetEntriesAsync()];
        foreach (var entree in _entrees) _visibles[entree.Key] = !entree.Hidden;
    }

    private void Deplacer(int index, int sens)
    {
        var cible = index + sens;
        if (cible < 0 || cible >= _entrees.Count) return;
        (_entrees[index], _entrees[cible]) = (_entrees[cible], _entrees[index]);
    }

    private void Reinitialiser()
    {
        _entrees = [.. Menu.Available()];
        foreach (var entree in _entrees) _visibles[entree.Key] = true;
    }

    private async Task EnregistrerAsync()
    {
        await Menu.SaveAsync([.. _entrees.Select(e => e with { Hidden = !_visibles.GetValueOrDefault(e.Key, true) })]);
        Snackbar.Add("Menu enregistré.", Severity.Success);
        Dialogue.Close(DialogResult.Ok(true));
    }
}
