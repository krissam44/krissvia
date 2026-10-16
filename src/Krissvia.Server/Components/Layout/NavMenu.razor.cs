namespace Krissvia.Server.Components.Layout;

public partial class NavMenu
{
    [Inject] private MenuService Menu { get; set; } = default!;

    private IReadOnlyList<MenuEntry> _entrees = [];
    private bool _fini;

    protected override async Task OnInitializedAsync()
    {
        _entrees = await Menu.GetVisibleAsync();
        Menu.Changed += SurChangement;
    }

    private async void SurChangement()
    {
        try
        {
            if (_fini) return;
            _entrees = await Menu.GetVisibleAsync();
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
