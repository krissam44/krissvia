namespace Krissvia.Mobile.WinUI;

/// <summary>Version Windows : pour essayer l'appli depuis le PC, contre le vrai serveur.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
