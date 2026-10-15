using System.Text.Json;
using Krissvia.Core.Events;
using Krissvia.Core.States;

namespace Krissvia.Core.Tests;

/// <summary>Ce que le magasin d'états publie : l'état précédent, sans lequel aucun déclencheur sur front n'est possible.</summary>
public class EntityStateStoreEventsTests
{
    private readonly List<CoreEvent> _publies = [];
    private readonly EntityStateStore _etats;

    public EntityStateStoreEventsTests() =>
        _etats = new EntityStateStore(new SansPersistance(), TimeProvider.System, new PublieurEnListe(_publies));

    private sealed class SansPersistance : IEntityStateRepository
    {
        public Task<IReadOnlyList<PersistedEntityState>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PersistedEntityState>>([]);

        public Task SaveAsync(IReadOnlyList<PersistedEntityState> states, CancellationToken ct = default) => Task.CompletedTask;
    }

    private EntityStateChanged[] Changements => _publies.OfType<EntityStateChanged>().ToArray();

    [Fact]
    public void La_premiere_valeur_n_est_pas_un_front()
    {
        _etats.Update("binary_sensor.porte", JsonSerializer.SerializeToElement(true));

        var e = Assert.Single(Changements);
        Assert.Null(e.Previous);
        Assert.False(e.ValueChanged);
    }

    [Fact]
    public void Un_changement_porte_l_ancienne_et_la_nouvelle_valeur()
    {
        _etats.Update("binary_sensor.porte", JsonSerializer.SerializeToElement(false));
        _etats.Update("binary_sensor.porte", JsonSerializer.SerializeToElement(true));

        var e = Changements[1];
        Assert.False(e.Previous!.Value.GetBoolean());
        Assert.True(e.Current.Value.GetBoolean());
        Assert.True(e.ValueChanged);
    }

    [Fact]
    public void Une_valeur_repetee_est_publiee_mais_n_est_pas_un_changement()
    {
        // Un détecteur qui réaffirme sa détection ne doit pas redéclencher un automatisme.
        _etats.Update("binary_sensor.radar", JsonSerializer.SerializeToElement(true));
        _etats.Update("binary_sensor.radar", JsonSerializer.SerializeToElement(true));

        Assert.Equal(2, Changements.Length);
        Assert.False(Changements[1].ValueChanged);
    }

    [Fact]
    public void La_perte_de_disponibilite_est_publiee()
    {
        _etats.Update("sensor.sonde", JsonSerializer.SerializeToElement(19.5));
        _etats.SetAvailability("sensor.sonde", false);
        _etats.SetAvailability("sensor.sonde", false);

        var e = Assert.Single(Changements.Skip(1));
        Assert.True(e.AvailabilityChanged);
        Assert.False(e.Current.Available);
        Assert.False(e.ValueChanged);
    }
}
