using System.Text.Json;
using Krissvia.Climate.Presence;
using Krissvia.Contracts;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>La présence du foyer : les règles de DomoMud, dont le repli « occupée » quand la détection se tait.</summary>
public class HomePresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);

    private sealed class Ecrivain : IEntityStateWriter
    {
        public List<JsonElement> Publies { get; } = [];
        public void Update(string entityId, JsonElement value, DateTimeOffset? measuredAt = null) => Publies.Add(value);
        public void SetAvailability(string entityId, bool available) { }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FakeStateReader Etats { get; } = new();
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public Ecrivain Ecrivain { get; } = new();
        public HomePresence Presence { get; }

        public Banc(params string[] personnes)
        {
            Presence = new HomePresence(Registre, Etats, Ecrivain, Horloge);
            Registre.RegisterAsync("livebox",
            [
                .. personnes.Select(p => new DeviceDescriptor
                {
                    UniqueId = p, Name = p,
                    Entities = [new EntityDescriptor { UniqueId = p, Kind = EntityKind.BinarySensor, Name = "Présence", DeviceClass = DeviceClasses.Presence }],
                }),
                // Un détecteur de mouvement n'est pas une présence du foyer.
                new DeviceDescriptor
                {
                    UniqueId = "couloir", Name = "Couloir",
                    Entities = [new EntityDescriptor { UniqueId = "couloir", Kind = EntityKind.BinarySensor, DeviceClass = DeviceClasses.Occupancy }],
                },
            ]).GetAwaiter().GetResult();
        }

        public string Id(string personne) => Registre.Loaded.Single(e => e.UniqueId == personne).EntityId;

        public void Mettre(string personne, bool present, bool disponible = true) =>
            Etats.Set(Id(personne), present, Horloge.GetUtcNow(), disponible);
    }

    [Fact]
    public async Task Quelqu_un_de_present_suffit_et_les_noms_sont_ceux_des_appareils()
    {
        var b = new Banc("Christian", "Anne");
        b.Mettre("Christian", true);
        b.Mettre("Anne", false);
        b.Etats.Set(b.Registre.Loaded.Single(e => e.UniqueId == "couloir").EntityId, true, T0);

        var e = await b.Presence.UpdateAsync();

        Assert.True(e.Occupied);
        Assert.Equal(["Christian"], e.Present);
        Assert.Equal(["Anne"], e.Absent);
        Assert.Equal(2, e.Tracked);   // le couloir n'en fait pas partie
    }

    [Fact]
    public async Task Tous_absents_la_maison_est_vide_depuis_le_premier_constat()
    {
        var b = new Banc("Christian", "Anne");
        b.Mettre("Christian", false);
        b.Mettre("Anne", false);
        await b.Presence.UpdateAsync();

        b.Horloge.Advance(TimeSpan.FromMinutes(30));
        var e = await b.Presence.UpdateAsync();

        Assert.False(e.Occupied);
        Assert.Equal(T0, e.EmptySince);

        b.Mettre("Anne", true);
        var retour = await b.Presence.UpdateAsync();
        Assert.True(retour.Occupied);
        Assert.Null(retour.EmptySince);
    }

    [Fact]
    public async Task Une_detection_muette_ne_fait_jamais_une_maison_vide()
    {
        var b = new Banc("Christian", "Anne");
        b.Mettre("Christian", false);
        b.Mettre("Anne", false, disponible: false);   // box injoignable, plugin arrêté…

        var e = await b.Presence.UpdateAsync();

        Assert.True(e.Occupied);
        Assert.True(e.DetectionLost);
        Assert.Equal(["Anne"], e.Unknown);
        Assert.Null(e.EmptySince);
    }

    [Fact]
    public async Task Sans_appareil_suivi_on_ne_pretend_pas_que_la_maison_est_vide()
    {
        var e = await new Banc().Presence.UpdateAsync();

        Assert.True(e.Occupied);
        Assert.False(e.DetectionLost);
        Assert.Equal(0, e.Tracked);
    }

    [Fact]
    public async Task L_etat_est_publie_en_entite_seulement_quand_il_change()
    {
        var b = new Banc("Christian");
        b.Mettre("Christian", true);

        await b.Presence.UpdateAsync();
        await b.Presence.UpdateAsync();
        b.Mettre("Christian", false);
        await b.Presence.UpdateAsync();

        var entite = Assert.Single(b.Registre.Loaded, e => e.DeviceClass == DeviceClasses.HomePresence);
        Assert.Equal(("sensor.maison_presence", CoreIntegrationsPresence), (entite.EntityId, entite.IntegrationId));
        Assert.Equal([true, false], b.Ecrivain.Publies.Select(p => p.GetProperty("occupied").GetBoolean()));
        Assert.Equal("Christian", b.Ecrivain.Publies[1].GetProperty("absent")[0].GetString());
    }

    private const string CoreIntegrationsPresence = Krissvia.Core.Commands.CoreIntegrations.Presence;
}
