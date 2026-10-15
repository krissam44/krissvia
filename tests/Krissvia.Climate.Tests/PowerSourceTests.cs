using Krissvia.Contracts;
using Krissvia.Core.Registry;
using System.Text.Json;
using Krissvia.Climate;
using Krissvia.Climate.LoadShedding;
using Krissvia.Core.States;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Lecteur d'états de test : un dictionnaire d'états que le test remplit à la main.</summary>
internal sealed class FakeStateReader : IEntityStateReader
{
    private readonly Dictionary<string, EntityStateSnapshot> _etats = new(StringComparer.Ordinal);

    public void Set(string entityId, double valeur, DateTimeOffset a, bool disponible = true) =>
        _etats[entityId] = new EntityStateSnapshot(JsonSerializer.SerializeToElement(valeur), a, disponible);

    public void Set(string entityId, bool valeur, DateTimeOffset a, bool disponible = true) =>
        _etats[entityId] = new EntityStateSnapshot(JsonSerializer.SerializeToElement(valeur), a, disponible);

    public void Set(string entityId, string valeur, DateTimeOffset a) =>
        _etats[entityId] = new EntityStateSnapshot(JsonSerializer.SerializeToElement(valeur), a);

    public EntityStateSnapshot? Get(string entityId) => _etats.GetValueOrDefault(entityId);
}

