using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.PluginHost.Services;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Contrat 0.3 (docs/CONTRACT-V0.3.md) : une platine lit et commande des entités qui ne sont pas à elle — la prise du
/// plugin Zigbee —, seulement celles qu'on lui a confiées, et ses ordres passent par le bus de commandes.
/// </summary>
public class Contrat03Tests
{
    private const string Platine = "openhasp";

    [Fact]
    public async Task Un_plugin_0_2_reste_accueilli_et_se_voit_repondre_0_2()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (plugin, _) = banc.Ouvrir();

        await plugin.EnvoyerAsync(BancPlugins.Hello(version: "0.2"));

        Assert.Equal("0.2", (await plugin.RecevoirAsync<Welcome>()).ContractVersion);
    }

    [Fact]
    public async Task L_abonnement_ne_donne_que_les_entites_confiees_et_suit_leurs_changements()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (zigbee, _) = await banc.ConnecterAvecPriseAsync();
        banc.Droits[(Platine, BancPlugins.Interrupteur)] = EntityAccess.Read;
        banc.Droits[(Platine, "switch.inexistant")] = EntityAccess.Read;
        var (platine, _) = await banc.ConnecterAsync(Platine);

        // La puissance existe, mais n'a pas été confiée : la platine n'apprend même pas qu'elle existe.
        await platine.EnvoyerAsync(new Subscribe { EntityIds = [BancPlugins.Interrupteur, BancPlugins.Puissance, "switch.inexistant", "switch.fantome"] });

        var reponse = await platine.RecevoirAsync<Subscribed>();
        Assert.Equal([BancPlugins.Interrupteur], reponse.Granted);
        Assert.Equal(
        [
            new DeniedEntity(BancPlugins.Puissance, SubscriptionDenial.NotAllowed),
            new DeniedEntity("switch.inexistant", SubscriptionDenial.Unknown),
            new DeniedEntity("switch.fantome", SubscriptionDenial.NotAllowed),   // inconnue ET non confiée : on ne le dit pas
        ], reponse.Denied);

        var etat = await platine.RecevoirAsync<EntityStateUpdate>();
        Assert.Equal(BancPlugins.Interrupteur, etat.EntityId);
        Assert.True(etat.Value!.Value.GetBoolean());
        Assert.True(etat.Available);

        // La puissance change : rien. L'interrupteur change : poussé.
        await zigbee.EnvoyerAsync(BancPlugins.Mesures(("0x01:power", 15.0), ("0x01:switch", false)));
        var suivant = await platine.RecevoirAsync<EntityStateUpdate>();
        Assert.Equal(BancPlugins.Interrupteur, suivant.EntityId);
        Assert.False(suivant.Value!.Value.GetBoolean());
    }

    [Fact]
    public async Task Un_droit_retire_coupe_les_changements_sans_attendre_un_nouvel_abonnement()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (zigbee, _) = await banc.ConnecterAvecPriseAsync();
        banc.Droits[(Platine, BancPlugins.Interrupteur)] = EntityAccess.Read;
        var (platine, _) = await banc.ConnecterAsync(Platine);
        await platine.EnvoyerAsync(new Subscribe { EntityIds = [BancPlugins.Interrupteur] });
        await platine.RecevoirAsync<Subscribed>();
        await platine.RecevoirAsync<EntityStateUpdate>();

        banc.Droits.Remove((Platine, BancPlugins.Interrupteur));   // retiré des réglages
        await zigbee.EnvoyerAsync(BancPlugins.Mesures(("0x01:switch", false)));
        await BancPlugins.AttendreAsync(() => banc.Etats.Get(BancPlugins.Interrupteur)?.Value.GetBoolean() == false);
        await Task.Delay(200);

        Assert.False(platine.TryLire(out var trame) && trame!.Contains("entity_state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_ordre_de_la_platine_passe_par_le_bus_jusqu_au_plugin_qui_porte_l_entite()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (zigbee, _) = await banc.ConnecterAvecPriseAsync();
        banc.Droits[(Platine, BancPlugins.Interrupteur)] = EntityAccess.Command;
        var (platine, _) = await banc.ConnecterAsync(Platine);

        await platine.EnvoyerAsync(new EntityCommandRequest { Id = "o1", EntityId = BancPlugins.Interrupteur, Command = StandardCommands.TurnOff });

        // Le plugin Zigbee reçoit l'ordre comme n'importe quel autre, l'exécute et publie l'état qui le confirme.
        var ordre = await zigbee.RecevoirAsync<CommandRequest>();
        Assert.Equal("0x01:switch", ordre.EntityUniqueId);
        await zigbee.EnvoyerAsync(new CommandResult { Id = ordre.Id, Outcome = CommandOutcome.Accepted });
        await zigbee.EnvoyerAsync(BancPlugins.Mesures(("0x01:switch", false)));

        var reponse = await platine.RecevoirAsync<EntityCommandResponse>();
        Assert.Equal(new EntityCommandResponse { Id = "o1", Outcome = CommandOutcome.Accepted, Verification = "confirmed" }, reponse);
        Assert.Contains(banc.Commandeur.RecentCommands, c => c.Command.Origin == "plugin:openhasp" && c.Command.EntityId == BancPlugins.Interrupteur);
    }

    [Fact]
    public async Task Lire_ne_permet_pas_de_commander()
    {
        await using var banc = await BancPlugins.CreerAsync();
        var (zigbee, _) = await banc.ConnecterAvecPriseAsync();
        banc.Droits[(Platine, BancPlugins.Interrupteur)] = EntityAccess.Read;
        var (platine, _) = await banc.ConnecterAsync(Platine);

        await platine.EnvoyerAsync(new EntityCommandRequest { Id = "o2", EntityId = BancPlugins.Interrupteur, Command = StandardCommands.TurnOn });

        var reponse = await platine.RecevoirAsync<EntityCommandResponse>();
        Assert.Equal(CommandOutcome.Failed, reponse.Outcome);
        Assert.Equal("not_sent", reponse.Verification);
        Assert.Contains("non autorisée", reponse.Detail);
        await Task.Delay(100);
        Assert.False(zigbee.TryLire(out var trame) && trame!.Contains("\"command\"", StringComparison.Ordinal));
    }

    // ── Réglages : choisir l'entité, c'est l'autoriser ───────────────────────

    private static readonly SettingField Schema = SettingsSchema.Parse(JsonDocument.Parse("""
        { "type": "object", "properties": {
            "Platines": { "type": "array", "items": { "type": "object", "properties": {
                "Node": { "type": "string" },
                "Thermostat": { "type": "string", "format": "entity", "x-kinds": ["climate"], "x-access": "command" },
                "Lampes": { "type": "array", "items": { "type": "object", "properties": {
                    "Entite": { "type": "string", "format": "entity", "x-kinds": ["switch", "light"], "x-access": "command" } } } } } } },
            "Sondes": { "type": "array", "items": { "type": "object", "properties": {
                "Entite": { "type": "string", "format": "entity", "x-kinds": ["sensor"], "x-device-classes": ["temperature"] } } } },
            "Exterieur": { "type": "string", "format": "entity" }
        } }
        """).RootElement)!;

    [Fact]
    public void Les_entites_choisies_n_importe_ou_dans_les_reglages_font_l_autorisation()
    {
        var valeurs = JsonNode.Parse("""
            { "Platines": [
                { "Node": "appart1", "Thermostat": "climate.salon", "Lampes": [ { "Entite": "switch.lampe_salon" } ] },
                { "Node": "appart2", "Thermostat": "climate.chambre", "Lampes": [] } ],
              "Sondes": [ { "Entite": "sensor.salon" }, { "Entite": "climate.salon" }, { "Entite": "" } ],
              "Exterieur": "sensor.dehors" }
            """)!.AsObject();

        var droits = SettingsSchema.EntityReferences(Schema, valeurs);

        Assert.Equal(new Dictionary<string, EntityAccess>
        {
            ["climate.salon"] = EntityAccess.Command,   // aussi en sonde (lecture) : le plus large l'emporte
            ["switch.lampe_salon"] = EntityAccess.Command,
            ["climate.chambre"] = EntityAccess.Command,
            ["sensor.salon"] = EntityAccess.Read,
            ["sensor.dehors"] = EntityAccess.Read,
        }, droits);
        Assert.Equal(["climate"], Schema.Properties[0].Properties[1].Entity!.Kinds);
        Assert.Null(Schema.Properties[0].Properties[0].Entity);   // un texte ordinaire n'est pas une entité
    }
}
