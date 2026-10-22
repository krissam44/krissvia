using System.Security.Cryptography;

namespace Krissvia.Climate.Alarm;

/// <summary>Un code d'alarme enregistré : jamais le code lui-même, son empreinte PBKDF2 et son sel.</summary>
public sealed record AlarmCode(string Id, string Name, string Salt, string Hash);

/// <summary>
/// Hachage et vérification des codes d'alarme (DomoMud).
/// </summary>
/// <remarks>
/// PBKDF2 protège la valeur STOCKÉE (une base volée), pas l'espace des codes : quatre chiffres font dix mille
/// possibilités qu'aucun hachage ne rend impraticables. La vraie protection est <see cref="AlarmLockout"/>.
/// </remarks>
public static class AlarmCodes
{
    /// <summary>Quelques dizaines de millisecondes par vérification sur un Raspberry Pi 4 : invisible à la saisie.</summary>
    public const int Iterations = 100_000;

    public const int MinimumLength = 4;
    public const int MaximumLength = 12;

    private const int TailleSel = 16;
    private const int TailleEmpreinte = 32;

    public static AlarmCode Create(string id, string nom, string code)
    {
        var sel = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TailleSel));
        return new AlarmCode(id, nom, sel, Hash(code, sel));
    }

    public static string Hash(string code, string sel) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
        code, Convert.FromBase64String(sel), Iterations, HashAlgorithmName.SHA256, TailleEmpreinte));

    /// <summary>
    /// Comparaison à temps constant : une comparaison ordinaire s'arrête au premier octet différent et laisse mesurer la
    /// progression. Une ligne illisible (sel ou empreinte hors base64) n'est jamais un code valide.
    /// </summary>
    public static bool Verify(string code, AlarmCode enregistre)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(enregistre.Salt) || string.IsNullOrEmpty(enregistre.Hash)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(Hash(code, enregistre.Salt)), Convert.FromBase64String(enregistre.Hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Le nom de celui dont c'est le code, ou null.</summary>
    public static string? Identify(string code, IEnumerable<AlarmCode> codes) =>
        codes.FirstOrDefault(c => Verify(code, c))?.Name;

    /// <summary>Des chiffres seulement, de 4 à 12 : ce que tape un digicode.</summary>
    public static bool IsAcceptable(string? code) =>
        code is { Length: >= MinimumLength and <= MaximumLength } && code.All(char.IsAsciiDigit);
}

/// <summary>Le verrou anti-force brute. <c>default</c> : aucun échec.</summary>
public readonly record struct AlarmLockState(int Failures, DateTimeOffset? LockedUntil);

/// <summary>
/// Trois codes faux, soixante secondes de blocage : la règle du digicode mural, appliquée aussi côté serveur pour que
/// l'API ou le téléphone ne deviennent pas la porte dérobée du clavier qui, lui, se bloque.
/// </summary>
/// <remarks>
/// Compteur GLOBAL et non par personne : avant d'avoir vérifié le code, on ne sait pas qui tape.
/// </remarks>
public static class AlarmLockout
{
    public const int FailuresBeforeLock = 3;
    public static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    public static bool IsLocked(AlarmLockState v, DateTimeOffset maintenant) => v.LockedUntil is { } jusque && maintenant < jusque;

    public static int SecondsLeft(AlarmLockState v, DateTimeOffset maintenant) =>
        IsLocked(v, maintenant) ? (int)Math.Ceiling((v.LockedUntil!.Value - maintenant).TotalSeconds) : 0;

    /// <summary>
    /// Un code refusé. Au seuil le clavier se bloque et le compteur repart de zéro : sans quoi la première tentative
    /// après l'expiration rebloquerait aussitôt.
    /// </summary>
    public static AlarmLockState Failure(AlarmLockState v, DateTimeOffset maintenant)
    {
        var echecs = v.Failures + 1;
        return echecs >= FailuresBeforeLock ? new AlarmLockState(0, maintenant + LockDuration) : v with { Failures = echecs };
    }

    /// <summary>Un code accepté efface tout.</summary>
    public static AlarmLockState Success() => default;
}
