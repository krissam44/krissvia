using System.Net;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Events;
using Krissvia.Core.Notifications;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Core.Supervision;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

/// <summary>Changement d'IP publique : un message par changement, la nouvelle et l'ancienne adresse, et un envoi qui insiste.</summary>
public class PublicIpWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

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
        public List<string> Messages { get; } = [];
        public bool Injoignable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Injoignable) throw new HttpRequestException("Connexion refusée");
            Messages.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("message").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Banc
    {
        public FakeTimeProvider Horloge { get; } = new(T0);
        public FauxNtfy Ntfy { get; } = new();
        public Magasin Magasin { get; } = new();
        public AlertService Alertes { get; }
        public EntityRegistry Registre { get; } = new(new InMemoryEntityRepository());
        public PublicIpWatch Veille { get; }
        public string Ip { get; }
        public string Lien { get; }

        public Banc()
        {
            var notifications = new NotificationService(Magasin, new HttpClient(Ntfy));
            notifications.SaveAsync(new NotificationSettings { Enabled = true, BaseUrl = "https://ntfy.exemple.fr/", Topic = "maison-a7x" }).GetAwaiter().GetResult();
            Alertes = new AlertService(notifications, Magasin, Horloge);
            Veille = new PublicIpWatch(Alertes, notifications, Registre, Horloge);
            Registre.RegisterAsync("livebox",
            [
                new DeviceDescriptor
                {
                    UniqueId = "box", Name = "Livebox",
                    Entities =
                    [
                        new EntityDescriptor { UniqueId = "ip", Kind = EntityKind.Sensor, Name = "IP publique", DeviceClass = DeviceClasses.PublicIp },
                        new EntityDescriptor { UniqueId = "lien", Kind = EntityKind.Sensor, Name = "Type de lien" },
                    ],
                },
            ]).GetAwaiter().GetResult();
            Ip = Registre.Loaded.Single(e => e.UniqueId == "ip").EntityId;
            Lien = Registre.Loaded.Single(e => e.UniqueId == "lien").EntityId;
        }

        private EntityStateSnapshot? _precedent;

        /// <summary>La box publie une valeur (null : rien), comme le magasin d'états la transmet au bus.</summary>
        public Task<PublicIpChange?> PublierAsync(string? ip, string? entite = null, bool disponible = true)
        {
            var actuel = new EntityStateSnapshot(JsonSerializer.SerializeToElement(ip ?? ""), Horloge.GetUtcNow(), disponible);
            var e = new EntityStateChanged(entite ?? Ip, _precedent, actuel);
            _precedent = actuel;
            return Veille.OnStateChangedAsync(e);
        }

        /// <summary>L'état restauré au démarrage (même indisponible), avant le premier relevé de la box.</summary>
        public void Restaurer(string ip) => _precedent = new EntityStateSnapshot(JsonSerializer.SerializeToElement(ip), T0.AddHours(-3), Available: false);
    }

    [Fact]
    public async Task Un_changement_envoie_la_nouvelle_et_l_ancienne_adresse_une_seule_fois()
    {
        var b = new Banc();

        Assert.Null(await b.PublierAsync("90.1.2.3"));          // premier relevé : rien à comparer
        Assert.Null(await b.PublierAsync("90.1.2.3"));          // relevé identique
        var c = await b.PublierAsync("86.4.5.6");
        Assert.Null(await b.PublierAsync("86.4.5.6"));

        Assert.Equal(("90.1.2.3", "86.4.5.6"), (c!.Previous, c.Current));
        var message = Assert.Single(b.Ntfy.Messages);
        Assert.Contains("86.4.5.6", message);
        Assert.Contains("90.1.2.3", message);
    }

    [Fact]
    public async Task Une_IP_vide_pendant_la_reconnexion_ne_fait_pas_oublier_l_ancienne()
    {
        var b = new Banc();
        await b.PublierAsync("90.1.2.3");

        Assert.Null(await b.PublierAsync(null));
        Assert.Null(await b.PublierAsync("90.1.2.4", disponible: false));   // indisponible : pas une mesure
        var c = await b.PublierAsync("86.4.5.6");

        Assert.Equal("90.1.2.3", c!.Previous);
        Assert.Single(b.Ntfy.Messages);
    }

    [Fact]
    public async Task Une_IP_changee_pendant_que_le_serveur_etait_arrete_est_signalee()
    {
        var b = new Banc();
        b.Restaurer("90.1.2.3");

        var c = await b.PublierAsync("86.4.5.6");

        Assert.Equal("90.1.2.3", c!.Previous);
        Assert.Single(b.Ntfy.Messages);
    }

    [Fact]
    public async Task Seule_une_entite_public_ip_compte_et_le_reglage_peut_l_eteindre()
    {
        var b = new Banc();
        await b.PublierAsync("vdsl", b.Lien);
        Assert.Null(await b.PublierAsync("fibre", b.Lien));

        await b.Alertes.SaveSettingsAsync((await b.Alertes.GetSettingsAsync()) with { PublicIp = new PublicIpAlertSettings { Enabled = false } });
        await b.PublierAsync("90.1.2.3");
        Assert.Null(await b.PublierAsync("86.4.5.6"));

        Assert.Empty(b.Ntfy.Messages);
    }

    [Fact]
    public async Task Un_envoi_echoue_est_retente_et_un_nouveau_changement_garde_l_adresse_d_origine()
    {
        var b = new Banc();
        await b.PublierAsync("90.1.2.3");
        b.Ntfy.Injoignable = true;
        await b.PublierAsync("86.4.5.6");                       // ntfy injoignable : en attente

        b.Horloge.Advance(TimeSpan.FromSeconds(30));
        await b.Veille.RetryPendingAsync();                     // trop tôt pour le premier essai
        await b.PublierAsync("86.7.8.9");                       // nouveau changement avant l'envoi

        b.Ntfy.Injoignable = false;
        b.Horloge.Advance(TimeSpan.FromMinutes(2));
        await b.Veille.RetryPendingAsync();
        await b.Veille.RetryPendingAsync();                     // envoyé : rien à renvoyer

        var message = Assert.Single(b.Ntfy.Messages);
        Assert.Contains("nouvelle adresse 86.7.8.9 (avant : 90.1.2.3)", message);
    }

    [Fact]
    public async Task Les_essais_s_arretent_apres_le_dernier_delai()
    {
        var b = new Banc();
        await b.PublierAsync("90.1.2.3");
        b.Ntfy.Injoignable = true;
        await b.PublierAsync("86.4.5.6");

        foreach (var delai in PublicIpWatch.RetryDelays)
        {
            b.Horloge.Advance(delai);
            await b.Veille.RetryPendingAsync();
        }

        b.Ntfy.Injoignable = false;
        b.Horloge.Advance(TimeSpan.FromHours(2));
        await b.Veille.RetryPendingAsync();

        Assert.Empty(b.Ntfy.Messages);
    }
}