public class PowerSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider Horloge = new(T0);

    // ── Source branchée sur une entité ───────────────────────────────────────

    [Fact]
    public void Une_entite_de_puissance_donne_sa_valeur_son_instant_et_sa_qualite()
    {
        var etats = new FakeStateReader();
        etats.Set("sensor.pince_power", 4500.0, T0);

        var echantillon = new EntityPowerSource(etats, "sensor.pince_power", PowerQuality.Active).Read();

        Assert.NotNull(echantillon);
        var e = echantillon.GetValueOrDefault();
        Assert.Equal(4500, e.Value);
        Assert.Equal(T0, e.MeasuredAt);
        Assert.Equal(PowerQuality.Active, e.Quality);
    }

    [Fact]
    public void Une_entite_inconnue_indisponible_ou_non_numerique_est_une_source_indisponible()
    {
        var etats = new FakeStateReader();
        etats.Set("sensor.hors_ligne", 4500.0, T0, disponible: false);
        etats.Set("sensor.texte", "Heures creuses", T0);

        Assert.Null(new EntityPowerSource(etats, "sensor.jamais_vue", PowerQuality.Apparent).Read());
        Assert.Null(new EntityPowerSource(etats, "sensor.hors_ligne", PowerQuality.Apparent).Read());
        Assert.Null(new EntityPowerSource(etats, "sensor.texte", PowerQuality.Apparent).Read());
    }

    // ── D12 : somme des équipements ──────────────────────────────────────────

    [Fact]
    public void D12_la_somme_prend_la_mesure_reelle_sinon_la_puissance_nominale_d_un_equipement_allume()
    {
        var etats = new FakeStateReader();
        etats.Set("switch.mesure", true, T0 - TimeSpan.FromSeconds(1));
        etats.Set("sensor.mesure_power", 800.0, T0 - TimeSpan.FromSeconds(1));
        etats.Set("switch.fil_pilote", true, T0 - TimeSpan.FromSeconds(5));   // ne mesure rien
        etats.Set("switch.eteint", false, T0);
        etats.Set("switch.mesure_nulle", true, T0);                           // allumé mais 0 W mesuré : nominal
        etats.Set("sensor.nulle_power", 0.0, T0);
        // switch.inconnu : aucun état, il est ignoré

        var source = new EquipmentSumPowerSource(etats, () =>
        [
            new MeteredEquipment("switch.mesure", "sensor.mesure_power", 1000),
            new MeteredEquipment("switch.fil_pilote", null, 1500),
            new MeteredEquipment("switch.eteint", null, 2000),
            new MeteredEquipment("switch.mesure_nulle", "sensor.nulle_power", 700),
            new MeteredEquipment("switch.inconnu", null, 3000),
        ], Horloge);

        var echantillon = source.Read();

        Assert.NotNull(echantillon);
        var e = echantillon.GetValueOrDefault();
        Assert.Equal(800 + 1500 + 700, e.Value);
        Assert.Equal(PowerQuality.Estimated, e.Quality);
        Assert.Equal(T0, e.MeasuredAt);   // datée de la lecture : les états ne vieillissent pas tant qu'ils ne changent pas
    }

    [Fact]
    public void D12_une_somme_entierement_mesuree_est_de_qualite_active()
    {
        var etats = new FakeStateReader();
        etats.Set("switch.a", true, T0);
        etats.Set("sensor.a_power", 800.0, T0);

        var echantillon = new EquipmentSumPowerSource(etats, () =>
            [new MeteredEquipment("switch.a", "sensor.a_power", 1000)], Horloge).Read();

        Assert.NotNull(echantillon);
        Assert.Equal(PowerQuality.Active, echantillon.GetValueOrDefault().Quality);
    }

    [Fact]
    public void D12_sans_aucun_etat_connu_la_source_est_indisponible_et_non_nulle()
    {
        var etats = new FakeStateReader();

        var source = new EquipmentSumPowerSource(etats, () => [new MeteredEquipment("switch.a", null, 1000)], Horloge);

        Assert.Null(source.Read());
    }

    [Fact]
    public void D12_tout_eteint_est_une_vraie_mesure_de_zero()
    {
        var etats = new FakeStateReader();
        etats.Set("switch.a", false, T0);

        var echantillon = new EquipmentSumPowerSource(etats, () => [new MeteredEquipment("switch.a", null, 1000)], Horloge).Read();

        Assert.NotNull(echantillon);
        Assert.Equal(0, echantillon.GetValueOrDefault().Value);
    }

    // ── Conversion en entrée du délesteur ────────────────────────────────────

    [Fact]
    public void Une_mesure_active_est_majoree_une_mesure_apparente_ou_estimee_ne_l_est_pas()
    {
        var active = new PowerSample(1000, T0, PowerQuality.Active);
        var apparente = new PowerSample(1000, T0, PowerQuality.Apparent);
        var estimee = new PowerSample(1000, T0, PowerQuality.Estimated);

        Assert.Equal(1100, PowerReadings.ToReading(active, 1.10).GetValueOrDefault().Value, 6);
        Assert.Equal(1000, PowerReadings.ToReading(apparente, 1.10).GetValueOrDefault().Value);
        Assert.Equal(1000, PowerReadings.ToReading(estimee, 1.10).GetValueOrDefault().Value);
        Assert.Equal(T0, PowerReadings.ToReading(active, 1.10).GetValueOrDefault().MeasuredAt);
    }

    [Fact]
    public void Une_source_indisponible_donne_une_absence_de_mesure_et_un_facteur_inferieur_a_1_est_refuse()
    {
        Assert.Null(PowerReadings.ToReading(null, 1.10));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = PowerReadings.ToReading(null, 0.9); });
    }

    // ── Chaîne complète : pince en W → seuils en VA ──────────────────────────

    [Fact]
    public async Task Une_pince_en_watts_est_majoree_avant_d_etre_comparee_aux_seuils_en_VA()
    {
        var etats = new FakeStateReader();
        etats.Set("sensor.pince_power", 4500.0, T0);
        var horloge = new FakeTimeProvider(T0);
        var commander = new ScriptedCommander();
        var delesteur = new LoadShedder(commander, new ThermostatInhibitor(commander), horloge);
        await delesteur.ConfigureAsync(
            LoadSheddingSettings.FromSubscription(6, 80, 70),
            [new SheddableDevice("cumulus", "Chauffe-eau", 1, 2000, TimeSpan.FromSeconds(120), ["switch.cumulus"])]);
        var source = new EntityPowerSource(etats, "sensor.pince_power", PowerQuality.Active);

        var sansMajoration = await delesteur.TickAsync(PowerReadings.ToReading(source.Read(), 1.0));   // 4 500 < 4 800
        Assert.Empty(sansMajoration.Shed);

        horloge.Advance(TimeSpan.FromSeconds(1));
        var avecMajoration = await delesteur.TickAsync(PowerReadings.ToReading(source.Read(), 1.10));  // 4 950 > 4 800
        Assert.Equal(new[] { "cumulus" }, avecMajoration.Shed.ToArray());
    }

    private static RegisteredEntity Entite(string id, string appareil, EntityKind famille, string? classe = null, double? puissance = null,
        bool actif = true, bool disparue = false) => new()
    {
        EntityId = id, DeviceId = appareil, IntegrationId = "test", UniqueId = id, Kind = famille, DeviceClass = classe,
        DisplayName = id, NominalPower = puissance, Enabled = actif, Missing = disparue,
    };

    [Fact]
    public void La_somme_prend_chaque_switch_declare_et_la_mesure_de_son_appareil()
    {
        var equipements = DeclaredEquipment.FromRegistry(
        [
            Entite("switch.prise", "prise", EntityKind.Switch, puissance: 1000),
            Entite("sensor.prise_power", "prise", EntityKind.Sensor, DeviceClasses.Power),
            Entite("sensor.prise_energy", "prise", EntityKind.Sensor, DeviceClasses.Energy),
            Entite("switch.radiateur", "rad", EntityKind.Switch, puissance: 1500),
            Entite("switch.non_declare", "x", EntityKind.Switch),
            Entite("switch.desactive", "y", EntityKind.Switch, puissance: 500, actif: false),
            Entite("switch.disparu", "z", EntityKind.Switch, puissance: 500, disparue: true),
            Entite("sensor.compteur_power", "compteur", EntityKind.Sensor, DeviceClasses.Power, puissance: 9000),
        ]);

        Assert.Equal(
            [new MeteredEquipment("switch.prise", "sensor.prise_power", 1000), new MeteredEquipment("switch.radiateur", null, 1500)],
            equipements.OrderBy(e => e.SwitchEntityId));
    }

    [Fact]
    public void Une_puissance_declaree_apres_coup_compte_a_la_lecture_suivante()
    {
        var etats = new FakeStateReader();
        etats.Set("switch.a", true, T0);
        var entites = new List<RegisteredEntity> { Entite("switch.a", "a", EntityKind.Switch) };
        var source = new EquipmentSumPowerSource(etats, () => DeclaredEquipment.FromRegistry(entites), Horloge);

        Assert.Null(source.Read());   // rien de déclaré : rien à additionner

        entites[0] = entites[0] with { NominalPower = 1200 };
        Assert.Equal(1200, source.Read().GetValueOrDefault().Value);
    }

    [Fact]
    public void Un_radiateur_allume_depuis_des_heures_compte_toujours_un_hors_ligne_non()
    {
        var etats = new FakeStateReader();
        etats.Set("switch.radiateur", true, T0 - TimeSpan.FromHours(3));
        etats.Set("switch.hors_ligne", true, T0, disponible: false);
        var source = new EquipmentSumPowerSource(etats, () =>
        [
            new MeteredEquipment("switch.radiateur", null, 1500),
            new MeteredEquipment("switch.hors_ligne", null, 2000),
        ], Horloge);

        var e = source.Read().GetValueOrDefault();

        Assert.Equal(1500, e.Value);
        Assert.Equal(T0, e.MeasuredAt);
    }
}
