namespace Krissvia.Server.Components.Layout;

public partial class NavMenu
{
    [Inject] private MenuService Menu { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private IReadOnlyList<MenuEntry> _entrees = [];
    private IReadOnlyList<ServiceSnapshot> _plugins = [];

    // Déplié d'office quand on est sur une page de service, replié sinon.
    private bool _servicesDeplies;
    private bool _fini;

    protected override async Task OnInitializedAsync()
    {
        _entrees = await Menu.GetVisibleAsync();
        _plugins = Menu.Plugins();
        _servicesDeplies = Navigation.ToBaseRelativePath(Navigation.Uri).StartsWith("parametres/services", StringComparison.OrdinalIgnoreCase);
        Menu.Changed += SurChangement;
    }

    private async void SurChangement()
    {
        try
        {
            if (_fini) return;
            _entrees = await Menu.GetVisibleAsync();
            _plugins = Menu.Plugins();
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // Circuit fermé entre-temps.
        }
    }

    public void Dispose()
    {
        _fini = true;
        Menu.Changed -= SurChangement;
    }
}
