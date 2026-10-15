using System.Threading.Channels;
using Krissvia.Contracts;

namespace Krissvia.PluginHost.Tests;

/// <summary>
/// Paire de canaux en mémoire : l'un côté cœur, l'autre côté plugin. Fermer l'un ferme les deux sens, comme une
/// connexion WebSocket.
/// </summary>
internal sealed class CanalMemoire : IMessageChannel
{
    private readonly Channel<string> _entree;
    private readonly Channel<string> _sortie;

    private CanalMemoire(Channel<string> entree, Channel<string> sortie)
    {
        _entree = entree;
        _sortie = sortie;
    }

    public static (CanalMemoire Coeur, CanalMemoire Plugin) Paire()
    {
        var versCoeur = Channel.CreateUnbounded<string>();
        var versPlugin = Channel.CreateUnbounded<string>();
        return (new CanalMemoire(versCoeur, versPlugin), new CanalMemoire(versPlugin, versCoeur));
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _entree.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask SendAsync(string frame, CancellationToken ct) =>
        _sortie.Writer.TryWrite(frame) ? ValueTask.CompletedTask : throw new InvalidOperationException("canal fermé");

    public ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        _entree.Writer.TryComplete();
        _sortie.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ── Côté plugin, pour les tests ──────────────────────────────────────────

    public ValueTask EnvoyerAsync(Message message) => SendAsync(ContractJson.Encode(message), CancellationToken.None);

    /// <summary>Prochain message autre qu'un ping, ou null si le canal est fermé. Échoue au bout de 5 s.</summary>
    public async Task<Message?> RecevoirAsync()
    {
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await ReceiveAsync(delai.Token) is { } trame)
        {
            var message = ContractJson.Decode(trame).Message ?? throw new InvalidOperationException($"trame illisible : {trame}");
            if (message is not Ping) return message;
        }

        return null;
    }

    public async Task<T> RecevoirAsync<T>() where T : Message =>
        Assert.IsType<T>(await RecevoirAsync());

    /// <summary>Vrai si une trame (ping compris) attend déjà d'être lue.</summary>
    public bool TryLire(out string? trame) => _entree.Reader.TryRead(out trame);
}
