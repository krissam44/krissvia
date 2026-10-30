using System.Text.Json;
using Krissvia.Core.Overview;

namespace Krissvia.Core.History;

/// <summary>Un jour d'un compteur tel que l'API le donne. Null : pas de mesure ce jour-là — pas « 0 kWh ».</summary>
public sealed record EnergyDayView(DateOnly Day, double? Kwh, double? Cost, double? PeakPower);

/// <summary>Une case de 5 min de la courbe de puissance du jour : moyenne, et l'étendue min-max de la case.</summary>
public sealed record EnergyPowerPointView(DateTimeOffset At, double? Mean, double? Min, double? Max);

/// <summary>Un compteur de l'écran Électricité tel que l'API le donne (<c>GET /api/energy</c>).</summary>
/// <param name="TodayKwh">Depuis minuit local : index maintenant moins index à minuit.</param>
/// <param name="Days">Du plus ancien au plus récent, aujourd'hui compris.</param>
/// <param name="TodayPower">La puissance de minuit à maintenant, par cases de 5 min ; vide sans entité de puissance.</param>
public sealed record EnergyMeterView(
    string Name,
    string IndexEntityId,
    double? Power,
    string? PowerUnit,
    double? Voltage,
    double? TodayKwh,
    double? TodayCost,
    IReadOnlyList<EnergyDayView> Days,
    IReadOnlyList<EnergyPowerPointView>? TodayPower = null);

/// <summary>
/// L'écran Électricité pour un client de l'API (appli mobile) : consommation et coût calculés une seule fois par le
/// serveur, d'après l'historique (HISTORIQUE.md §6). Un client qui referait « index − index à minuit » ou la règle
/// des remises à zéro de <see cref="EnergyDays"/> finirait par ne pas compter comme le web.
/// </summary>
/// <param name="Price">Le prix du kWh (€), null s'il n'est pas renseigné : aucun coût n'est alors donné.</param>
public sealed record EnergyOverviewView(double? Price, IReadOnlyList<EnergyMeterView> Meters)
{
    public static JsonSerializerOptions Json => SensorOverviewView.Json;
}
