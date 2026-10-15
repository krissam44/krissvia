using Krissvia.Contracts;

namespace Krissvia.PluginHost.Tests;

/// <summary>Le commandeur ne masque jamais un échec : sans réponse, un ordre n'est pas supposé passé.</summary>
public class CommandesTests
{
    [Fact]
    public async Task Une_commande_part_vers_le_plugin_et_rend_son_resultat()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff, CommandPriority.Safety, ttlSeconds: 5);

        var ordre = await plugin.RecevoirAsync<CommandRequest>();
        Assert.Equal("0x01:switch", ordre.EntityUniqueId);
        Assert.Equal(StandardCommands.TurnOff, ordre.Command);
        Assert.Equal(CommandPriority.Safety, ordre.Priority);
        Assert.Equal(5, ordre.TtlSeconds);

        await plugin.EnvoyerAsync(new CommandResult { Id = ordre.Id, Outcome = CommandOutcome.Failed, Detail = "relais bloqué" });
        Assert.Equal(CommandOutcome.Failed, await commande);
    }

    [Fact]
    public async Task Sans_reponse_dans_le_delai_la_commande_est_injoignable()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOn);
        await plugin.RecevoirAsync<CommandRequest>();
        await banc.AvancerAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.Unreachable, await commande.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Le_ttl_borne_l_attente_du_resultat()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff, ttlSeconds: 3);
        await plugin.RecevoirAsync<CommandRequest>();
        await banc.AvancerAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.Unreachable, await commande.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Une_session_qui_se_ferme_pendant_l_attente_rend_injoignable()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff);
        await plugin.RecevoirAsync<CommandRequest>();
        await plugin.CloseAsync("plantage", CancellationToken.None);

        Assert.Equal(CommandOutcome.Unreachable, await commande.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Un_plugin_deconnecte_rend_injoignable()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = await banc.ConnecterAvecPriseAsync();
        await plugin.CloseAsync("arrêt", CancellationToken.None);
        await session;

        Assert.Equal(CommandOutcome.Unreachable, await banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff));
    }

    [Fact]
    public async Task Une_entite_inconnue_echoue()
    {
        await using var banc = await BancPlugins.CreerAsync();
        await banc.ConnecterAvecPriseAsync();

        Assert.Equal(CommandOutcome.Failed, await banc.Commandeur.SendAsync("switch.radiateur_chbre_rdc", StandardCommands.TurnOn));
    }

    [Fact]
    public async Task Un_resultat_inattendu_n_affecte_pas_les_commandes_en_cours()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOn);
        var ordre = await plugin.RecevoirAsync<CommandRequest>();
        await plugin.EnvoyerAsync(new CommandResult { Id = "autre", Outcome = CommandOutcome.Failed });
        await plugin.EnvoyerAsync(new CommandResult { Id = ordre.Id, Outcome = CommandOutcome.Accepted });

        Assert.Equal(CommandOutcome.Accepted, await commande);
    }
}
