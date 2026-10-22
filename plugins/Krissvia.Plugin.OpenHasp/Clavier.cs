namespace Krissvia.Plugin.OpenHasp;

/// <summary>Ce que montre le digicode d'une platine : le code masqué et le dernier message.</summary>
public sealed record EtatClavier(string Masque, string? Message);

/// <summary>
/// Le code en cours de saisie sur UNE platine. Il ne vit qu'ici, en mémoire : jamais affiché (des étoiles), jamais
/// journalisé, envoyé une seule fois dans les arguments de l'ordre, puis oublié.
/// </summary>
/// <remarks>
/// Un code commencé puis abandonné expire au bout de <see cref="Expiration"/> : sans ça, le suivant qui passe devant
/// la platine n'aurait qu'à appuyer sur « Désarmer ». Le message (« code incorrect »…) s'efface de lui-même.
/// </remarks>
public sealed class Clavier(TimeProvider horloge)
{
    public const int MaxChiffres = 12;
    public static readonly TimeSpan Expiration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DureeMessage = TimeSpan.FromSeconds(10);

    private readonly Lock _verrou = new();
    private string _code = "";
    private DateTimeOffset _derniereTouche;
    private string? _message;
    private DateTimeOffset _messageJusqua;

    /// <summary>Un chiffre, « C » (tout effacer) ou « &lt; » (effacer le dernier).</summary>
    public void Appuyer(string touche)
    {
        lock (_verrou)
        {
            var maintenant = horloge.GetUtcNow();
            if (maintenant - _derniereTouche >= Expiration) _code = "";
            _derniereTouche = maintenant;
            _message = null;
            _code = touche switch
            {
                "C" => "",
                "<" => _code.Length > 0 ? _code[..^1] : "",
                [var c] when char.IsAsciiDigit(c) && _code.Length < MaxChiffres => _code + c,
                _ => _code,
            };
        }
    }

    /// <summary>Le code saisi, qui est aussitôt oublié ; null s'il n'y en a pas (ou plus : expiré).</summary>
    public string? Prendre()
    {
        lock (_verrou)
        {
            var code = Valide() ? _code : null;
            _code = "";
            return code is { Length: > 0 } ? code : null;
        }
    }

    public void Signaler(string message)
    {
        lock (_verrou)
        {
            _message = message;
            _messageJusqua = horloge.GetUtcNow() + DureeMessage;
        }
    }

    public EtatClavier Etat()
    {
        lock (_verrou)
        {
            var masque = Valide() ? string.Join(' ', Enumerable.Repeat('*', _code.Length)) : "";
            return new EtatClavier(masque, horloge.GetUtcNow() < _messageJusqua ? _message : null);
        }
    }

    private bool Valide() => _code.Length > 0 && horloge.GetUtcNow() - _derniereTouche < Expiration;
}
