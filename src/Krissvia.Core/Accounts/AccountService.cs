using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Krissvia.Core.Configuration;

namespace Krissvia.Core.Accounts;

public enum LoginOutcome { Ok, Invalid, Locked }

public sealed record LoginResult(LoginOutcome Outcome, int LockedSeconds = 0);

/// <summary>
/// Comptes de l'interface d'administration : identifiant et mot de passe haché (PBKDF2-SHA256, sel propre à chaque
/// compte), enregistrés dans la collection <c>comptes</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Tant qu'aucun compte n'existe, un jeton d'installation à usage unique est tiré au démarrage et écrit dans le
/// journal du serveur : sans lui, le premier venu sur le réseau créerait le compte avant le propriétaire.</item>
/// <item>Verrou : 5 échecs, puis 1 minute de refus. Compteur GLOBAL, comme celui du digicode de DomoMud : il ne
/// dépend pas de l'identifiant tapé, sinon on essaierait les mots de passe sur des noms inventés. Remis à zéro au
/// verrouillage, sinon la première tentative après expiration reverrouillerait aussitôt.</item>
/// <item>On ne supprime ni le dernier compte, ni le sien : l'interface ne doit jamais devenir inaccessible.</item>
/// </list>
/// </remarks>
public sealed partial class AccountService(IConfigurationStore store, TimeProvider clock)
{
    public const string Collection = "comptes";
    public const int MinPasswordLength = 10;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(1);
    private const int Iterations = 100_000;

    private readonly SemaphoreSlim _verrou = new(1, 1);
    private Dictionary<string, Compte> _comptes = new(StringComparer.Ordinal);
    private int _echecs;
    private DateTimeOffset? _verrouJusqua;

    private sealed record Compte(string Hash, string Salt, int Iterations, DateTimeOffset CreatedAt);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public bool HasAccounts => _comptes.Count > 0;

    /// <summary>Jeton d'installation, tant qu'aucun compte n'existe ; null ensuite.</summary>
    public string? SetupToken { get; private set; }

    public IReadOnlyList<string> Users => _comptes.Keys.Order(StringComparer.Ordinal).ToList();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var comptes = new Dictionary<string, Compte>(StringComparer.Ordinal);
            foreach (var document in await store.ListAsync(Collection, ct))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Compte>(document.Json, Json) is { } compte) comptes[document.Id] = compte;
                }
                catch (JsonException)
                {
                    // Un compte illisible ne doit pas empêcher les autres de se connecter.
                }
            }

            _comptes = comptes;
            SetupToken = comptes.Count == 0 ? Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) : null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Crée le premier compte, avec le jeton du journal. Renvoie la raison d'un refus, ou null.</summary>
    public async Task<string?> CreateFirstAsync(string? token, string user, string password, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (_comptes.Count > 0 || SetupToken is null) return "un compte existe déjà : connectez-vous.";
            if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(SetupToken), Encoding.UTF8.GetBytes(token.Trim())))
                return "jeton d'installation faux (il est écrit dans le journal du serveur).";

            if (Verifier(user, password) is { } refus) return refus;
            await EnregistrerAsync(user, password, ct);
            SetupToken = null;
            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<LoginResult> VerifyAsync(string user, string password, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            var maintenant = clock.GetUtcNow();
            if (_verrouJusqua is { } jusqua && maintenant < jusqua)
                return new LoginResult(LoginOutcome.Locked, (int)Math.Ceiling((jusqua - maintenant).TotalSeconds));

            // Le hachage est calculé même pour un identifiant inconnu : la durée ne dit pas s'il existe.
            var compte = _comptes.GetValueOrDefault(user ?? "");
            var sel = compte is null ? RandomNumberGenerator.GetBytes(16) : Convert.FromBase64String(compte.Salt);
            var calcule = Hacher(password ?? "", sel, compte?.Iterations ?? Iterations);
            if (compte is not null && CryptographicOperations.FixedTimeEquals(calcule, Convert.FromBase64String(compte.Hash)))
            {
                _echecs = 0;
                return new LoginResult(LoginOutcome.Ok);
            }

            if (++_echecs >= MaxFailures)
            {
                _echecs = 0;
                _verrouJusqua = maintenant + LockDuration;
                return new LoginResult(LoginOutcome.Locked, (int)LockDuration.TotalSeconds);
            }

            return new LoginResult(LoginOutcome.Invalid);
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<string?> CreateAsync(string user, string password, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (_comptes.ContainsKey(user)) return $"le compte « {user} » existe déjà.";
            if (Verifier(user, password) is { } refus) return refus;
            await EnregistrerAsync(user, password, ct);
            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<string?> ChangePasswordAsync(string user, string current, string nouveau, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_comptes.TryGetValue(user, out var compte)) return "compte inconnu.";
            if (!CryptographicOperations.FixedTimeEquals(Hacher(current ?? "", Convert.FromBase64String(compte.Salt), compte.Iterations),
                    Convert.FromBase64String(compte.Hash)))
                return "mot de passe actuel faux.";
            if (VerifierMotDePasse(nouveau) is { } refus) return refus;

            await EnregistrerAsync(user, nouveau, ct);
            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task<string?> DeleteAsync(string user, string currentUser, CancellationToken ct = default)
    {
        await _verrou.WaitAsync(ct);
        try
        {
            if (!_comptes.ContainsKey(user)) return "compte inconnu.";
            if (user == currentUser) return "on ne supprime pas son propre compte.";
            if (_comptes.Count == 1) return "c'est le dernier compte : l'interface deviendrait inaccessible.";

            await store.DeleteAsync(Collection, user, ct);
            _comptes.Remove(user);
            return null;
        }
        finally
        {
            _verrou.Release();
        }
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    private async Task EnregistrerAsync(string user, string password, CancellationToken ct)
    {
        var sel = RandomNumberGenerator.GetBytes(16);
        var compte = new Compte(Convert.ToBase64String(Hacher(password, sel, Iterations)), Convert.ToBase64String(sel), Iterations, clock.GetUtcNow());
        await store.SaveAsync(Collection, new ConfigurationDocument(user, JsonSerializer.Serialize(compte, Json)), ct);
        _comptes[user] = compte;
    }

    private static byte[] Hacher(string password, byte[] sel, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, sel, iterations, HashAlgorithmName.SHA256, 32);

    private static string? Verifier(string user, string password) =>
        user is null || !IdentifiantValide().IsMatch(user)
            ? "identifiant : 2 à 32 caractères, minuscules, chiffres, « . », « _ » ou « - »."
            : VerifierMotDePasse(password);

    private static string? VerifierMotDePasse(string password) =>
        password is null || password.Length < MinPasswordLength ? $"mot de passe : au moins {MinPasswordLength} caractères." : null;

    [GeneratedRegex("^[a-z0-9._-]{2,32}$")]
    private static partial Regex IdentifiantValide();
}
