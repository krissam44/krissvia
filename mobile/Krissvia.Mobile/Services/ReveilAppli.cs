namespace Krissvia.Mobile.Services;

/// <summary>
/// Le retour de l'appli au premier plan, pour les écrans qui interrogent le serveur : après une heure dans la poche,
/// ils relisent tout de suite au lieu d'attendre leur prochain tour.
/// </summary>
public sealed class ReveilAppli
{
    public event Action? Reveillee;

    public void Signaler() => Reveillee?.Invoke();
}
