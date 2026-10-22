using System.Reflection;

namespace Krissvia.Core;

/// <summary>
/// La version d'un binaire et le commit dont il a été construit, lus dans son <c>AssemblyInformationalVersion</c>
/// (<c>1.0.0+edc186d3e1e4…</c>, que le SDK .NET écrit tout seul quand il compile dans un dépôt git). C'est ce qui permet
/// de savoir, d'un coup d'œil, quelle version tourne sur la VM ou sur le Pi.
/// </summary>
/// <param name="Version">La version (« 1.0.0 »).</param>
/// <param name="Commit">Le commit complet, null si le binaire a été construit hors d'un dépôt.</param>
public sealed record BuildVersion(string Version, string? Commit)
{
    /// <summary>Les 7 premiers caractères du commit, comme GitHub et <c>git log --oneline</c>.</summary>
    public string? ShortCommit => Commit is { Length: > 7 } c ? c[..7] : Commit;

    /// <summary>« 1.0.0 · edc186d », ou « 1.0.0 » seul.</summary>
    public string Label => ShortCommit is { } c ? $"{Version} · {c}" : Version;

    public static BuildVersion Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return new BuildVersion("?", null);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return new BuildVersion(informationalVersion.Trim(), null);
        var commit = informationalVersion[(plus + 1)..].Trim();
        return new BuildVersion(informationalVersion[..plus].Trim(), commit.Length > 0 ? commit : null);
    }

    public static BuildVersion Of(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? assembly.GetName().Version?.ToString(3));
}
