using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Tests;

public class CoreCommandRouterTests
{
    private sealed class Destinataire : ICommandDispatcher, ICoreCommandHandler
    {
        public List<string> Recus { get; } = [];

        public Task<CommandOutcome> DispatchAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct) => Noter(entity);

        public Task<CommandOutcome> HandleAsync(RegisteredEntity entity, EntityCommand command, CancellationToken ct) => Noter(entity);

        private Task<CommandOutcome> Noter(RegisteredEntity entity)
        {
            Recus.Add(entity.EntityId);
            return Task.FromResult(CommandOutcome.Accepted);
        }
    }

    private static RegisteredEntity Entite(string id, string integration) => new()
    {
        EntityId = id, DeviceId = "d", IntegrationId = integration, UniqueId = id, Kind = EntityKind.Switch, DisplayName = id,
    };

    private static readonly EntityCommand Ordre = new("x", "turn_on");

    [Fact]
    public async Task L_entite_d_un_plugin_va_au_plugin()
    {
        var plugins = new Destinataire();
        var module = new Destinataire();
        var routeur = new CoreCommandRouter(plugins);
        routeur.Register(CoreIntegrations.Thermostats, module);

        await routeur.DispatchAsync(Entite("switch.prise", "zigbee2mqtt"), Ordre, CancellationToken.None);

        Assert.Equal(["switch.prise"], plugins.Recus);
        Assert.Empty(module.Recus);
    }

    [Fact]
    public async Task L_entite_d_un_module_du_coeur_va_au_module()
    {
        var plugins = new Destinataire();
        var module = new Destinataire();
        var routeur = new CoreCommandRouter(plugins);
        routeur.Register(CoreIntegrations.Thermostats, module);

        await routeur.DispatchAsync(Entite("climate.salon", CoreIntegrations.Thermostats), Ordre, CancellationToken.None);

        Assert.Equal(["climate.salon"], module.Recus);
        Assert.Empty(plugins.Recus);
    }

    [Fact]
    public async Task Un_module_pas_demarre_est_injoignable_et_l_ordre_ne_part_pas_ailleurs()
    {
        var plugins = new Destinataire();
        var routeur = new CoreCommandRouter(plugins);

        var issue = await routeur.DispatchAsync(Entite("climate.salon", CoreIntegrations.Thermostats), Ordre, CancellationToken.None);

        Assert.Equal(CommandOutcome.Unreachable, issue);
        Assert.Empty(plugins.Recus);
    }

    [Fact]
    public void Seule_une_integration_du_coeur_peut_s_inscrire()
    {
        var routeur = new CoreCommandRouter(new Destinataire());

        Assert.Throws<ArgumentException>(() => routeur.Register("zigbee2mqtt", new Destinataire()));
    }
}
