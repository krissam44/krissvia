using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.PluginHost.Tests;

/// <summary>Ce que reçoit le cœur : déclarations, mesures, joignabilité, et les fautes du plugin.</summary>
public class ReceptionTests
{
    [Fact]
    public async Task Une_declaration_puis_une_mesure_arrivent_dans_le_registre_et_le_magasin()
    {
        await using var banc = await BancPlugins.CreerAsync();

        await banc.ConnecterAvecPriseAsync();

        var entite = await banc.Registre.GetAsync(BancPlugins.Puissance);
        Assert.NotNull(entite);
        Assert.Equal("zigbee2mqtt", entite.IntegrationId);
        Assert.Equal(1200.5, banc.Etats.Get(BancPlugins.Puissance)!.Value.GetDouble());
    }

    [Fact]
    public async Task L_instant_de_mesure_du_plugin_est_conserve()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();
        var mesure = banc.Horloge.GetUtcNow().AddMinutes(-3);

        await plugin.EnvoyerAsync(new StateBatch
        {
            States = [new EntityState { EntityUniqueId = "0x01:power", Value = JsonSerializer.SerializeToElement(80), Timestamp = mesure }],
        });

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance)!.MeasuredAt == mesure);
    }

    [Fact]
    public async Task Une_mesure_d_entite_non_declaree_est_ignoree_sans_couper_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        await plugin.EnvoyerAsync(BancPlugins.Mesures(("0xFF:inconnue", 1), ("0x01:power", 42)));

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance)!.Value.GetDouble() == 42);
        Assert.Equal(2, banc.Etats.GetAll().Count);
    }

    [Fact]
    public async Task Un_type_inconnu_et_une_trame_illisible_sont_ignores_sans_couper_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        await plugin.SendAsync("""{"type":"message_du_futur","x":1}""", CancellationToken.None);
        await plugin.SendAsync("{pas du json", CancellationToken.None);
        await plugin.SendAsync("""{"type":"state"}""", CancellationToken.None);
        await plugin.EnvoyerAsync(BancPlugins.Mesures(("0x01:power", 7)));

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance)!.Value.GetDouble() == 7);
    }

    [Fact]
    public async Task Une_entite_qui_n_est_plus_declaree_devient_indisponible()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();
        var sansPuissance = BancPlugins.Prise() with { Entities = [BancPlugins.Prise().Entities[0]] };

        await plugin.EnvoyerAsync(new Declare { Devices = [sansPuissance] });

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance) is { Available: false });
        Assert.True(banc.Etats.Get(BancPlugins.Interrupteur)!.Available);
    }

    [Fact]
    public async Task Un_appareil_hors_ligne_rend_ses_entites_indisponibles()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        await plugin.EnvoyerAsync(new Availability { DeviceUniqueId = "0x01", Online = false });

        await BancPlugins.AttendreAsync(() =>
            banc.Etats.Get(BancPlugins.Interrupteur) is { Available: false } &&
            banc.Etats.Get(BancPlugins.Puissance) is { Available: false });
    }

    [Fact]
    public async Task Une_seule_entite_peut_etre_declaree_hors_ligne()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        await plugin.EnvoyerAsync(new Availability { EntityUniqueId = "0x01:power", Online = false });
        await plugin.EnvoyerAsync(new Ping());

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance) is { Available: false });
        Assert.True(banc.Etats.Get(BancPlugins.Interrupteur)!.Available);
    }

    [Fact]
    public async Task Le_retour_en_ligne_ne_rend_disponible_que_ce_qui_a_ete_mesure_pendant_la_session()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAsync();
        await plugin.EnvoyerAsync(new Declare { Devices = [BancPlugins.Prise()] });
        await BancPlugins.AttendreAsync(() => banc.Registre.GetAsync(BancPlugins.Puissance).Result is not null);

        // L'interrupteur n'a qu'une valeur ancienne (restaurée au démarrage) ; seule la puissance est mesurée.
        banc.Etats.Update(BancPlugins.Interrupteur, JsonSerializer.SerializeToElement(true));
        banc.Etats.SetAvailability(BancPlugins.Interrupteur, false);
        await plugin.EnvoyerAsync(BancPlugins.Mesures(("0x01:power", 50)));
        await plugin.EnvoyerAsync(new Availability { DeviceUniqueId = "0x01", Online = false });
        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance) is { Available: false });

        await plugin.EnvoyerAsync(new Availability { DeviceUniqueId = "0x01", Online = true });

        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Puissance) is { Available: true });
        Assert.False(banc.Etats.Get(BancPlugins.Interrupteur)!.Available);
    }

    [Fact]
    public async Task Le_plugin_hors_ligne_rend_toutes_ses_entites_indisponibles()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = await banc.ConnecterAvecPriseAsync();

        await plugin.EnvoyerAsync(new Availability { Online = false });

        await BancPlugins.AttendreAsync(() => banc.Etats.GetAll().Values.All(e => !e.Available));
        Assert.Contains("zigbee2mqtt", banc.Hub.ConnectedPlugins);
    }
}
