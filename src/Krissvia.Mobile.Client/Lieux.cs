using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>
/// Une installation Krissvia que l'appli sait joindre : l'appartement, la maison… Sa clé d'API n'est pas ici : elle
/// vit dans le coffre de l'appareil, sous <see cref="Id"/>.
/// </summary>
public sealed record Lieu(string Id, string Nom, string BaseUrl);

/// <summary>
/// Les lieux connus et celui qu'on regarde. Chaque installation est indépendante (son broker, ses équipements, sa
/// clé) : l'appli en suit une à la fois. Aucune entrée-sortie ici — l'appli range le JSON de <see cref="Ecrire"/> dans
/// ses Preferences — ce qui le rend testable sans appareil.
/// </summary>
public sealed class CarnetLieux
{
    private readonly List<Lieu> _lieux = [];

    public IReadOnlyList<Lieu> Lieux => _lieux;

    /// <summary>Le lieu affiché ; null tant qu'aucun n'est enregistré.</summary>
    public Lieu? Actif { get; private set; }

    /// <summary>Ajoute un lieu (adresse normalisée) ; le premier devient le lieu affiché.</summary>
    public Lieu Ajouter(string nom, string adresse)
    {
        var lieu = new Lieu(Guid.NewGuid().ToString("N"), NomOuDefaut(nom), AdresseServeur.Normaliser(adresse));
        _lieux.Add(lieu);
        Actif ??= lieu;
        return lieu;
    }

    public Lieu Modifier(string id, string nom, string adresse)
    {
        var i = Index(id);
        var lieu = _lieux[i] with { Nom = NomOuDefaut(nom, i), BaseUrl = AdresseServeur.Normaliser(adresse) };
        _lieux[i] = lieu;
        if (Actif?.Id == id) Actif = lieu;
        return lieu;
    }

    /// <summary>Retire un lieu ; si c'était celui affiché, on passe au premier qui reste.</summary>
    public void Supprimer(string id)
    {
        _lieux.RemoveAt(Index(id));
        if (Actif?.Id == id) Actif = _lieux.FirstOrDefault();
    }

    /// <summary>Vrai si le lieu affiché a changé.</summary>
    public bool Choisir(string id)
    {
        var lieu = _lieux[Index(id)];
        if (Actif?.Id == id) return false;
        Actif = lieu;
        return true;
    }

    public string Ecrire() => JsonSerializer.Serialize(new Document(_lieux, Actif?.Id));

    /// <summary>
    /// Relit le carnet. Sans carnet mais avec l'adresse d'avant les lieux (<paramref name="ancienneAdresse"/>), en fait
    /// le premier lieu : une appli mise à jour ne doit pas redemander sa connexion. Renvoie aussi ce lieu, pour que
    /// l'appli y range l'ancienne clé.
    /// </summary>
    public static (CarnetLieux Carnet, Lieu? Repris) Lire(string? json, string? ancienneAdresse)
    {
        var carnet = new CarnetLieux();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<Document>(json) is { } d)
                {
                    // Normalisée aussi à la lecture : une adresse enregistrée avant une règle resterait sinon fautive.
                    carnet._lieux.AddRange((d.Lieux ?? []).Where(l => l.Id.Length > 0).Select(l => l with { BaseUrl = AdresseServeur.Normaliser(l.BaseUrl) }));
                    carnet.Actif = carnet._lieux.FirstOrDefault(l => l.Id == d.Actif) ?? carnet._lieux.FirstOrDefault();
                    return (carnet, null);
                }
            }
            catch (JsonException)
            {
                // Carnet illisible : on repart de l'ancienne adresse s'il y en a une, sinon de rien.
            }
        }

        if (string.IsNullOrWhiteSpace(ancienneAdresse) || AdresseServeur.Normaliser(ancienneAdresse).Length == 0) return (carnet, null);
        return (carnet, carnet.Ajouter("Krissvia", ancienneAdresse));
    }

    private int Index(string id)
    {
        var i = _lieux.FindIndex(l => l.Id == id);
        return i >= 0 ? i : throw new KeyNotFoundException($"Lieu inconnu : {id}");
    }

    private string NomOuDefaut(string nom, int? index = null) =>
        nom.Trim() is { Length: > 0 } n ? n : $"Lieu {(index ?? _lieux.Count) + 1}";

    private sealed record Document(List<Lieu> Lieux, string? Actif);
}
