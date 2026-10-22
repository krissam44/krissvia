using Krissvia.Core.Notifications;

namespace Krissvia.Server.Components.Pages;

/// <summary>
/// Notifications : le serveur ntfy (avec un essai avant d'enregistrer), les réglages des alertes et celles en cours.
/// </summary>
public partial class Notifications : IDisposable
{
    [Inject] private NotificationService Service { get; set; } = default!;
    [Inject] private AlertService Alertes { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private sealed class FormulaireNtfy
    {
        public bool Enabled { get; set; }
        public string Serveur { get; set; } = "";
        public string Sujet { get; set; } = "";
        public string? Jeton { get; set; }

        public NotificationSettings Vers() => new() { Enabled = Enabled, BaseUrl = Serveur, Topic = Sujet, Token = Jeton };
    }

    private sealed class FormulaireGel
    {
        public bool Actif { get; set; }
        public double Seuil { get; set; }
        public int Horizon { get; set; }
    }

    private FormulaireNtfy _ntfy = new();
    private FormulaireGel _gel = new();
    private AlertSettings _alertes = new();
    private bool _envoi;

    protected override async Task OnInitializedAsync()
    {
        var n = await Service.GetSettingsAsync();
        _ntfy = new FormulaireNtfy { Enabled = n.Enabled, Serveur = n.BaseUrl, Sujet = n.Topic, Jeton = n.Token };
        _alertes = await Alertes.GetSettingsAsync();
        _gel = new FormulaireGel { Actif = _alertes.Frost.Enabled, Seuil = _alertes.Frost.ThresholdCelsius, Horizon = _alertes.Frost.HorizonHours };
        Alertes.Changed += Rafraichir;
    }

    private void Rafraichir() => InvokeAsync(StateHasChanged);

    private async Task EnregistrerNtfyAsync()
    {
        try
        {
            await Service.SaveAsync(_ntfy.Vers());
            Snackbar.Add(_ntfy.Enabled ? "Notifications enregistrées et activées." : "Enregistré ; notifications désactivées.", Severity.Success);
        }
        catch (NotificationValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    /// <summary>Éprouve le formulaire tel qu'il est, même pas encore enregistré ni activé.</summary>
    private async Task EssayerAsync()
    {
        var reglages = _ntfy.Vers();
        try
        {
            NotificationService.Validate(reglages with { Enabled = false });
        }
        catch (NotificationValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
            return;
        }

        _envoi = true;
        var r = await Service.SendAsync(new Notification("Krissvia", "Notification d'essai : tout fonctionne.", Tags: ["white_check_mark"]), reglages);
        _envoi = false;
        Snackbar.Add(r.Sent ? "Essai envoyé : il doit arriver sur le téléphone." : $"Essai non envoyé : {r.Detail?.TrimEnd('.')}.",
            r.Sent ? Severity.Success : Severity.Error);
    }

    private async Task EnregistrerAlertesAsync()
    {
        try
        {
            // Relus au moment d'enregistrer : la page Supervision écrit dans le même document.
            _alertes = (await Alertes.GetSettingsAsync()) with { Frost = new FrostAlertSettings { Enabled = _gel.Actif, ThresholdCelsius = _gel.Seuil, HorizonHours = _gel.Horizon } };
            await Alertes.SaveSettingsAsync(_alertes);
            Snackbar.Add("Alertes enregistrées ; prises en compte à la prochaine analyse.", Severity.Success);
        }
        catch (NotificationValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
    }

    private static string Remise(SendOutcome issue) => issue switch
    {
        SendOutcome.Sent => "envoyée",
        SendOutcome.Disabled => "non envoyée (notifications désactivées)",
        SendOutcome.NotConfigured => "non envoyée (ntfy non réglé)",
        _ => "",
    };

    public void Dispose() => Alertes.Changed -= Rafraichir;
}
