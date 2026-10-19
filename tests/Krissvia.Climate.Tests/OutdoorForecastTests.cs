using System.Text.Json;
using Krissvia.Climate.Thermostats;
using Krissvia.Core.States;

namespace Krissvia.Climate.Tests;

/// <summary>La température extérieure anticipée : moyenne prévue sur 2 h, ou rien plutôt qu'une valeur douteuse.</summary>
public class OutdoorForecastTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Une valeur d'entité météo dont la série horaire part de <paramref name="debut"/>.</summary>
    internal static JsonElement Meteo(DateTimeOffset debut, params double[] temperatures) => JsonSerializer.SerializeToElement(new
    {
        temperature = 99.0,   // la mesure du moment n'entre pas dans la moyenne prévue
        hourly = temperatures.Select((t, i) => new { time = debut.AddHours(i).ToString("yyyy-MM-ddTHH:mm:ssZ"), temperature = t }),
    });

    [Fact]
    public void La_moyenne_echantillonne_l_horizon_toutes_les_demi_heures_en_interpolant()
    {
        // 10 °C à 12 h, 6 °C à 13 h, 2 °C à 14 h : échantillons 10, 8, 6, 4, 2 → 6.
        Assert.Equal(6.0, OutdoorForecast.MeanAhead(Meteo(T0, 10, 6, 2, 0), T0, OutdoorForecast.Horizon));

        // À 12 h 30 : 8, 6, 4, 2, 1 → 4,2.
        Assert.Equal(4.2, OutdoorForecast.MeanAhead(Meteo(T0, 10, 6, 2, 0), T0.AddMinutes(30), OutdoorForecast.Horizon));
    }

    [Fact]
    public void Une_serie_qui_ne_couvre_pas_tout_l_horizon_ne_donne_rien()
    {
        Assert.Null(OutdoorForecast.MeanAhead(Meteo(T0, 10, 6), T0, OutdoorForecast.Horizon));          // s'arrête à 13 h
        Assert.Null(OutdoorForecast.MeanAhead(Meteo(T0.AddMinutes(10), 10, 6, 2, 0), T0, OutdoorForecast.Horizon));   // commence après
        Assert.Null(OutdoorForecast.MeanAhead(JsonSerializer.SerializeToElement(new { temperature = 5 }), T0, OutdoorForecast.Horizon));
        Assert.Null(OutdoorForecast.MeanAhead(JsonSerializer.SerializeToElement(12.5), T0, OutdoorForecast.Horizon));
    }

    [Fact]
    public void Une_prevision_indisponible_ou_trop_vieille_n_est_pas_crue()
    {
        var valeur = Meteo(T0.AddHours(-4), 10, 10, 10, 10, 6, 2, 0, 0);

        // Échantillons à 12 h, 12 h 30… 14 h : 6, 4, 2, 1, 0 → 2,6.
        Assert.Equal(2.6, OutdoorForecast.Anticipated(new EntityStateSnapshot(valeur, T0.AddHours(-1)), T0));
        Assert.Null(OutdoorForecast.Anticipated(new EntityStateSnapshot(valeur, T0.AddHours(-1), Available: false), T0));
        Assert.Null(OutdoorForecast.Anticipated(new EntityStateSnapshot(valeur, T0 - OutdoorForecast.MaxAge - TimeSpan.FromMinutes(1)), T0));
        Assert.Null(OutdoorForecast.Anticipated(null, T0));
    }
}
