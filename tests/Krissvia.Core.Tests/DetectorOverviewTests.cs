using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Overview;
using Krissvia.Core.Registry;
using Krissvia.Mobile.Client;

namespace Krissvia.Core.Tests;

/// <summary><c>GET /api/detectors</c> : les règles du cœur, et ce que l'appli mobile en lit vraiment.</summary>
public class DetectorOverviewTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private static RegisteredEntity E(string id, EntityKind kind, string? classe, string appareil, bool active = true) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "p", UniqueId = id, Kind = kind, DisplayName = id,
        DeviceClass = classe, Enabled = active,
    };

    private static RegisteredDevice D(string id, string? piece) => new()
    {
        DeviceId = id, IntegrationId = "p", UniqueId = id, DisplayName = id, Room = piece,
    };

    private static readonly RegisteredDevice[] Appareils = [D("baie", "Séjour"), D("radar", "Séjour"), D("porte", "Entrée"), D("velux", null)];

    private static IReadOnlyList<RegisteredEntity> Entites() =>
    [
        E("binary_sensor.radar", EntityKind.BinarySensor, DeviceClasses.Occupancy, "radar"),
        E("binary_sensor.baie", EntityKind.BinarySensor, DeviceClasses.Opening, "baie"),
        E("sensor.baie_batterie", EntityKind.Sensor, DeviceClasses.Battery, "baie"),
        E("binary_sensor.porte", EntityKind.BinarySensor, DeviceClasses.Opening, "porte"),
        E("binary_sensor.velux", EntityKind.BinarySensor, DeviceClasses.Opening, "velux"),
        E("binary_sensor.eteinte", EntityKind.BinarySensor, DeviceClasses.Opening, "porte", active: false),
        E("binary_sensor.connexion", EntityKind.BinarySensor, DeviceClasses.Connectivity, "porte"),
    ];

    [Fact]
    public void Ouvertures_et_detecteurs_par_piece_les_mouvements_apres_les_ouvertures()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("binary_sensor.radar", true, Maintenant);
        etats.Fixer("binary_sensor.baie", true, Maintenant.AddMinutes(-3));
        etats.Fixer("sensor.baie_batterie", 64.0, Maintenant);
        etats.Fixer("binary_sensor.porte", false, Maintenant.AddHours(-2));

        var vue = DetectorOverview.Build(Entites(), Appareils, etats, new HashSet<string> { "binary_sensor.porte" });

        // Entrée, puis Séjour (ouverture avant radar), puis sans pièce ; ni l'entité désactivée ni la connectivité.
        Assert.Equal(["binary_sensor.porte", "binary_sensor.baie", "binary_sensor.radar", "binary_sensor.velux"], vue.Select(d => d.Entity.EntityId));
        var baie = vue.Single(d => d.Entity.EntityId == "binary_sensor.baie");
        Assert.True(baie.Active);
        Assert.Equal(64.0, baie.Battery);
        Assert.Equal(Maintenant.AddMinutes(-3), baie.Since);
        Assert.True(vue.Single(d => d.Entity.EntityId == "binary_sensor.radar").IsMotion);
        Assert.True(vue.Single(d => d.Entity.EntityId == "binary_sensor.porte").Monitored);
        Assert.False(baie.Monitored);
    }

    [Fact]
    public void Un_capteur_sans_nouvelles_n_est_ni_ferme_ni_calme()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("binary_sensor.porte", false, Maintenant, disponible: false);

        var vue = DetectorOverview.Build(Entites(), Appareils, etats, new HashSet<string>());

        var porte = vue.Single(d => d.Entity.EntityId == "binary_sensor.porte");
        Assert.Equal(DetectorHealth.Offline, porte.Health);
        Assert.Null(porte.Active);
        var velux = vue.Single(d => d.Entity.EntityId == "binary_sensor.velux");
        Assert.Equal(DetectorHealth.NeverReceived, velux.Health);
        Assert.Null(velux.Active);
    }

    [Fact]
    public void Depuis_quand_est_le_dernier_changement_pas_la_derniere_publication()
    {
        // Un radar republie « calme » toutes les minutes : la carte doit dire depuis quand c'est calme.
        var etats = new EtatsEnMemoire();
        etats.Etats["binary_sensor.radar"] = new States.EntityStateSnapshot(
            JsonSerializer.SerializeToElement(false), Maintenant, ChangedAt: Maintenant.AddHours(-3));

        var radar = DetectorOverview.Build(Entites(), Appareils, etats, new HashSet<string>()).Single(d => d.Entity.EntityId == "binary_sensor.radar");

        Assert.Equal(Maintenant.AddHours(-3), radar.Since);
    }

    [Fact]
    public void L_appli_mobile_lit_ce_que_le_coeur_serialise()
    {
        var etats = new EtatsEnMemoire();
        etats.Fixer("binary_sensor.radar", true, Maintenant);
        etats.Fixer("binary_sensor.baie", false, Maintenant.AddMinutes(-3));
        etats.Fixer("sensor.baie_batterie", 64.0, Maintenant);
        etats.Fixer("binary_sensor.porte", true, Maintenant, disponible: false);
        var json = JsonSerializer.SerializeToElement(
            DetectorOverviewView.From(DetectorOverview.Build(Entites(), Appareils, etats, new HashSet<string> { "binary_sensor.baie" })),
            DetectorOverviewView.Json);

        var vue = VueDetecteurs.Lire(json)!;

        var baie = vue.Detecteurs.Single(d => d.EntityId == "binary_sensor.baie");
        Assert.Equal(("Séjour", false, false, 64.0, true, "Fermé"), (baie.Piece, baie.Mouvement, baie.Actif, baie.Batterie, baie.Surveille, baie.Libelle));
        Assert.Equal(Maintenant.AddMinutes(-3), baie.Depuis);
        Assert.Equal("Mouvement", vue.Detecteurs.Single(d => d.EntityId == "binary_sensor.radar").Libelle);
        Assert.Equal("Indisponible", vue.Detecteurs.Single(d => d.EntityId == "binary_sensor.porte").Libelle);
        Assert.Equal("Jamais reçu", vue.Detecteurs.Single(d => d.EntityId == "binary_sensor.velux").Libelle);
        Assert.Equal((0, 1), (vue.Ouverts, vue.EnMouvement));
    }
}
