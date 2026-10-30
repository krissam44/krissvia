using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Krissvia.Mobile.Client.Tests;

/// <summary>Le flux contre un vrai serveur local qui parle comme Krissvia : liste, WebSocket, états, fermeture.</summary>
public sealed class FluxEnDirectTests : IAsyncLifetime
{
    private WebApplication _serveur = null!;
    private string _adresse = null!;

    /// <summary>Requête de chaque connexion au flux : clé reçue et filtre demandé.</summary>
    private readonly ConcurrentQueue<(string? Cle, string Filtre)> _connexions = new();

    /// <summary>Faux : le serveur ferme le flux juste après la photo, comme à son arrêt.</summary>
    private volatile bool _fermerApresPhoto;

    private readonly TaskCompletionSource _envoyerEtat = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _serveur = builder.Build();
        _serveur.UseWebSockets();

        _serveur.MapGet("/api/entities", () => Results.Text(
            Exemples.Liste(Exemples.Climat("climate.salon", "Salon"), Exemples.Interrupteur), "application/json"));

        _serveur.Map("/api/live", async (HttpContext contexte) =>
        {
            _connexions.Enqueue((contexte.Request.Headers[KrissviaApi.EnTeteCle].ToString(), contexte.Request.Query["entities"].ToString()));
            using var socket = await contexte.WebSockets.AcceptWebSocketAsync();
            await EnvoyerAsync(socket, Exemples.MessageEntites(Exemples.Climat("climate.salon", "Salon")));

            if (_fermerApresPhoto)
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "arrêt du serveur", CancellationToken.None);
                return;
            }

            await _envoyerEtat.Task.WaitAsync(contexte.RequestAborted);
            var etat = Exemples.EtatSalon.Replace("\"temperature\": 19.2", "\"temperature\": 21.3", StringComparison.Ordinal);
            await EnvoyerAsync(socket, $$"""{ "type": "states", "states": { "climate.salon": {{etat}} } }""");

            var tampon = new byte[64];
            try
            {
                while ((await socket.ReceiveAsync(tampon, contexte.RequestAborted)).MessageType != WebSocketMessageType.Close) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        });

        await _serveur.StartAsync();
        _adresse = _serveur.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync() => await _serveur.DisposeAsync();

    private static Task EnvoyerAsync(WebSocket socket, string message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private (FluxEnDirect Flux, ThermostatsEnDirect Thermostats) Creer()
    {
        var connexion = new KrissviaApiTests.Connexion(_adresse, "secret");
        var thermostats = new ThermostatsEnDirect();
        var flux = new FluxEnDirect(new KrissviaApi(new HttpClient(), connexion), connexion, thermostats)
        {
            Delai = _ => TimeSpan.FromMilliseconds(20),
        };
        return (flux, thermostats);
    }

    private static async Task AttendreAsync(Func<bool> condition)
    {
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, delai.Token);
    }

    [Fact]
    public async Task Le_flux_s_ouvre_avec_la_cle_sur_les_thermostats_seuls_et_suit_les_etats()
    {
        var (flux, thermostats) = Creer();
        await using var _ = flux;

        flux.Demarrer();
        await AttendreAsync(() => flux.Connecte);

        var (cle, filtre) = Assert.Single(_connexions);
        Assert.Equal("secret", cle);
        Assert.Equal("climate.salon", filtre);
        Assert.Equal(19.2, Assert.Single(thermostats.Thermostats).Temperature);

        _envoyerEtat.SetResult();
        await AttendreAsync(() => thermostats.Thermostats.Single().Temperature == 21.3);
    }

    [Fact]
    public async Task Une_fermeture_par_le_serveur_est_suivie_d_une_reconnexion()
    {
        _fermerApresPhoto = true;
        var (flux, _) = Creer();
        await using var __ = flux;

        flux.Demarrer();

        await AttendreAsync(() => _connexions.Count >= 3);
    }

    [Fact]
    public async Task Reveiller_rouvre_le_flux_tout_de_suite()
    {
        var (flux, _) = Creer();
        await using var __ = flux;
        flux.Demarrer();
        await AttendreAsync(() => flux.Connecte);

        flux.Reveiller();

        await AttendreAsync(() => _connexions.Count == 2);
    }

    [Fact]
    public async Task Un_serveur_injoignable_se_dit_et_le_flux_reste_ferme()
    {
        var connexion = new KrissviaApiTests.Connexion("http://127.0.0.1:1", "secret");
        var flux = new FluxEnDirect(new KrissviaApi(new HttpClient(), connexion), connexion, new ThermostatsEnDirect())
        {
            Delai = _ => TimeSpan.FromMilliseconds(20),
        };
        await using var _ = flux;

        flux.Demarrer();

        await AttendreAsync(() => flux.Erreur is not null);
        Assert.False(flux.Connecte);
        Assert.StartsWith("Serveur injoignable", flux.Erreur, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arreter_ferme_le_flux()
    {
        var (flux, _) = Creer();
        flux.Demarrer();
        await AttendreAsync(() => flux.Connecte);

        await flux.ArreterAsync();

        Assert.False(flux.Connecte);
    }
}
