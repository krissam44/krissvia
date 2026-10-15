namespace Krissvia.Server.Components;

/// <summary>Libellés et couleurs des statuts de service : une seule source pour la liste et la fiche.</summary>
internal static class Statuts
{
    public static string Libelle(ServiceStatus statut) => statut switch
    {
        ServiceStatus.Connected => "Démarré",
        ServiceStatus.Starting => "Démarrage…",
        ServiceStatus.NotConnected => "Lancé, non connecté",
        ServiceStatus.Restarting => "Relance…",
        ServiceStatus.External => "Connecté (lancé ailleurs)",
        ServiceStatus.NotInstalled => "Non installé",
        _ => "Arrêté",
    };

    public static Color Couleur(ServiceStatus statut) => statut switch
    {
        ServiceStatus.Connected => Color.Success,
        ServiceStatus.Starting or ServiceStatus.External => Color.Info,
        ServiceStatus.NotConnected or ServiceStatus.Restarting => Color.Warning,
        ServiceStatus.NotInstalled => Color.Error,
        _ => Color.Default,
    };
}
