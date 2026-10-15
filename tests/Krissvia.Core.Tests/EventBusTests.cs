using System.Collections.Concurrent;
using Krissvia.Core.Events;

namespace Krissvia.Core.Tests;

/// <summary>Le bus d'événements : ordre, isolement des abonnés, publication qui ne bloque jamais.</summary>
public sealed class EventBusTests : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Abonne, Exception Erreur)> _erreurs = new();
    private readonly EventBus _bus;

    public EventBusTests() => _bus = new EventBus((abonne, ex) => _erreurs.Enqueue((abonne, ex)));

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    private static PluginConnectionChanged Plugin(string id) => new(id, Connected: true);

    internal static async Task AttendreAsync(Func<bool> condition, string quoi = "")
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limite) Assert.Fail($"Condition jamais remplie {quoi}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Un_abonne_recoit_ses_evenements_dans_l_ordre_de_publication()
    {
        var recus = new ConcurrentQueue<string>();
        using var _ = _bus.Subscribe<PluginConnectionChanged>("test", (e, _) => { recus.Enqueue(e.PluginId); return Task.CompletedTask; });

        for (var i = 0; i < 100; i++) _bus.Publish(Plugin($"p{i}"));

        await AttendreAsync(() => recus.Count == 100);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"p{i}"), recus);
    }

    [Fact]
    public async Task Un_abonne_ne_recoit_que_son_type_et_un_abonne_au_type_de_base_recoit_tout()
    {
        var plugins = 0;
        var tous = 0;
        using var a = _bus.Subscribe<PluginConnectionChanged>("plugins", (_, _) => { Interlocked.Increment(ref plugins); return Task.CompletedTask; });
        using var b = _bus.Subscribe<CoreEvent>("tous", (_, _) => { Interlocked.Increment(ref tous); return Task.CompletedTask; });

        _bus.Publish(Plugin("z2m"));
        _bus.Publish(new RegistryChanged());

        await AttendreAsync(() => tous == 2);
        await Task.Delay(50);
        Assert.Equal(1, plugins);
    }

    [Fact]
    public async Task Une_exception_est_signalee_et_n_arrete_ni_l_abonne_ni_les_autres()
    {
        var recusParLeFragile = new ConcurrentQueue<string>();
        var recusParLAutre = 0;
        using var fragile = _bus.Subscribe<PluginConnectionChanged>("fragile", (e, _) =>
        {
            if (e.PluginId == "boum") throw new InvalidOperationException("boum");
            recusParLeFragile.Enqueue(e.PluginId);
            return Task.CompletedTask;
        });
        using var autre = _bus.Subscribe<PluginConnectionChanged>("autre", (_, _) => { Interlocked.Increment(ref recusParLAutre); return Task.CompletedTask; });

        _bus.Publish(Plugin("boum"));
        _bus.Publish(Plugin("suivant"));

        await AttendreAsync(() => recusParLeFragile.Count == 1 && recusParLAutre == 2);
        Assert.Equal("suivant", Assert.Single(recusParLeFragile));
        Assert.Equal("fragile", Assert.Single(_erreurs).Abonne);
    }

    [Fact]
    public async Task Un_abonne_bloque_ne_bloque_ni_la_publication_ni_les_autres_abonnes()
    {
        // Le piège de HaDiscoveryService dans DomoMud : un abonné qui attend une ressource que tient le publieur.
        var portail = new TaskCompletionSource();
        var recusParLAutre = 0;
        using var lent = _bus.Subscribe<PluginConnectionChanged>("lent", async (_, ct) => await portail.Task.WaitAsync(ct));
        using var autre = _bus.Subscribe<PluginConnectionChanged>("autre", (_, _) => { Interlocked.Increment(ref recusParLAutre); return Task.CompletedTask; });

        var publication = Task.Run(() => { for (var i = 0; i < 10; i++) _bus.Publish(Plugin($"p{i}")); });

        await publication.WaitAsync(TimeSpan.FromSeconds(1));   // TimeoutException si Publish attendait l'abonné lent
        await AttendreAsync(() => recusParLAutre == 10);
        Assert.Equal(9, _bus.Diagnostics().Single(d => d.Subscriber == "lent").Pending);
        portail.SetResult();
        await AttendreAsync(() => _bus.Diagnostics().Single(d => d.Subscriber == "lent").Pending == 0);
    }

    [Fact]
    public async Task Apres_desabonnement_plus_rien_n_arrive()
    {
        var recus = 0;
        var abonnement = _bus.Subscribe<PluginConnectionChanged>("test", (_, _) => { Interlocked.Increment(ref recus); return Task.CompletedTask; });
        _bus.Publish(Plugin("avant"));
        await AttendreAsync(() => recus == 1);

        abonnement.Dispose();
        _bus.Publish(Plugin("apres"));

        await Task.Delay(50);
        Assert.Equal(1, recus);
        Assert.Empty(_bus.Diagnostics());
    }
}
