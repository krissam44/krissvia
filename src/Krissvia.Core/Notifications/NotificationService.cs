using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krissvia.Core.Configuration;

namespace Krissvia.Core.Notifications;

/// <summary>Le serveur ntfy et le sujet où partent les notifications (le téléphone s'y abonne).</summary>
public sealed record NotificationSettings
{
    public bool Enabled { get; init; }

    /// <summary>Le serveur public ou un serveur à soi (« https://ntfy.exemple.fr »).</summary>
    public string BaseUrl { get; init; } = "https://ntfy.sh";

    /// <summary>
    /// Le sujet. Sur le serveur public, n'importe qui le connaissant peut lire et écrire : il doit être difficile à
    /// deviner, ou protégé par un jeton sur un serveur à soi.
    /// </summary>
    public string Topic { get; init; } = "";

    /// <summary>Jeton d'accès (en-tête <c>Authorization: Bearer</c>), pour un serveur protégé.</summary>
    public string? Token { get; init; }

    [JsonIgnore]
    public bool IsConfigured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && Topic.Length > 0;
}

/// <summary>Un message : titre, texte, priorité ntfy (1 à 5) et étiquettes (« snowflake » devient ❄️).</summary>
public sealed record Notification(string Title, string Message, int Priority = 3, IReadOnlyList<string>? Tags = null);

public enum SendOutcome { Sent, Disabled, NotConfigured, Failed }

public sealed record SendResult(SendOutcome Outcome, string? Detail = null)
{
    public bool Sent => Outcome == SendOutcome.Sent;
}

public sealed class NotificationValidationException(string message) : Exception(message);

/// <summary>
/// Envoi des notifications push par ntfy (https://ntfy.sh), comme DomoMud. Publication en JSON plutôt qu'en
/// en-têtes : les en-têtes HTTP n'acceptent que l'ASCII, et DomoMud devait y remplacer les accents des titres.
/// </summary>
/// <remarks>
/// Les réglages (dont le jeton) sont dans la collection <c>notifications</c>, en clair comme ceux des plugins : la
/// base reste en mode 600, et ses sauvegardes aussi. Ne lève jamais d'exception à l'envoi : le résultat le dit.
/// </remarks>
public sealed partial class NotificationService(IConfigurationStore store, HttpClient http)
{
    public const string Collection = "notifications";
    public const string DocumentId = "ntfy";

    /// <summary>Un envoi qui traîne ne doit pas retenir une boucle d'alertes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private NotificationSettings? _reglages;

    public event Action? Changed;

    public async Task<NotificationSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        if (_reglages is { } r) return r;
        var document = (await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId);
        try
        {
            _reglages = document is null ? new NotificationSettings() : JsonSerializer.Deserialize<NotificationSettings>(document.Json, Json) ?? new();
        }
        catch (JsonException)
        {
            _reglages = new NotificationSettings();   // illisible : désactivé, la page le montrera vide
        }

        return _reglages;
    }

    public async Task SaveAsync(NotificationSettings reglages, CancellationToken ct = default)
    {
        Validate(reglages);
        var propres = reglages with
        {
            BaseUrl = reglages.BaseUrl.Trim().TrimEnd('/'),
            Topic = reglages.Topic.Trim(),
            Token = string.IsNullOrWhiteSpace(reglages.Token) ? null : reglages.Token.Trim(),
        };
        await store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, JsonSerializer.Serialize(propres, Json)), ct);
        _reglages = propres;
        Changed?.Invoke();
    }

    public static void Validate(NotificationSettings r)
    {
        if (!Uri.TryCreate(r.BaseUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new NotificationValidationException("adresse du serveur : http:// ou https://");
        if (r.Topic.Trim().Length > 0 && !Sujet().IsMatch(r.Topic.Trim()))
            throw new NotificationValidationException("sujet : lettres, chiffres, « - » et « _ », 64 caractères au plus");
        if (r.Enabled && r.Topic.Trim().Length == 0)
            throw new NotificationValidationException("un sujet est nécessaire pour activer les notifications");
    }

    /// <summary>
    /// Envoie un message avec les réglages enregistrés, ou avec ceux de <paramref name="essai"/> (le bouton
    /// « Envoyer un essai » éprouve un formulaire avant de l'enregistrer, et même désactivé).
    /// </summary>
    public async Task<SendResult> SendAsync(Notification n, NotificationSettings? essai = null, CancellationToken ct = default)
    {
        var r = essai ?? await GetSettingsAsync(ct);
        if (essai is null && !r.Enabled) return new SendResult(SendOutcome.Disabled);
        if (!r.IsConfigured) return new SendResult(SendOutcome.NotConfigured, "adresse ou sujet manquant");

        try
        {
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(Timeout);
            using var requete = new HttpRequestMessage(HttpMethod.Post, r.BaseUrl.TrimEnd('/'))
            {
                // Corps de longueur connue (Content-Length) plutôt qu'envoyé par morceaux : certains relais devant un
                // serveur ntfy à soi refusent le « chunked ».
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    topic = r.Topic,
                    title = n.Title,
                    message = n.Message,
                    priority = Math.Clamp(n.Priority, 1, 5),
                    tags = n.Tags,
                }, Json), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(r.Token)) requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Token);

            using var reponse = await http.SendAsync(requete, delai.Token);
            return reponse.IsSuccessStatusCode
                ? new SendResult(SendOutcome.Sent)
                : new SendResult(SendOutcome.Failed, $"le serveur répond {(int)reponse.StatusCode} {reponse.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(SendOutcome.Failed, $"pas de réponse en {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            // Le message du système (« connexion refusée », « hôte inconnu »), dans la langue de la machine, pas
            // l'enveloppe anglaise de .NET.
            var cause = (ex.InnerException as System.Net.Sockets.SocketException)?.Message ?? ex.HttpRequestError.ToString();
            return new SendResult(SendOutcome.Failed, $"serveur injoignable ({cause.TrimEnd('.', ' ')})");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Sujet();
}
