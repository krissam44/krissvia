using System.Text.Json;
using Krissvia.Core.Events;
using Krissvia.Core.Live;
using Krissvia.Core.States;

namespace Krissvia.Core.Tests;

public class LiveFeedTests : IAsyncLifetime
{
    private readonly EventBus _bus = new();
    private readonly LiveFeed _flux;

    public LiveFeedTests() => _flux = new LiveFeed(_bus);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _flux.Dispose();
        await _bus.DisposeAsync();
    }

    private static readonly EntityStateSnapshot Etat = new(JsonSerializer.SerializeToElement(1), DateTimeOffset.UnixEpoch);

    private void Changer(string id) => _bus.Publish(new EntityStateChanged(id, null, Etat));

    private static Task<LiveBatch> Lire(LiveSubscription abonnement) =>
        abonnement.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // Le bus remet les événements sur sa propre boucle : on attend qu'il ait vidé ses files.
    private async Task Attendre()
    {
        for (var i = 0; i < 200 && _bus.Diagnostics().Any(d => d.Pending > 0); i++) await Task.Delay(10);
        await Task.Delay(20);
    }

    [Fact]
    public async Task Un_changement_d_etat_reveille_le_client()
    {
        using var abonnement = _flux.Subscribe();
        var lecture = Lire(abonnement);

        Changer("sensor.salon");

        var lot = await lecture;
        Assert.Equal(["sensor.salon"], lot.ChangedEntities);
        Assert.False(lot.RegistryChanged);
    }

    [Fact]
    public async Task Les_changements_accumules_arrivent_une_seule_fois_chacun_dans_l_ordre()
    {
        using var abonnement = _flux.Subscribe();

        Changer("sensor.a");
        Changer("sensor.b");
        Changer("sensor.a");
        await Attendre();

        Assert.Equal(["sensor.a", "sensor.b"], (await Lire(abonnement)).ChangedEntities);
    }

    [Fact]
    public async Task Une_lecture_vide_le_lot()
    {
        using var abonnement = _flux.Subscribe();
        Changer("sensor.a");
        await Attendre();
        await Lire(abonnement);

        Changer("sensor.b");

        Assert.Equal(["sensor.b"], (await Lire(abonnement)).ChangedEntities);
    }

    [Fact]
    public async Task Le_filtre_ecarte_les_autres_entites_mais_pas_le_registre()
    {
        using var abonnement = _flux.Subscribe(["sensor.salon"]);

        Changer("sensor.cave");
        _bus.Publish(new RegistryChanged());
        Changer("sensor.salon");
        await Attendre();

        var lot = await Lire(abonnement);
        Assert.True(lot.RegistryChanged);
        Assert.Equal(["sensor.salon"], lot.ChangedEntities);
    }

    [Fact]
    public async Task Chaque_client_recoit_tout()
    {
        using var premier = _flux.Subscribe();
        using var second = _flux.Subscribe();

        Changer("sensor.a");
        await Attendre();

        Assert.Equal(["sensor.a"], (await Lire(premier)).ChangedEntities);
        Assert.Equal(["sensor.a"], (await Lire(second)).ChangedEntities);
    }

    [Fact]
    public async Task Un_client_parti_ne_recoit_plus_rien()
    {
        var abonnement = _flux.Subscribe();
        Assert.Equal(1, _flux.Subscribers);

        abonnement.Dispose();
        Changer("sensor.a");
        await Attendre();

        Assert.Equal(0, _flux.Subscribers);
        await Assert.ThrowsAsync<TimeoutException>(() => abonnement.ReadAsync().WaitAsync(TimeSpan.FromMilliseconds(100)));
    }
}
