using Krissvia.Mobile.Client;

namespace Krissvia.Mobile.Services;

/// <summary>
/// Adresse de Krissvia et clé d'API. La clé est un secret : coffre de l'appareil (<see cref="SecureStorage"/>, Keystore
/// Android), jamais les Preferences en clair. L'adresse n'a rien de sensible.
/// </summary>
public sealed class ConnexionSettings : IConnexion
{
    private const string CleUrl = "krissvia.url";
    private const string CleApi = "krissvia.apikey";

    private bool _charge;

    public string BaseUrl { get; private set; } = string.Empty;
    public string ApiKey { get; private set; } = string.Empty;

    public bool EstConfigure => BaseUrl.Length > 0;

    public async Task ChargerAsync()
    {
        if (_charge) return;
        // Normalisée aussi à la lecture : une adresse enregistrée avant une règle resterait sinon fautive.
        BaseUrl = AdresseServeur.Normaliser(Preferences.Default.Get(CleUrl, string.Empty));
        try
        {
            ApiKey = await SecureStorage.Default.GetAsync(CleApi) ?? string.Empty;
        }
        catch (Exception)
        {
            // Coffre indisponible (appareil sans verrouillage d'écran, émulateur) : la clé sera redemandée.
            ApiKey = string.Empty;
        }
        _charge = true;
    }

    public async Task EnregistrerAsync(string adresse, string cle)
    {
        BaseUrl = AdresseServeur.Normaliser(adresse);
        ApiKey = cle.Trim();
        _charge = true;

        Preferences.Default.Set(CleUrl, BaseUrl);
        try
        {
            if (ApiKey.Length == 0) SecureStorage.Default.Remove(CleApi);
            else await SecureStorage.Default.SetAsync(CleApi, ApiKey);
        }
        catch (Exception)
        {
            // Voir ChargerAsync : l'appli fonctionne pour cette session, la clé sera redemandée.
        }
    }
}
