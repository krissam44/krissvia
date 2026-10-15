namespace Krissvia.PluginHost;

/// <summary>
/// Canal d'une session plugin : des trames texte, une par message JSON du contrat. Le PluginHost ne connaît que
/// cette interface ; WebSocket en est l'implémentation de production, une paire en mémoire celle des tests.
/// </summary>
public interface IMessageChannel
{
    /// <summary>Trame suivante, ou <c>null</c> quand le canal est fermé (par l'autre côté ou par une coupure).</summary>
    ValueTask<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Envoie une trame. Sûr en appels concurrents. Lève une exception si le canal est fermé.</summary>
    ValueTask SendAsync(string frame, CancellationToken ct);

    /// <summary>Ferme le canal. Sans effet s'il l'est déjà ; ne lève jamais d'exception.</summary>
    ValueTask CloseAsync(string reason, CancellationToken ct);
}
