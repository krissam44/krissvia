using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Plugin.Ipp.Decouverte;
using Krissvia.Plugin.Ipp.Protocole;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Plugin.Ipp.Tests;

/// <summary>Le plugin contre de fausses imprimantes : entités, problème, imprimante éteinte, mémoire, découverte, « Vérifier ».</summary>
public class PluginTests
{
    private const string Samsung = "16a65700-007c-1000-bb49-30cda71ce2f2";
    private static readonly Uri UrlSamsung = new("ipp://192.168.1.63:631/ipp/print");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 18, 38, 0, TimeSpan.Zero);

    private sealed class FausseImprimantes : IIppClient
    {
        public Dictionary<string, Func<IppResponse>> Reponses { get; } = new(StringComparer.Ordinal);
        public List<string> Appels { get; } = [];

        public Task<IppResponse> GetPrinterAttributesAsync(Uri imprimante, CancellationToken ct)
        {
            Appels.Add(imprimante.ToString());
            return Reponses.TryGetValue(imprimante.ToString(), out var r)
                ? Task.FromResult(r())
                : Task.FromException<IppResponse>(new HttpRequestException("connexion refusée"));
        }
    }

    private sealed class FausseDecouverte : IDecouverteIpp
    {
        public List<ServiceIpp> Annonces { get; } = [];
        public int Recherches { get; private set; }

        public Task<IReadOnlyList<ServiceIpp>> ChercherAsync(TimeSpan duree, CancellationToken ct)
        {
            Recherches++;
            return Task.FromResult<IReadOnlyList<ServiceIpp>>([.. Annonces]);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FausseImprimantes Ipp { get; } = new();
        public FausseDecouverte Decouverte { get; } = new();
        public string Fichier { get; } = Path.Combine(Path.GetTempPath(), $"krissvia-ipp-{Guid.NewGuid():N}.json");
        public IppPlugin Plugin { get; private set; } = null!;

        public Banc(bool decouverte = false, params Uri[] manuelles) => Nouveau(decouverte, manuelles);

        public IppPlugin Nouveau(bool decouverte, params Uri[] manuelles) =>
            Plugin = new IppPlugin(Ipp, Decouverte, Horloge, NullLogger<IppPlugin>.Instance,
                new OptionsIpp(manuelles, decouverte, TimeSpan.FromMinutes(5)), new MemoireImprimantes(Fichier));

        public void Repond(Uri url, string trame) => Ipp.Reponses[url.ToString()] = () => IppCodec.Decode(Trames.Lire(trame));

        public void Eteint(Uri url) => Ipp.Reponses.Remove(url.ToString());

        public async Task ReleveAsync(double minutes = 5)
        {
            Horloge.Advance(TimeSpan.FromMinutes(minutes));
            await Plugin.CycleAsync(CancellationToken.None);
        }

        public IReadOnlyList<Message> Snapshot => Plugin.Snapshot();

        public DeviceDescriptor Appareil(string cle) => Snapshot.OfType<Declare>().Single().Devices.Single(d => d.UniqueId == cle);

        public JsonElement Valeur(string id) => Snapshot.OfType<StateBatch>().Single().States.Single(s => s.EntityUniqueId == id).Value;

        public bool Disponible(string cle) => Snapshot.OfType<Availability>().Single(a => a.DeviceUniqueId == cle).Online;
    }

    [Fact]
    public async Task Une_imprimante_devient_un_appareil_avec_ses_entites()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");

        await banc.ReleveAsync();

        var a = banc.Appareil(Samsung);
        Assert.Equal(("Samsung M2020 Series", "Samsung", "Samsung M2020 Series"), (a.Name, a.Manufacturer, a.Model));
        Assert.Equal(
            [
                (Samsung, EntityKind.Sensor, DeviceClasses.Printer, (string?)null),
                ($"{Samsung}:etat", EntityKind.Sensor, DeviceClasses.Enum, null),
                ($"{Samsung}:probleme", EntityKind.BinarySensor, DeviceClasses.Problem, null),
                ($"{Samsung}:conso:black_toner", EntityKind.Sensor, null, Units.Percent),
                ($"{Samsung}:conso:transfer_roller", EntityKind.Sensor, null, Units.Percent),
                ($"{Samsung}:conso:fuser_life", EntityKind.Sensor, null, Units.Percent),
                ($"{Samsung}:conso:pick_up_roller", EntityKind.Sensor, null, Units.Percent),
            ],
            a.Entities.Select(e => (e.UniqueId, e.Kind, e.DeviceClass, e.Unit)));
        Assert.Equal([IppPlugin.Refresh], a.Entities[0].Commands);

        Assert.Equal("Prête", banc.Valeur($"{Samsung}:etat").GetString());
        Assert.False(banc.Valeur($"{Samsung}:probleme").GetBoolean());
        Assert.Equal(96, banc.Valeur($"{Samsung}:conso:black_toner").GetInt32());
        Assert.True(banc.Disponible(Samsung));

        var objet = banc.Valeur(Samsung);
        Assert.Equal("idle", objet.GetProperty("state").GetString());
        Assert.Equal(4, objet.GetProperty("markers").GetArrayLength());
        Assert.Equal("Black Toner", objet.GetProperty("markers")[0].GetProperty("name").GetString());
        // Le plus bas : le premier des trois à 86 %, comme la carte de Gladys (« Transfer Roller · 86 % »).
        Assert.Equal(("Transfer Roller", 86), (objet.GetProperty("lowest").GetProperty("name").GetString(), objet.GetProperty("lowest").GetProperty("level").GetInt32()));
        Assert.Equal(T0.AddMinutes(5), objet.GetProperty("last_read").GetDateTimeOffset());
    }

    [Fact]
    public async Task Un_bourrage_leve_le_probleme_et_sa_fin_le_baisse()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-bourrage.ipp.txt");
        await banc.ReleveAsync();

        Assert.True(banc.Valeur($"{Samsung}:probleme").GetBoolean());
        Assert.Equal("Arrêtée", banc.Valeur($"{Samsung}:etat").GetString());
        Assert.Contains("Bourrage papier", banc.Valeur(Samsung).GetProperty("alerts").EnumerateArray().Select(a => a.GetString()));
        // -3 et -2 : pas de niveau, donc pas d'état pour ces deux capteurs plutôt qu'un faux 0 %.
        Assert.DoesNotContain(banc.Snapshot.OfType<StateBatch>().Single().States, s => s.EntityUniqueId.EndsWith("fuser_life") || s.EntityUniqueId.EndsWith("pick_up_roller"));

        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();
        Assert.False(banc.Valeur($"{Samsung}:probleme").GetBoolean());
    }

    [Fact]
    public async Task Une_imprimante_eteinte_devient_indisponible_sans_etre_un_probleme()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();

        banc.Eteint(UrlSamsung);
        await banc.ReleveAsync();
        Assert.True(banc.Disponible(Samsung));   // un relevé raté : peut-être un simple hoquet du Wi-Fi

        await banc.ReleveAsync();
        Assert.False(banc.Disponible(Samsung));
        Assert.False(banc.Valeur($"{Samsung}:probleme").GetBoolean());
        Assert.Equal(96, banc.Valeur($"{Samsung}:conso:black_toner").GetInt32());   // le dernier relevé reste

        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();
        Assert.True(banc.Disponible(Samsung));
    }

    [Fact]
    public async Task Eteinte_au_lancement_suivant_elle_est_quand_meme_declaree()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();

        banc.Eteint(UrlSamsung);
        banc.Nouveau(false, UrlSamsung);   // le plugin redémarre, l'imprimante est éteinte
        await banc.ReleveAsync();

        var a = banc.Appareil(Samsung);
        Assert.Equal(7, a.Entities.Count);   // ses consommables aussi : rien ne passe « disparu »
        Assert.Equal("Samsung M2020 Series", a.Name);
        Assert.False(banc.Disponible(Samsung));
    }

    [Fact]
    public async Task Une_adresse_retiree_des_reglages_n_est_plus_declaree()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();

        banc.Nouveau(false);
        await banc.ReleveAsync();

        Assert.Empty(banc.Snapshot.OfType<Declare>().Single().Devices);
        Assert.Single(banc.Ipp.Appels);   // et plus interrogée
    }

    [Fact]
    public async Task Les_deux_annonces_et_la_saisie_d_une_meme_imprimante_font_un_seul_appareil()
    {
        var ipps = new Uri("ipps://192.168.1.63:631/ipp/print");
        var banc = new Banc(true, UrlSamsung);
        banc.Decouverte.Annonces.AddRange([new ServiceIpp("Samsung", UrlSamsung, Samsung, null), new ServiceIpp("Samsung", ipps, Samsung, null)]);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        banc.Repond(ipps, "samsung-m2020-prete.ipp.txt");

        await banc.ReleveAsync();
        await banc.ReleveAsync();
        await banc.ReleveAsync();

        Assert.Single(banc.Snapshot.OfType<Declare>().Single().Devices);
        // L'adresse en IPPS, reconnue au premier relevé comme la même imprimante, n'est plus interrogée ensuite.
        Assert.Equal(1, banc.Ipp.Appels.Count(a => a == ipps.ToString()));
        Assert.Equal(3, banc.Ipp.Appels.Count(a => a == UrlSamsung.ToString()));
    }

    [Fact]
    public async Task La_decouverte_trouve_l_imprimante_mais_ne_cherche_pas_a_chaque_releve()
    {
        var banc = new Banc(true);
        banc.Decouverte.Annonces.Add(new ServiceIpp("Samsung", UrlSamsung, Samsung, null));
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");

        await banc.ReleveAsync();
        await banc.ReleveAsync();
        Assert.Equal(1, banc.Decouverte.Recherches);
        Assert.Equal("Samsung M2020 Series", banc.Appareil(Samsung).Name);

        await banc.ReleveAsync(15);
        Assert.Equal(2, banc.Decouverte.Recherches);
    }

    [Fact]
    public async Task Decouverte_coupee_on_ne_cherche_pas_et_les_imprimantes_decouvertes_s_en_vont()
    {
        var banc = new Banc(true);
        banc.Decouverte.Annonces.Add(new ServiceIpp("Samsung", UrlSamsung, Samsung, null));
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();

        banc.Nouveau(false);
        await banc.ReleveAsync();

        Assert.Equal(1, banc.Decouverte.Recherches);
        Assert.Empty(banc.Snapshot.OfType<Declare>().Single().Devices);
    }

    [Fact]
    public async Task Verifier_releve_tout_de_suite()
    {
        var banc = new Banc(false, UrlSamsung);
        banc.Repond(UrlSamsung, "samsung-m2020-prete.ipp.txt");
        await banc.ReleveAsync();
        banc.Repond(UrlSamsung, "samsung-m2020-bourrage.ipp.txt");

        var r = await banc.Plugin.ExecuteAsync(new CommandRequest { Id = "1", EntityUniqueId = Samsung, Command = IppPlugin.Refresh }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        Assert.Equal(2, banc.Ipp.Appels.Count);
        Assert.True(banc.Valeur($"{Samsung}:probleme").GetBoolean());

        banc.Eteint(UrlSamsung);
        r = await banc.Plugin.ExecuteAsync(new CommandRequest { Id = "2", EntityUniqueId = Samsung, Command = IppPlugin.Refresh }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Unreachable, r.Outcome);

        r = await banc.Plugin.ExecuteAsync(new CommandRequest { Id = "3", EntityUniqueId = "autre", Command = IppPlugin.Refresh }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, r.Outcome);
        r = await banc.Plugin.ExecuteAsync(new CommandRequest { Id = "4", EntityUniqueId = Samsung, Command = "print" }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task Sans_UUID_l_adresse_sert_de_cle()
    {
        var url = new Uri("ipp://10.0.0.7:631/ipp/print");
        var banc = new Banc(false, url);
        banc.Ipp.Reponses[url.ToString()] = () => ReleveTests.Reponse(("printer-state", [3]), ("printer-make-and-model", ["HP LaserJet 1020"]));

        await banc.ReleveAsync();

        Assert.Equal("10-0-0-7-631-ipp-print", Assert.Single(banc.Snapshot.OfType<Declare>().Single().Devices).UniqueId);
    }
}
