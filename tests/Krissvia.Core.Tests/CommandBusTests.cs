using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>
/// Le bus de commandes sur le vrai registre, le vrai magasin d'états et le vrai bus d'événements. Seul le
/// transport vers les plugins est simulé : c'est lui qui quitte la machine.
/// </summary>
public sealed class CommandBusTests : IAsyncDisposable
{
    private const string Radiateur = "switch.radiateur_salon";
    private const string Lampe = "light.lampe_salon";
    private const string Sonde = "sensor.sonde_salon_temperature";

    private readonly FakeTimeProvider _horloge = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
    private readonly EventBus _evenements = new();
    private readonly EntityRegistry _registre;
    private readonly EntityStateStore _etats;
    private readonly FauxTransport _transport;
    private readonly CommandBus _bus;

    public CommandBusTests()
    {
        _registre = new EntityRegistry(new InMemoryEntityRepository(), _evenements);
        _etats = new EntityStateStore(new MagasinVide(), _horloge, _evenements);
        _transport = new FauxTransport(_etats);
        _bus = new CommandBus(_registre, _transport, _evenements, _horloge);

        _registre.RegisterAsync("test",
        [
            new DeviceDescriptor
            {
                UniqueId = "rad", Name = "Radiateur salon",
                Entities = [new EntityDescriptor { UniqueId = "rad:state", Kind = EntityKind.Switch, Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle] }],
            },
            new DeviceDescriptor
            {
                UniqueId = "lam", Name = "Lampe salon",
                Entities = [new EntityDescriptor { UniqueId = "lam:light", Kind = EntityKind.Light, Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.SetBrightness] }],
            },
            new DeviceDescriptor
            {
                UniqueId = "son", Name = "Sonde salon",
                Entities = [new EntityDescriptor { UniqueId = "son:t", Kind = EntityKind.Sensor, DeviceClass = DeviceClasses.Temperature }],
            },
        ]).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _bus.Dispose();
        await _evenements.DisposeAsync();
    }

    /// <summary>Transport simulé : enregistre les ordres, répond l'issue réglée, et peut faire « obéir » l'appareil.</summary>
    private sealed class FauxTransport(EntityStateStore etats) : ICommandDispatcher
    {
        public ConcurrentQueue<EntityCommand> Recus { get; } = new();
        public CommandOutcome Issue { get; set; } = CommandOutcome.Accepted;
        public Exception? Erreur { get; set; }

        /// <summary>État publié par l'appareil en réponse ; null = l'appareil ne republie rien.</summary>
        public Func<EntityCommand, object?>? Reponse { get; set; }

        public Task<CommandOutcome> DispatchAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct)
        {
            Recus.Enqueue(command);
            if (Erreur is not null) throw Erreur;
            if (Reponse?.Invoke(command) is { } etat) etats.Update(entity.EntityId, JsonSerializer.SerializeToElement(etat));
            return Task.FromResult(Issue);
        }
    }

    private sealed class MagasinVide : IEntityStateRepository
    {
        public Task<IReadOnlyList<PersistedEntityState>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PersistedEntityState>>([]);

        public Task SaveAsync(IReadOnlyList<PersistedEntityState> states, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Avance l'horloge jusqu'à ce que la tâche se termine : son délai n'est armé qu'après l'envoi.</summary>
    private async Task<T> AvancerJusquAAsync<T>(Task<T> tache)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!tache.IsCompleted)
        {
            if (DateTime.UtcNow > limite) Assert.Fail("La tâche ne s'est jamais terminée.");
            _horloge.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        return await tache;
    }

    private static EntityCommand Ordre(string entityId, string commande, CommandPriority priorite = CommandPriority.Normal) =>
        new(entityId, commande) { Priority = priorite, Origin = "test" };

    // ── Contrôles ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Une_entite_inconnue_echoue_sans_rien_envoyer()
    {
        Assert.Equal(CommandOutcome.Failed, await _bus.SendAsync("switch.radiateur_chbre_rdc", StandardCommands.TurnOff));
        Assert.Empty(_transport.Recus);
    }

    [Fact]
    public async Task Une_commande_non_declaree_echoue_sans_rien_envoyer()
    {
        Assert.Equal(CommandOutcome.Failed, await _bus.SendAsync(Sonde, StandardCommands.TurnOn));
        Assert.Equal(CommandOutcome.Failed, await _bus.SendAsync(Lampe, StandardCommands.Toggle));
        Assert.Empty(_transport.Recus);
    }

    [Fact]
    public async Task Une_entite_desactivee_refuse_un_ordre_normal_mais_jamais_un_ordre_de_securite()
    {
        await _registre.SetEnabledAsync(Radiateur, false);

        Assert.Equal(CommandOutcome.Failed, await _bus.SendAsync(Radiateur, StandardCommands.TurnOn));
        Assert.Empty(_transport.Recus);

        Assert.Equal(CommandOutcome.Accepted, await _bus.SendAsync(Radiateur, StandardCommands.TurnOff, CommandPriority.Safety));
        Assert.Single(_transport.Recus);
    }

    [Fact]
    public async Task Une_exception_du_transport_devient_un_echec()
    {
        _transport.Erreur = new IOException("prise débranchée");

        Assert.Equal(CommandOutcome.Failed, await _bus.SendAsync(Radiateur, StandardCommands.TurnOff));
        Assert.Equal("prise débranchée", _bus.RecentCommands[0].Detail);
    }

    [Fact]
    public async Task L_ordre_part_complet_vers_le_transport()
    {
        var args = JsonSerializer.SerializeToElement(new { brightness = 40 });

        await _bus.SendAsync(new EntityCommand(Lampe, StandardCommands.SetBrightness)
        {
            Args = args, Priority = CommandPriority.Safety, TtlSeconds = 5, Origin = "api",
        });

        var recu = Assert.Single(_transport.Recus);
        Assert.Equal(40, recu.Args!.Value.GetProperty("brightness").GetInt32());
        Assert.Equal((CommandPriority.Safety, 5, "api"), (recu.Priority, recu.TtlSeconds, recu.Origin));
    }

    [Fact]
    public async Task Chaque_ordre_est_journalise_et_publie_sur_le_bus()
    {
        var publies = new ConcurrentQueue<CommandRecord>();
        using var _ = _evenements.Subscribe<CommandCompleted>("test", (e, _) => { publies.Enqueue(e.Record); return Task.CompletedTask; });

        await _bus.SendAsync(Ordre(Radiateur, StandardCommands.TurnOn));
        await _bus.SendAsync(Ordre(Sonde, StandardCommands.TurnOn));

        Assert.Equal([CommandOutcome.Failed, CommandOutcome.Accepted], _bus.RecentCommands.Select(c => c.Outcome));
        await EventBusTests.AttendreAsync(() => publies.Count == 2);
    }

    // ── Vérification (§7.3) ──────────────────────────────────────────────────

    [Fact]
    public async Task Un_etat_conforme_publie_apres_l_ordre_le_confirme()
    {
        _transport.Reponse = _ => false;

        var resultat = await _bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.TurnOff, CommandPriority.Safety));

        Assert.True(resultat.Confirmed);
    }

    [Fact]
    public async Task Sans_etat_publie_l_ordre_accepte_n_est_pas_confirme()
    {
        var resultat = await AvancerJusquAAsync(_bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.TurnOff)));

        Assert.Equal(CommandOutcome.Accepted, resultat.Outcome);
        Assert.Equal(CommandVerification.NotConfirmed, resultat.Verification);
    }

    [Fact]
    public async Task L_etat_deja_connu_ne_confirme_rien()
    {
        // Le cœur CROIT le radiateur éteint ; le relais, lui, n'a rien republié après l'ordre.
        _etats.Update(Radiateur, JsonSerializer.SerializeToElement(false));
        await Task.Delay(50);

        var resultat = await AvancerJusquAAsync(_bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.TurnOff, CommandPriority.Safety)));

        Assert.Equal(CommandVerification.NotConfirmed, resultat.Verification);
    }

    [Fact]
    public async Task Un_relais_qui_reste_allume_n_est_pas_confirme()
    {
        _transport.Reponse = _ => true;

        var resultat = await AvancerJusquAAsync(_bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.TurnOff, CommandPriority.Safety)));

        Assert.Equal(CommandVerification.NotConfirmed, resultat.Verification);
    }

    [Fact]
    public async Task Une_lumiere_eteinte_se_confirme_par_son_champ_on()
    {
        _transport.Reponse = _ => new { on = false, brightness = 40 };

        Assert.True((await _bus.SendVerifiedAsync(Ordre(Lampe, StandardCommands.TurnOff))).Confirmed);
    }

    [Fact]
    public async Task Un_ordre_non_accepte_n_est_pas_verifie()
    {
        _transport.Issue = CommandOutcome.Unreachable;

        var resultat = await _bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.TurnOff, CommandPriority.Safety));

        Assert.Equal((CommandOutcome.Unreachable, CommandVerification.NotSent), (resultat.Outcome, resultat.Verification));
    }

    [Fact]
    public async Task Une_bascule_n_a_pas_d_etat_attendu()
    {
        var resultat = await _bus.SendVerifiedAsync(Ordre(Radiateur, StandardCommands.Toggle));

        Assert.Equal(CommandVerification.NotApplicable, resultat.Verification);
    }
}
