using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;

namespace Krissvia.Server.Components.Layout;

/// <summary>
/// Cadre de l'interface : barre du haut (fil d'Ariane, mode sombre, menu ⋮, déconnexion) et menu latéral réductible.
/// </summary>
public partial class MainLayout
{
    private static readonly Krissvia.Core.BuildVersion Version = Krissvia.Core.BuildVersion.Of(typeof(MainLayout).Assembly);

    [Inject] private MenuService Menu { get; set; } = default!;
    [Inject] private PreferencesService Preferences { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IDialogService Dialogues { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState>? Etat { get; set; }

    private bool _menuOuvert = true;
    private bool _sombre;
    private string? _utilisateur;

    private MenuEntry? PageCourante =>
        Menu.Current("/" + Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0].TrimStart('/'));

    protected override async Task OnInitializedAsync()
    {
        Navigation.LocationChanged += SurNavigation;
        Menu.Changed += SurMenu;

        _utilisateur = Etat is null ? null : (await Etat).User.Identity?.Name;
        if (_utilisateur is not null) _sombre = (await Preferences.GetAsync(_utilisateur)).DarkMode;
    }

    private void BasculerMenu() => _menuOuvert = !_menuOuvert;

    private async Task BasculerThemeAsync()
    {
        _sombre = !_sombre;
        if (_utilisateur is not null) await Preferences.SaveAsync(_utilisateur, new UserPreferences(DarkMode: _sombre));
    }

    private async Task PersonnaliserMenuAsync()
    {
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };
        await Dialogues.ShowAsync<PersonnaliserMenu>("Personnaliser le menu", options);
    }

    // Le fil d'Ariane suit la page ; une entrée de service peut apparaître ou disparaître.
    private void SurNavigation(object? sender, LocationChangedEventArgs e) => InvokeAsync(StateHasChanged);

    private void SurMenu() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Navigation.LocationChanged -= SurNavigation;
        Menu.Changed -= SurMenu;
    }

    // Palettes reprises de DomoMud.
    private static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Black = "#110e2d",
            AppbarText = "#424242",
            AppbarBackground = "rgb(255,255,255)",
            DrawerBackground = "#ffffff",
            ActionDefault = "#74718e",
            ActionDisabled = "#9999994d",
            ActionDisabledBackground = "#605f6d4d",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7e6fff",
            Surface = "#1e1e2d",
            Background = "#1a1a27",
            BackgroundGray = "#151521",
            AppbarText = "#92929f",
            AppbarBackground = "rgb(26,26,39)",
            DrawerBackground = "#1a1a27",
            ActionDefault = "#74718e",
            ActionDisabled = "#9999994d",
            ActionDisabledBackground = "#605f6d4d",
            TextPrimary = "#b2b0bf",
            TextSecondary = "#92929f",
            TextDisabled = "#ffffff33",
            DrawerIcon = "#92929f",
            DrawerText = "#92929f",
            GrayLight = "#2a2833",
            GrayLighter = "#1e1e2d",
            Info = "#4a86ff",
            Success = "#3dcb6c",
            Warning = "#ffb545",
            Error = "#ff3f5f",
            LinesDefault = "#33323e",
            TableLines = "#33323e",
            Divider = "#292838",
            OverlayLight = "#1e1e2d80",
        },
    };
}
