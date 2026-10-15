using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.Teleinfo.Tests;

/// <summary>La trame Linky (format JSON de DomoMud, <c>LinkyData</c>) traduite en entités du contrat.</summary>
public class TeleinfoTranslatorTests
{
    private const string Topic = "energy/linky";
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);

    private readonly TeleinfoTranslator _t = new(Topic, "linky", "Linky", TimeSpan.FromSeconds(60));

    private const string Trame = """
        {"HEURE":"18:00:02","DATE":"15/10/2026","EAST":"012345678","EASF01":"008000123","EASF02":"004345555",
         "SINSTS":"03120","IRMS1":"014","URMS1":"231","LTARF":"   HEURE  PLEINE  ","NTARF":"01"}
        """;

    private Dictionary<string, JsonElement> Etats(string charge, DateTimeOffset? a = null) =>
        _t.TraiterMessage(Topic, charge, a ?? T0).OfType<StateBatch>().SelectMany(b => b.States)
            .ToDictionary(s => s.EntityUniqueId, s => s.Value);

    [Fact]
    public void La_declaration_porte_la_puissance_apparente_en_VA_et_les_index_en_kWh()
    {
        var entites = Assert.Single(_t.Declaration().Devices).Entities.ToDictionary(e => e.UniqueId);

        Assert.Equal((DeviceClasses.ApparentPower, Units.VoltAmpere), (entites["linky:sinsts"].DeviceClass, entites["linky:sinsts"].Unit));
        Assert.Equal(StateClass.TotalIncreasing, entites["linky:east"].StateClass);
        Assert.Equal(Units.KilowattHour, entites["linky:easf02"].Unit);
        Assert.Equal(DeviceClasses.Enum, entites["linky:ltarf"].DeviceClass);
        Assert.All(entites.Values, e => Assert.Equal(EntityKind.Sensor, e.Kind));
    }

    [Fact]
    public void Les_valeurs_de_la_trame_sont_converties()
    {
        var e = Etats(Trame);

        Assert.Equal(3120, e["linky:sinsts"].GetInt64());
        Assert.Equal(12345.678, e["linky:east"].GetDouble());
        Assert.Equal(4345.555, e["linky:easf02"].GetDouble());
        Assert.Equal(14, e["linky:irms1"].GetInt64());
        Assert.Equal(231, e["linky:urms1"].GetInt64());
        Assert.Equal("HEURE  PLEINE", e["linky:ltarf"].GetString());
        Assert.Equal(1, e["linky:ntarf"].GetInt64());
    }

    [Fact]
    public void Une_valeur_illisible_est_ignoree_jamais_lue_comme_zero()
    {
        // Un SINSTS à 0 ferait tout rétablir au délesteur.
        var e = Etats("""{"SINSTS":"","EAST":"abc","URMS1":"230"}""");

        Assert.False(e.ContainsKey("linky:sinsts"));
        Assert.False(e.ContainsKey("linky:east"));
        Assert.Equal(230, e["linky:urms1"].GetInt64());
    }

    [Fact]
    public void Les_nombres_et_les_etiquettes_en_minuscules_sont_acceptes()
    {
        var e = Etats("""{"sinsts":2500,"East":12000}""");

        Assert.Equal(2500, e["linky:sinsts"].GetInt64());
        Assert.Equal(12, e["linky:east"].GetDouble());
    }

    [Fact]
    public void Un_autre_topic_ou_une_charge_illisible_ne_produisent_rien()
    {
        Assert.Empty(_t.TraiterMessage("energy/autre", Trame, T0));
        Assert.Empty(_t.TraiterMessage(Topic, "{pas du json", T0));
        Assert.Empty(_t.TraiterMessage(Topic, "[1,2]", T0));
        Assert.Empty(_t.TraiterMessage(Topic, """{"HEURE":"18:00:00"}""", T0));
    }

    [Fact]
    public void Un_compteur_muet_rend_le_plugin_hors_ligne_une_seule_fois_puis_en_ligne_au_retour()
    {
        Etats(Trame);

        Assert.Empty(_t.Surveiller(T0.AddSeconds(60)));
        var muet = Assert.IsType<Availability>(Assert.Single(_t.Surveiller(T0.AddSeconds(61))));
        Assert.False(muet.Online);
        Assert.Empty(_t.Surveiller(T0.AddSeconds(120)));

        var retour = _t.TraiterMessage(Topic, Trame, T0.AddSeconds(130));
        Assert.True(Assert.IsType<Availability>(retour[0]).Online);
        Assert.IsType<StateBatch>(retour[1]);
    }

    [Fact]
    public void Avant_toute_trame_rien_n_est_signale_comme_muet()
    {
        Assert.Empty(_t.Surveiller(T0.AddHours(1)));
    }

    [Fact]
    public void L_instantane_redonne_la_declaration_les_dernieres_valeurs_et_la_joignabilite()
    {
        _t.MqttConnecte();
        Etats(Trame);

        var instantane = _t.Instantane();

        Assert.IsType<Declare>(instantane[0]);
        Assert.Equal(8, Assert.IsType<StateBatch>(instantane[1]).States.Count);
        Assert.True(Assert.IsType<Availability>(instantane[2]).Online);
    }

    [Fact]
    public void Broker_perdu_le_plugin_est_hors_ligne()
    {
        _t.MqttConnecte();
        Etats(Trame);

        Assert.False(Assert.IsType<Availability>(Assert.Single(_t.MqttDeconnecte())).Online);
        Assert.False(_t.Instantane().OfType<Availability>().Single().Online);
    }
}
