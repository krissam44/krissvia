namespace Krissvia.Sdk.Mqtt;

/// <summary>Ce que le plugin reçoit du broker. Appelé dans l'ordre des messages, jamais en parallèle.</summary>
public interface IMqttHandler
{
    Task ConnecteAsync();
    Task DeconnecteAsync();
    Task MessageAsync(string topic, string charge);

    /// <summary>La connexion au broker a échoué, et pourquoi (refusée, injoignable…). Suivie de <see cref="DeconnecteAsync"/>.</summary>
    Task ConnexionImpossibleAsync(string motif) => Task.CompletedTask;
}

/// <summary>Connexion au broker. MQTTnet en production, un broker en mémoire dans les tests.</summary>
public interface IMqttLink
{
    /// <summary>Se connecte, s'abonne, se reconnecte, jusqu'à l'annulation.</summary>
    Task RunAsync(string abonnement, IMqttHandler handler, CancellationToken ct);

    /// <summary>
    /// Ajoute un abonnement à la connexion EN COURS (topics connus seulement à l'exécution : ceux qu'annonce une
    /// configuration MQTT Discovery). Faux si le broker n'est pas joignable. Une reconnexion le perd : le plugin le refait
    /// depuis <see cref="IMqttHandler.ConnecteAsync"/>. À appeler hors de <see cref="IMqttHandler.MessageAsync"/>, qui
    /// bloque la réception des accusés.
    /// </summary>
    Task<bool> SubscribeAsync(string filtre, CancellationToken ct);

    /// <summary>Faux si le broker n'est pas joignable : l'ordre n'est pas parti.</summary>
    Task<bool> PublishAsync(string topic, string charge, CancellationToken ct);
}
