namespace Krissvia.Plugin.OpenHasp;

/// <summary>
/// N'envoie à une platine que ce qui a changé — sans quoi chaque message de sonde repousserait tout l'écran des trois
/// platines — mais renvoie quand même au bout de <see cref="RafraichissementForce"/> : une platine peut avoir perdu un
/// attribut sans qu'on le sache (message perdu, redémarrage dont l'annonce s'est perdue).
/// </summary>
public sealed class Republication(TimeProvider horloge)
{
    public static readonly TimeSpan RafraichissementForce = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, (string Valeur, DateTimeOffset Quand)> _envoyes = new(StringComparer.Ordinal);
    private readonly Lock _verrou = new();

    /// <summary>Faut-il envoyer cette valeur ? Ne retient rien : <see cref="Noter"/> le fait une fois l'envoi réussi.</summary>
    public bool AEnvoyer(string noeud, string attribut, string valeur)
    {
        lock (_verrou)
        {
            return !_envoyes.TryGetValue(Cle(noeud, attribut), out var precedent)
                   || precedent.Valeur != valeur
                   || horloge.GetUtcNow() - precedent.Quand >= RafraichissementForce;
        }
    }

    /// <summary>Retenu APRÈS l'envoi : une publication ratée doit repartir au passage suivant.</summary>
    public void Noter(string noeud, string attribut, string valeur)
    {
        lock (_verrou) _envoyes[Cle(noeud, attribut)] = (valeur, horloge.GetUtcNow());
    }

    public void Oublier(string noeud, string attribut)
    {
        lock (_verrou) _envoyes.Remove(Cle(noeud, attribut));
    }

    /// <summary>
    /// Une platine qui redémarre a tout perdu : on oublie ce qu'on lui croyait affiché. Elle seule : vider tout
    /// ferait repeindre les trois platines parce qu'une a redémarré.
    /// </summary>
    public void OublierNoeud(string noeud)
    {
        var prefixe = noeud + "/";
        lock (_verrou)
            foreach (var cle in _envoyes.Keys.Where(k => k.StartsWith(prefixe, StringComparison.Ordinal)).ToList())
                _envoyes.Remove(cle);
    }

    private static string Cle(string noeud, string attribut) => $"{noeud}/{attribut}";
}
