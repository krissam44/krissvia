using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Krissvia.Plugin.OpenHasp.Tests;

/// <summary>La vraie connexion MQTTnet face à un broker absent : le plugin doit apprendre pourquoi, pas seulement « coupé ».</summary>
public class LienMqttTests
{
    private sealed class Temoin : IMqttHandler
    {
        public ConcurrentQueue<string> Evenements { get; } = new();
        public Task ConnecteAsync() => Ajouter("connecté");
        public Task DeconnecteAsync() => Ajouter("déconnecté");
        public Task MessageAsync(string topic, string charge) => Task.CompletedTask;
        public Task ConnexionImpossibleAsync(string motif) => Ajouter(motif);

        private Task Ajouter(string e)
        {
            Evenements.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    private static int PortLibre()
    {
        var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        var port = ((IPEndPoint)ecoute.LocalEndpoint).Port;
        ecoute.Stop();
        return port;
    }

    private static async Task<string[]> DeuxPremiersEvenementsAsync(MqttOptions options)
    {
        var temoin = new Temoin();
        using var arret = new CancellationTokenSource();
        var boucle = new MqttNetLink(options, TimeProvider.System, NullLogger<MqttNetLink>.Instance).RunAsync("hasp/#", temoin, arret.Token);

        var limite = DateTime.UtcNow.AddSeconds(10);
        while (temoin.Evenements.Count < 2 && DateTime.UtcNow < limite) await Task.Delay(20);
        await arret.CancelAsync();
        await boucle.WaitAsync(TimeSpan.FromSeconds(5));
        return [.. temoin.Evenements.Take(2)];
    }

    [Fact]
    public async Task Un_broker_injoignable_est_signale_avec_son_motif_avant_la_deconnexion()
    {
        // Un port qu'on vient de libérer : rien n'y écoute.
        var evenements = await DeuxPremiersEvenementsAsync(new MqttOptions { Host = "127.0.0.1", Port = PortLibre() });

        Assert.Equal(["broker injoignable (adresse ou port)", "déconnecté"], evenements);
    }

    [Fact]
    public async Task Sans_broker_regle_le_plugin_le_dit_au_coeur()
    {
        var evenements = await DeuxPremiersEvenementsAsync(new MqttOptions { Host = " " });

        Assert.Equal(["aucun broker réglé (pastille MQTT de la barre du haut)", "déconnecté"], evenements);
    }

    [Fact]
    public async Task Un_mot_de_passe_refuse_est_signale_comme_tel()
    {
        // MQTTnet 5 ne lève rien sur un refus : sans lecture du code, on n'aurait que « client non connecté » à afficher.
        var port = PortLibre();
        using var serveur = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
        serveur.ValidatingConnectionAsync += e =>
        {
            if (e.Password != "bon") e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        await serveur.StartAsync();

        var evenements = await DeuxPremiersEvenementsAsync(new MqttOptions { Host = "127.0.0.1", Port = port, Username = "essai", Password = "faux" });

        Assert.Equal(["identifiant ou mot de passe refusé par le broker", "déconnecté"], evenements);
        await serveur.StopAsync();
    }
}
