using Krissvia.Mobile.Client;

namespace Krissvia.Mobile.Services;

/// <summary>
/// Les lieux (une installation Krissvia chacun : l'appartement, la maison) et celui qu'on regarde. Le carnet — noms
/// et adresses, rien de sensible — vit dans les Preferences ; la clé d'API de chaque lieu est un secret, rangée dans le
/// coffre de l'appareil (<see cref="SecureStorage"/>, Keystore Android) sous l'identifiant du lieu.
/// </summary>
/// <remarks>
/// <see cref="BaseUrl"/> et <see cref="ApiKey"/> sont ceux du lieu affiché : le client de l'API et le flux les relisent
/// à chaque appel, changer de lieu ne demande donc que de relancer le flux.
/// </remarks>
public sealed class ConnexionSettings : IConnexion
{
    private const string CleCarnet = "krissvia.lieux";
    private const string PrefixeCle = "krissvia.apikey.";

    // Avant les lieux : une seule adresse et une seule clé, reprises au premier lancement de la nouvelle version.
    private const string AncienneCleUrl = "krissvia.url";
    private const string AncienneCleApi = "krissvia.apikey";

    private CarnetLieux _carnet = new();
    private Task? _chargement;

    /// <summary>Levé après un changement du lieu affiché (choix, enregistrement, suppression du lieu affiché).</summary>
    public event Action? LieuChange;

    public IReadOnlyList<Lieu> Lieux => _carnet.Lieux;
    public Lieu? Actif => _carnet.Actif;

    public string BaseUrl => _carnet.Actif?.BaseUrl ?? string.Empty;
    public string ApiKey { get; private set; } = string.Empty;

    public bool EstConfigure => BaseUrl.Length > 0;

    /// <summary>
    /// Une seule lecture, partagée : la barre du haut et l'écran la demandent en même temps au démarrage, et deux
    /// reprises de l'ancien réglage créeraient deux lieux.
    /// </summary>
    public Task ChargerAsync() => _chargement ??= LireAsync();

    private async Task LireAsync()
    {
        var (carnet, repris) = CarnetLieux.Lire(Preferences.Default.Get(CleCarnet, string.Empty), Preferences.Default.Get(AncienneCleUrl, string.Empty));
        _carnet = carnet;
        if (repris is not null)
        {
            // La clé d'avant passe sous le lieu repris ; les anciennes entrées s'effacent une fois le carnet écrit.
            await EcrireCleAsync(repris.Id, await LireCleAsync(AncienneCleApi));
            Sauver();
            Preferences.Default.Remove(AncienneCleUrl);
            Effacer(AncienneCleApi);
        }
        await ActualiserCleAsync();
    }

    /// <summary>La clé enregistrée d'un lieu, pour la montrer dans son formulaire.</summary>
    public Task<string> CleDeAsync(string id) => LireCleAsync(PrefixeCle + id);

    /// <summary>Ajoute (<paramref name="id"/> null) ou modifie un lieu, et l'affiche : on vient de le régler pour s'y connecter.</summary>
    public async Task<Lieu> EnregistrerAsync(string? id, string nom, string adresse, string cle)
    {
        await ChargerAsync();
        var lieu = id is null ? _carnet.Ajouter(nom, adresse) : _carnet.Modifier(id, nom, adresse);
        await EcrireCleAsync(lieu.Id, cle.Trim());
        _carnet.Choisir(lieu.Id);
        Sauver();
        await ActualiserCleAsync();
        LieuChange?.Invoke();
        return lieu;
    }

    public async Task ChoisirAsync(string id)
    {
        await ChargerAsync();
        if (!_carnet.Choisir(id)) return;
        Sauver();
        await ActualiserCleAsync();
        LieuChange?.Invoke();
    }

    public async Task SupprimerAsync(string id)
    {
        await ChargerAsync();
        var etaitAffiche = _carnet.Actif?.Id == id;
        _carnet.Supprimer(id);
        Effacer(PrefixeCle + id);
        Sauver();
        await ActualiserCleAsync();
        if (etaitAffiche) LieuChange?.Invoke();
    }

    private async Task ActualiserCleAsync() =>
        ApiKey = _carnet.Actif is { } actif ? await LireCleAsync(PrefixeCle + actif.Id) : string.Empty;

    private void Sauver() => Preferences.Default.Set(CleCarnet, _carnet.Ecrire());

    private static async Task<string> LireCleAsync(string entree)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(entree) ?? string.Empty;
        }
        catch (Exception)
        {
            // Coffre indisponible (appareil sans verrouillage d'écran, émulateur) : la clé sera redemandée.
            return string.Empty;
        }
    }

    private static async Task EcrireCleAsync(string id, string cle)
    {
        try
        {
            if (cle.Length == 0) SecureStorage.Default.Remove(PrefixeCle + id);
            else await SecureStorage.Default.SetAsync(PrefixeCle + id, cle);
        }
        catch (Exception)
        {
            // Voir LireCleAsync : l'appli fonctionne, la clé sera redemandée.
        }
    }

    private static void Effacer(string entree)
    {
        try
        {
            SecureStorage.Default.Remove(entree);
        }
        catch (Exception)
        {
            // Voir LireCleAsync.
        }
    }
}
