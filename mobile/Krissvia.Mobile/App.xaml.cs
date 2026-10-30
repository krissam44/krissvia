using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;

namespace Krissvia.Mobile;

public partial class App : Application
{
    private readonly FluxEnDirect _flux;
    private readonly ReveilAppli _reveil;

    public App(FluxEnDirect flux, ReveilAppli reveil)
    {
        InitializeComponent();
        _flux = flux;
        _reveil = reveil;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var fenetre = new Window(new MainPage()) { Title = "Krissvia" };
        // Au retour au premier plan, Android a souvent coupé la connexion sans rien dire : on repart de la liste,
        // tout de suite, plutôt que d'afficher des valeurs d'il y a une heure jusqu'au prochain essai.
        fenetre.Resumed += (_, _) =>
        {
            _flux.Reveiller();
            _reveil.Signaler();
        };
        return fenetre;
    }
}
