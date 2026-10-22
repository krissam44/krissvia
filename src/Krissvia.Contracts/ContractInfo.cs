namespace Krissvia.Contracts;

/// <summary>Version du contrat plugin ↔ cœur. Tant qu'elle commence par « 0. », rien n'est garanti stable.</summary>
public static class ContractInfo
{
    public const string Version = "0.3";

    /// <summary>
    /// Les versions 0.x que ce cœur comprend aussi. La 0.3 n'a fait qu'AJOUTER des messages (docs/CONTRACT-V0.3.md) :
    /// un plugin 0.2 reste accepté, et ne voit aucune différence.
    /// </summary>
    public static readonly IReadOnlyList<string> AlsoUnderstood = ["0.2"];

    /// <summary>
    /// Le cœur comprend-il un plugin qui parle <paramref name="pluginVersion"/> ? En 0.x, chaque version mineure
    /// peut tout casser : il faut la même, ou une de celles qu'il comprend aussi. À partir de 1.0 : même majeure,
    /// mineure inférieure ou égale.
    /// </summary>
    public static bool IsCompatible(string? pluginVersion)
    {
        if (!TryParse(pluginVersion, out var plugin) || !TryParse(Version, out var coeur)) return false;
        if (plugin.Major != coeur.Major) return false;

        return coeur.Major == 0
            ? plugin.Minor == coeur.Minor || AlsoUnderstood.Contains($"{plugin.Major}.{plugin.Minor}")
            : plugin.Minor <= coeur.Minor;
    }

    private static bool TryParse(string? version, out (int Major, int Minor) resultat)
    {
        resultat = default;
        var parties = version?.Split('.');
        if (parties is not { Length: 2 }) return false;
        if (!int.TryParse(parties[0], out var majeure) || !int.TryParse(parties[1], out var mineure)) return false;
        if (majeure < 0 || mineure < 0) return false;

        resultat = (majeure, mineure);
        return true;
    }
}
