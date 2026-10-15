using Krissvia.Contracts;

namespace Krissvia.PluginHost.Tests;

/// <summary>hello → welcome ou rejected : la négociation que la v0.1 n'avait pas.</summary>
public class OuvertureSessionTests
{
    [Fact]
    public async Task Un_hello_valide_recoit_welcome_avec_la_version_et_les_delais()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello());
        var accueil = await plugin.RecevoirAsync<Welcome>();

        Assert.Equal(ContractInfo.Version, accueil.ContractVersion);
        Assert.Equal(15, accueil.HeartbeatSeconds);
        Assert.Equal(45, accueil.TimeoutSeconds);
        await BancPlugins.AttendreAsync(() => banc.Hub.ConnectedPlugins.Contains("zigbee2mqtt"));
    }

    [Fact]
    public async Task Une_version_de_contrat_incompatible_est_refusee_et_la_connexion_fermee()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello(version: "0.1"));

        var refus = await plugin.RecevoirAsync<Rejected>();
        Assert.Equal(RejectReason.IncompatibleContract, refus.Reason);
        Assert.Null(await plugin.RecevoirAsync());
        await session;
        Assert.Empty(banc.Hub.ConnectedPlugins);
    }

    [Fact]
    public async Task Un_jeton_faux_est_refuse()
    {
        await using var banc = await BancPlugins.CreerAsync();
        banc.Options.Tokens["zigbee2mqtt"] = "secret";
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello(jeton: "secreT"));

        Assert.Equal(RejectReason.InvalidToken, (await plugin.RecevoirAsync<Rejected>()).Reason);
    }

    [Fact]
    public async Task Un_plugin_declare_sans_jeton_est_refuse()
    {
        await using var banc = await BancPlugins.CreerAsync();
        banc.Options.Tokens["zigbee2mqtt"] = "secret";
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello());

        Assert.Equal(RejectReason.InvalidToken, (await plugin.RecevoirAsync<Rejected>()).Reason);
    }

    [Fact]
    public async Task Le_bon_jeton_ouvre_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        banc.Options.Tokens["zigbee2mqtt"] = "secret";

        await banc.ConnecterAsync(jeton: "secret");
    }

    [Fact]
    public async Task Par_defaut_un_plugin_non_declare_est_refuse()
    {
        await using var banc = await BancPlugins.CreerAsync();
        banc.Options.AllowUnregisteredPlugins = false;
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello());

        Assert.Equal(RejectReason.UnknownPlugin, (await plugin.RecevoirAsync<Rejected>()).Reason);
    }

    [Fact]
    public async Task Une_premiere_trame_qui_n_est_pas_hello_est_refusee()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(new Ping());

        Assert.Equal(RejectReason.ProtocolError, (await plugin.RecevoirAsync<Rejected>()).Reason);
    }

    [Theory]
    [InlineData("Zigbee2MQTT")]
    [InlineData("zigbee 2 mqtt")]
    [InlineData("")]
    public async Task Un_plugin_id_hors_format_est_refuse(string pluginId)
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello(pluginId));

        Assert.Equal(RejectReason.ProtocolError, (await plugin.RecevoirAsync<Rejected>()).Reason);
    }

    [Fact]
    public async Task Sans_hello_dans_le_delai_la_connexion_est_refusee()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, session) = banc.Ouvrir();

        await banc.AvancerAsync(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(1));

        Assert.Equal(RejectReason.ProtocolError, (await plugin.RecevoirAsync<Rejected>()).Reason);
        await session;
    }
}
