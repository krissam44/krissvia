namespace Krissvia.Contracts;

/// <summary>Version du contrat plugin ↔ cœur. Tant qu'elle commence par « 0. », rien n'est garanti stable.</summary>
public static class ContractInfo
{
    public const string Version = "0.2";

    /// <summary>
    /// Le cœur comprend-il un plugin qui parle <paramref name="pluginVersion"/> ? En 0.x, chaque version mineure
    /// peut tout casser : il faut la même. À partir de 1.0 : même majeure, mineure inférieure ou égale.
    /// </summary>
    public static bool IsCompatible(string? pluginVersion)
    {
        if (!TryParse(pluginVersion, out var plugin) || !TryParse(Version, out var coeur)) return false;
        if (plugin.Major != coeur.Major) return false;

        return coeur.Major == 0 ? plugin.Minor == coeur.Minor : plugin.Minor <= coeur.Minor;
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
