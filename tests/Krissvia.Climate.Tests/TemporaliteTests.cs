using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>
/// Le moteur de chauffe dépend du temps (cycles de 10 min, plannings, préchauffage).
/// Tout le code d'horloge passe par <see cref="TimeProvider"/> pour pouvoir simuler une nuit
/// entière instantanément. Ce test vérifie que l'outillage est en place.
/// </summary>
public class TemporaliteTests
{
    [Fact]
    public void Une_nuit_entiere_se_simule_en_un_instant()
    {
        var temps = new FakeTimeProvider(new DateTimeOffset(2026, 12, 1, 22, 0, 0, TimeSpan.Zero));

        temps.Advance(TimeSpan.FromHours(8));

        Assert.Equal(6, temps.GetUtcNow().Hour);
    }
}
