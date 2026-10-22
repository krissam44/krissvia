using System.Globalization;
using Krissvia.Climate.Thermostats;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Krissvia.Server.Components.Thermostats;

/// <summary>
/// L'auto-TPI d'un thermostat, dans l'onglet Régulation : activation, type de chauffage, dernier modèle identifié, réglage
/// proposé à côté de celui en place, « Appliquer » et retour arrière. Activation et type s'enregistrent tout de suite :
/// ils ne passent pas par le bouton « Enregistrer » de la page.
/// </summary>
public partial class AutoTpiPanel
{
    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    [Parameter, EditorRequired] public string ThermostatId { get; set; } = "";

    /// <summary>Le réglage vient de changer (appliqué ou remis) : la page met à jour ses champs Kp, Ki, Kint.</summary>
    [Parameter] public EventCallback<TpiGains> TpiChanged { get; set; }

    [Inject] private AutoTpiService AutoTpi { get; set; } = default!;
    [Inject] private ThermostatService Thermostats { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private bool _actif;
    private HeatingType _type;
    private bool _occupe;
    private AutoTpiReport? _rapport;

    protected override void OnParametersSet()
    {
        var t = Thermostats.Get(ThermostatId);
        _actif = t?.AutoTpi ?? false;
        _type = t?.HeatingType ?? HeatingType.Electric;
        _rapport = AutoTpi.Get(ThermostatId);
    }

    private Task ActiverAsync(bool actif) => OptionsAsync(actif, _type);

    private Task TypeChangeAsync(HeatingType type) => OptionsAsync(_actif, type);

    private async Task OptionsAsync(bool actif, HeatingType type)
    {
        await AgirAsync(async () =>
        {
            var t = await AutoTpi.SetOptionsAsync(ThermostatId, actif, type);
            (_actif, _type) = (t.AutoTpi, t.HeatingType);
            // Activé sans calcul encore : le faire tout de suite, plutôt que de laisser un panneau vide jusqu'à demain.
            if (_actif && _rapport is null) _rapport = await AutoTpi.ComputeAsync(ThermostatId);
        });
    }

    private Task CalculerAsync() => AgirAsync(async () => _rapport = await AutoTpi.ComputeAsync(ThermostatId));

    private Task AppliquerAsync() => AgirAsync(async () =>
    {
        var t = await AutoTpi.ApplyAsync(ThermostatId);
        _rapport = AutoTpi.Get(ThermostatId);
        await TpiChanged.InvokeAsync(TpiGains.Of(t.Tpi));
        Snackbar.Add("Réglage proposé appliqué : il régule dès le prochain cycle.", Severity.Success);
    });

    private Task RevenirAsync() => AgirAsync(async () =>
    {
        var t = await AutoTpi.RevertAsync(ThermostatId);
        _rapport = AutoTpi.Get(ThermostatId);
        await TpiChanged.InvokeAsync(TpiGains.Of(t.Tpi));
        Snackbar.Add("Réglage précédent remis.", Severity.Info);
    });

    private async Task AgirAsync(Func<Task> action)
    {
        _occupe = true;
        try
        {
            await action();
        }
        catch (ThermostatValidationException ex)
        {
            Snackbar.Add(Phrases.Majuscule(ex.Message), Severity.Warning);
        }
        finally
        {
            _occupe = false;
        }
    }

    private string Heure(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).ToString("ddd d MMM HH:mm", Francais);

    private static string Jours(double heures) =>
        heures >= 48 ? $"{heures / 24:0.#} jours".Replace('.', ',') : $"{heures:0} h";

    private static string Nombre(double v, string format) => double.IsFinite(v) ? v.ToString(format, Francais) : "∞";
}
