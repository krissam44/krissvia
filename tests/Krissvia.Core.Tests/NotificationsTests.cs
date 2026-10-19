using System.Net;
using System.Text.Json;
using Krissvia.Core.Configuration;
using Krissvia.Core.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>Notifications ntfy et alertes : ce qui part sur le réseau, et ce qui ne doit pas repartir.</summary>
public class NotificationsTests
{
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

    /// <summary>Un faux serveur ntfy : note les requêtes, répond ce qu'on lui dit.</summary>
    private sealed class FauxNtfy : HttpMessageHandler
    {
        public List<(HttpRequestMessage Requete, string Corps)> Recues { get; } = [];
        public HttpStatusCode Reponse { get; set; } = HttpStatusCode.OK;
        public bool Injoignable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Injoignable) throw new HttpRequestException("Connexion refusée");
            Recues.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Reponse);
        }
    }

    private static readonly NotificationSettings Reglees = new() { Enabled = true, BaseUrl = "https://ntfy.exemple.fr/", Topic = "maison-a7x", Token = "jeton" };

    private static async Task<(NotificationService Service, FauxNtfy Serveur, Magasin Magasin)> PrepareAsync(NotificationSettings? r = null)
    {
        var serveur = new FauxNtfy();
        var magasin = new Magasin();
        var service = new NotificationService(magasin, new HttpClient(serveur));
        if (r is not null) await service.SaveAsync(r);
        return (service, serveur, magasin);
    }

    private static string Titre((HttpRequestMessage, string Corps) r) => JsonDocument.Parse(r.Corps).RootElement.GetProperty("title").GetString()!;

    [Fact]
    public async Task Un_message_part_en_json_avec_ses_accents_et_le_jeton()
    {
        var (service, serveur, _) = await PrepareAsync(Reglees);

        var r = await service.SendAsync(new Notification("Risque de gel à Vertou", "Minimum prévu −2 °C.", 4, ["snowflake"]));

        Assert.True(r.Sent);
        var (requete, corps) = Assert.Single(serveur.Recues);
        Assert.Equal("https://ntfy.exemple.fr/", requete.RequestUri!.ToString());
        Assert.Equal("Bearer jeton", requete.Headers.Authorization!.ToString());
        var json = JsonDocument.Parse(corps).RootElement;
        Assert.Equal("maison-a7x", json.GetProperty("topic").GetString());
        Assert.Equal("Risque de gel à Vertou", json.GetProperty("title").GetString());
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        Assert.Equal("snowflake", json.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Desactivees_ou_en_echec_l_envoi_le_dit_sans_exception()
    {
        var (service, serveur, _) = await PrepareAsync(Reglees with { Enabled = false });
        Assert.Equal(SendOutcome.Disabled, (await service.SendAsync(new Notification("t", "m"))).Outcome);
        Assert.Empty(serveur.Recues);

        // L'essai de la page passe outre la désactivation, avec les valeurs du formulaire.
        Assert.True((await service.SendAsync(new Notification("t", "m"), Reglees with { Enabled = false })).Sent);

        serveur.Reponse = HttpStatusCode.Forbidden;
        var refus = await service.SendAsync(new Notification("t", "m"), Reglees);
        Assert.Equal(SendOutcome.Failed, refus.Outcome);
        Assert.Contains("403", refus.Detail);

        serveur.Injoignable = true;
        Assert.Equal(SendOutcome.Failed, (await service.SendAsync(new Notification("t", "m"), Reglees)).Outcome);
    }

    [Fact]
    public async Task Les_reglages_sont_controles_nettoyes_et_relus()
    {
        var (service, _, magasin) = await PrepareAsync();
        await Assert.ThrowsAsync<NotificationValidationException>(() => service.SaveAsync(Reglees with { BaseUrl = "ftp://x" }));
        await Assert.ThrowsAsync<NotificationValidationException>(() => service.SaveAsync(Reglees with { Topic = "a b" }));
        await Assert.ThrowsAsync<NotificationValidationException>(() => service.SaveAsync(Reglees with { Topic = "" }));

        await service.SaveAsync(Reglees with { Topic = " maison-a7x ", Token = " " });

        var relus = await new NotificationService(magasin, new HttpClient(new FauxNtfy())).GetSettingsAsync();
        Assert.Equal(("https://ntfy.exemple.fr", "maison-a7x", (string?)null, true), (relus.BaseUrl, relus.Topic, relus.Token, relus.Enabled));
    }

    // ── Alertes ──────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);

    private static async Task<(AlertService Alertes, FauxNtfy Serveur, FakeTimeProvider Horloge)> AlertesAsync()
    {
        var (service, serveur, magasin) = await PrepareAsync(Reglees);
        var horloge = new FakeTimeProvider(T0);
        return (new AlertService(service, magasin, horloge), serveur, horloge);
    }

    [Fact]
    public async Task Un_incident_n_est_signale_qu_une_fois_et_sa_fin_seulement_s_il_etait_signale()
    {
        var (alertes, serveur, _) = await AlertesAsync();

        Assert.False(await alertes.ResolveAsync("gel:a", new Notification("fin", "m")));   // rien d'actif : silence
        Assert.Equal(RaiseOutcome.Raised, await alertes.RaiseAsync("gel:a", new Notification("gel", "m")));
        Assert.Equal(RaiseOutcome.AlreadyActive, await alertes.RaiseAsync("gel:a", new Notification("gel", "m")));
        Assert.True(alertes.IsActive("gel:a"));
        Assert.True(await alertes.ResolveAsync("gel:a", new Notification("fin", "m")));

        Assert.Equal(["gel", "fin"], serveur.Recues.Select(Titre));
        Assert.Empty(alertes.Active);
    }

    [Fact]
    public async Task Un_incident_retabli_reste_en_quarantaine()
    {
        var (alertes, _, horloge) = await AlertesAsync();
        await alertes.RaiseAsync("gel:a", new Notification("gel", "m"), TimeSpan.FromHours(24));
        await alertes.ResolveAsync("gel:a", new Notification("fin", "m"));

        horloge.Advance(TimeSpan.FromHours(23));
        Assert.Equal(RaiseOutcome.Quarantined, await alertes.RaiseAsync("gel:a", new Notification("gel", "m"), TimeSpan.FromHours(24)));

        horloge.Advance(TimeSpan.FromHours(2));
        Assert.Equal(RaiseOutcome.Raised, await alertes.RaiseAsync("gel:a", new Notification("gel", "m"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task Un_envoi_echoue_sera_retente_mais_des_notifications_desactivees_notent_l_incident()
    {
        var (alertes, serveur, _) = await AlertesAsync();
        serveur.Injoignable = true;

        Assert.Equal(RaiseOutcome.NotDelivered, await alertes.RaiseAsync("gel:a", new Notification("gel", "m")));
        Assert.False(alertes.IsActive("gel:a"));

        serveur.Injoignable = false;
        Assert.Equal(RaiseOutcome.Raised, await alertes.RaiseAsync("gel:a", new Notification("gel", "m")));
        Assert.Equal(SendOutcome.Sent, Assert.Single(alertes.Active).Delivery);
    }

    [Fact]
    public async Task Les_reglages_d_alerte_sont_bornes_et_relus()
    {
        var (alertes, _, _) = await AlertesAsync();
        await Assert.ThrowsAsync<NotificationValidationException>(() =>
            alertes.SaveSettingsAsync(new AlertSettings { Frost = new FrostAlertSettings { HorizonHours = 72 } }));

        await alertes.SaveSettingsAsync(new AlertSettings { Frost = new FrostAlertSettings { Enabled = false, ThresholdCelsius = -2, HorizonHours = 12 } });

        Assert.Equal(new FrostAlertSettings { Enabled = false, ThresholdCelsius = -2, HorizonHours = 12 }, (await alertes.GetSettingsAsync()).Frost);
    }
}
