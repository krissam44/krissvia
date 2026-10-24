using Krissvia.PluginHost.Services;
using Microsoft.Extensions.Logging;

namespace Krissvia.PluginHost.Tests;

/// <summary>La sortie d'un plugin relue pour journalctl : un message par entrée, au niveau qu'il annonce.</summary>
public class JournalPluginTests
{
    private static List<(LogLevel, string)> Lire(params string[] lignes)
    {
        var journal = new PluginJournal();
        return [.. lignes.Select(journal.Read).OfType<(LogLevel, string)>()];
    }

    [Fact]
    public void L_en_tete_de_net_donne_son_niveau_au_texte_qui_suit_sans_etre_repete()
    {
        var entrees = Lire(
            "warn: Krissvia.Sdk.Mqtt.MqttNetLink[711124951]",
            "      Aucun broker MQTT réglé : à saisir dans la pastille MQTT de la barre du haut.",
            "info: Krissvia.Sdk.CoreClient[1898781499]",
            "      Connecté au cœur Krissvia (contrat 0.3).");

        Assert.Equal(
        [
            (LogLevel.Warning, "Aucun broker MQTT réglé : à saisir dans la pastille MQTT de la barre du haut."),
            (LogLevel.Information, "Connecté au cœur Krissvia (contrat 0.3)."),
        ], entrees);
    }

    [Fact]
    public void Une_erreur_reste_une_erreur_et_une_ligne_libre_passe_en_information()
    {
        var entrees = Lire("fail: Plugin[1]", "      Message illisible.", "Traceback (most recent call last):", "", "   suite libre");

        Assert.Equal([(LogLevel.Error, "Message illisible."), (LogLevel.Information, "Traceback (most recent call last):"),
            (LogLevel.Information, "suite libre")], entrees);
    }
}
