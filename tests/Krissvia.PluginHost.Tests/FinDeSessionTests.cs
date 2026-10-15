using Krissvia.Contracts;

namespace Krissvia.PluginHost.Tests;

/// <summary>Principe 3 de l'architecture : plugin mort ⇒ entités indisponibles ⇒ les failsafes du cœur prennent le relais.</summary>
public class FinDeSessionTests
{
    [Fact]
    public async Task Une_deconnexion_rend_les_entites_du_plugin_indisponibles()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = await banc.ConnecterAvecPriseAsync();

        await plugin.CloseAsync("coupure", CancellationToken.None);
        await session;

        Assert.All(banc.Etats.GetAll().Values, e => Assert.False(e.Available));
        Assert.Empty(banc.Hub.ConnectedPlugins);
    }

    [Fact]
    public async Task Un_arret_annonce_ferme_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = await banc.ConnecterAvecPriseAsync();

        await plugin.EnvoyerAsync(new Shutdown { Reason = "mise à jour" });
        await session;

        Assert.All(banc.Etats.GetAll().Values, e => Assert.False(e.Available));
    }

    [Fact]
    public async Task Le_coeur_envoie_un_ping_a_chaque_battement()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAsync();

        await banc.AvancerAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        await BancPlugins.AttendreAsync(() => plugin.TryLire(out var trame) && trame!.Contains("\"type\":\"ping\""));
    }

    [Fact]
    public async Task Un_plugin_muet_est_declare_mort_et_ses_entites_indisponibles()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (_, session) = await banc.ConnecterAvecPriseAsync();

        await banc.AvancerAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));

        await session.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(banc.Etats.GetAll().Values, e => Assert.False(e.Available));
    }

    [Fact]
    public async Task Un_plugin_qui_envoie_ses_pings_reste_connecte()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = await banc.ConnecterAvecPriseAsync();

        for (var i = 0; i < 8; i++)
        {
            await plugin.EnvoyerAsync(new Ping());
            await banc.AvancerAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        }

        Assert.False(session.IsCompleted);
        Assert.True(banc.Etats.Get(BancPlugins.Puissance)!.Available);
    }

    [Fact]
    public async Task Un_nouveau_hello_remplace_l_ancienne_session_sans_eteindre_les_entites()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (ancien, ancienneSession) = await banc.ConnecterAvecPriseAsync();

        var (nouveau, _) = await banc.ConnecterAsync();

        Assert.Null(await ancien.RecevoirAsync());
        await ancienneSession.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("zigbee2mqtt", banc.Hub.ConnectedPlugins);
        Assert.True(banc.Etats.Get(BancPlugins.Puissance)!.Available);

        // Et c'est bien la nouvelle session qui reçoit désormais les commandes.
        var commande = banc.Commandeur.SendAsync(BancPlugins.Interrupteur, StandardCommands.TurnOff);
        var ordre = await nouveau.RecevoirAsync<CommandRequest>();
        await nouveau.EnvoyerAsync(new CommandResult { Id = ordre.Id, Outcome = CommandOutcome.Accepted });
        Assert.Equal(CommandOutcome.Accepted, await commande);
    }
}
