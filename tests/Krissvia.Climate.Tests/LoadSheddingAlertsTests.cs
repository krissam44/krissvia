using System.Net;
using System.Text.Json;
using Krissvia.Climate.LoadShedding;
using Krissvia.Core.Configuration;
using Krissvia.Core.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Climate.Tests;

/// <summary>Les alertes du délestage : ce qui part sur le téléphone, une fois, et ce qui ne doit pas partir.</summary>
public class LoadSheddingAlertsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 19, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<SheddableDeviceConfiguration> Equipements =
    [
        new() { Id = "chauffe-eau", Name = "Chauffe-eau" },
        new() { Id = "radiateur", Name = "Radiateur chambre" },
    ];

    private static LoadSheddingStatus Etat(double? puissance, IReadOnlyList<string>? delestes = null, IReadOnlyList<string>? nonConfirmes = null,
        bool sansMesure = false, bool actif = true) =>
        new(actif, puissance, puissance is null ? null : T0, "Linky", sansMesure, 4800, 4200, delestes ?? [], nonConfirmes ?? [], T0);

    private sealed class Magasin : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(
                Documents.Where(kv => kv.Key.Item1 == collection).Select(kv => new ConfigurationDocument(kv.Key.Item2, kv.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) => Task.FromResult(Documents.Remove((collection, id)));
    }

    private sealed class FauxNtfy : HttpMessageHandler
    {
        public List<(string Titre, string Message)> Recues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Recues.Add((json.GetProperty("title").GetString()!, json.GetProperty("message").GetString()!));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FauxNtfy Ntfy { get; } = new();
        public AlertService Service { get; }
        public LoadSheddingAlerts Alertes { get; }

        public Banc()
        {
            Horloge.SetLocalTimeZone(TimeZoneInfo.Utc);
            var magasin = new Magasin();
            var notifications = new NotificationService(magasin, new HttpClient(Ntfy));
            notifications.SaveAsync(new NotificationSettings { Enabled = true, BaseUrl = "http://ntfy.essai", Topic = "maison" }).GetAwaiter().GetResult();
            Service = new AlertService(notifications, magasin, Horloge);
            Alertes = new LoadSheddingAlerts(Service, Horloge);
        }

        public Task TickAsync(double minutes, LoadSheddingStatus etat)
        {
            Horloge.SetUtcNow(T0.AddMinutes(minutes));
            return Alertes.OnTickAsync(etat, Equipements);
        }

        public IEnumerable<string> Titres => Ntfy.Recues.Select(r => r.Titre);
    }

    [Fact]
    public async Task Un_episode_fait_une_alerte_avec_les_equipements_coupes_puis_sa_fin()
    {
        var b = new Banc();

        await b.TickAsync(0, Etat(5230, ["chauffe-eau"]));
        await b.TickAsync(0.1, Etat(5100, ["chauffe-eau", "radiateur"]));   // la vague suivante ne renvoie rien
        await b.TickAsync(3, Etat(3900));

        Assert.Equal(["Délestage en cours", "Délestage terminé"], b.Titres);
        Assert.Equal("Puissance 5 230 VA au-dessus du seuil (4 800 VA). Coupé : Chauffe-eau.", b.Ntfy.Recues[0].Message.Replace(' ', ' ').Replace(' ', ' '));
    }

    [Fact]
    public async Task Un_deuxieme_episode_dans_le_quart_d_heure_ne_renotifie_pas()
    {
        var b = new Banc();
        await b.TickAsync(0, Etat(5230, ["chauffe-eau"]));
        await b.TickAsync(2, Etat(3900));
        await b.TickAsync(10, Etat(5300, ["chauffe-eau"]));
        Assert.Equal(["Délestage en cours", "Délestage terminé"], b.Titres);

        await b.TickAsync(12, Etat(3900));
        await b.TickAsync(30, Etat(5300, ["chauffe-eau"]));
        Assert.Equal(["Délestage en cours", "Délestage terminé", "Délestage en cours"], b.Titres);
    }

    [Fact]
    public async Task Un_redemarrage_en_plein_delestage_signale_quand_meme_l_episode()
    {
        var b = new Banc();   // nouvelle instance : la bascule « début d'épisode » a eu lieu avant le redémarrage

        await b.TickAsync(0, Etat(5000, ["radiateur"]));

        Assert.Equal(["Délestage en cours"], b.Titres);
    }

    [Fact]
    public async Task Une_coupure_non_confirmee_alerte_puis_se_confirme()
    {
        var b = new Banc();
        await b.TickAsync(0, Etat(5230, ["chauffe-eau"], ["chauffe-eau"]));
        await b.TickAsync(0.1, Etat(5230, ["chauffe-eau"], ["chauffe-eau"]));
        await b.TickAsync(0.2, Etat(4300, ["chauffe-eau"]));

        Assert.Equal(["Délestage en cours", "Coupure non confirmée : Chauffe-eau", "Coupure confirmée : Chauffe-eau"], b.Titres);
    }

    [Fact]
    public async Task Sans_mesure_cinq_minutes_le_delestage_le_dit()
    {
        var b = new Banc();
        await b.TickAsync(0, Etat(null, sansMesure: true));
        await b.TickAsync(4, Etat(null, sansMesure: true));
        Assert.Empty(b.Ntfy.Recues);

        await b.TickAsync(5, Etat(null, sansMesure: true));
        await b.TickAsync(6, Etat(3000));

        Assert.Equal(["Délestage sans mesure", "Délestage : mesure retrouvée"], b.Titres);
        Assert.Contains("depuis 19:00", b.Ntfy.Recues[0].Message);
    }

    [Fact]
    public async Task Delestage_desactive_rien_ne_part_et_l_episode_en_cours_est_oublie()
    {
        var b = new Banc();
        await b.TickAsync(0, Etat(5230, ["chauffe-eau"]));

        await b.TickAsync(1, Etat(5230, ["chauffe-eau"], actif: false));
        await b.TickAsync(2, Etat(3000));

        Assert.False(b.Service.IsActive(LoadSheddingAlerts.EpisodeKey));
        Assert.Equal(["Délestage en cours"], b.Titres);   // pas de « terminé » pour un épisode qu'on a cessé de suivre
    }
}
