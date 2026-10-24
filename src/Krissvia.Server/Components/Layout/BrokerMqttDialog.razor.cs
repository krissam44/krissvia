using System.Text.Json.Nodes;
using Krissvia.PluginHost.Services;

namespace Krissvia.Server.Components.Layout;

/// <summary>
/// Saisie du broker commun. À la première ouverture, reprend le broker déjà saisi dans un plugin (rien à retaper). En
/// enregistrant : propose de retirer le broker propre des plugins qui en avaient un, puis relance les plugins MQTT activés,
/// qui ne lisent leurs réglages qu'au démarrage.
/// </summary>
public partial class BrokerMqttDialog
{
    private static readonly string[] ClesBroker = ["Host", "Port", "Username", "Password"];

    [CascadingParameter] private IMudDialogInstance Dialogue { get; set; } = default!;
    [Inject] private SharedBrokerService Broker { get; set; } = default!;
    [Inject] private PluginSettingsService Reglages { get; set; } = default!;
    [Inject] private PluginSupervisor Superviseur { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private string _hote = "";
    private int _port = 1883;
    private string? _utilisateur;
    private string? _motDePasse;
    private string? _motDePasseExistant;
    private bool _motDePasseEnregistre;
    private bool _unifier = true;
    private bool _occupe;
    private string? _erreur;
    private MqttOverview _etat = new(MqttHealth.NoPlugin, []);

    /// <summary>Vrai tant que rien n'est enregistré : ce qu'on voit est une proposition, pas le broker commun.</summary>
    private bool _nonEnregistre;

    /// <summary>Le plugin dont la saisie est reprise, quand rien de commun n'est encore enregistré.</summary>
    private string? _reprisDe;

    /// <summary>Les plugins MQTT qui ont saisi leur propre broker, avec la section concernée.</summary>
    private List<(string Id, string Name, PluginManifest Fiche, string Section)> _propres = [];

    protected override void OnInitialized()
    {
        _etat = Superviseur.MqttStatus();
        _propres = [.. Superviseur.Snapshots()
            .Select(s => (s, Fiche: Superviseur.GetManifest(s.Id)))
            .Where(x => x.Fiche is not null && Reglages.HasSaved(x.s.Id))
            .SelectMany(x => SettingsSchema.SharedSections(SettingsSchema.Parse(x.Fiche!.Settings))
                .Where(sec => sec.Kind == "mqtt" && Section(Reglages.Get(x.Fiche!), sec.Path)?["Host"]?.GetValue<string>() is { Length: > 0 })
                .Select(sec => (x.s.Id, x.s.Name, x.Fiche!, sec.Path)))];

        if (Broker.Current is { IsConfigured: true } actuel)
        {
            (_hote, _port, _utilisateur) = (actuel.Host, actuel.Port, actuel.Username);
            _motDePasseExistant = actuel.Password;
            _motDePasseEnregistre = actuel.Password is not null;
            return;
        }

        // Rien de commun encore : on reprend le broker du premier plugin qui en a un — et on le DIT, sinon la saisie
        // pré-remplie passe pour le broker commun déjà enregistré.
        _nonEnregistre = true;
        if (_propres.FirstOrDefault() is { Fiche: not null } premier && Section(Reglages.Get(premier.Fiche), premier.Section) is { } s)
        {
            _reprisDe = premier.Name;
            _hote = s["Host"]?.GetValue<string>() ?? "";
            _port = s["Port"] is JsonValue p && p.TryGetValue<int>(out var port) ? port : 1883;
            _utilisateur = s["Username"]?.GetValue<string>();
            _motDePasseExistant = s["Password"]?.GetValue<string>();
            _motDePasseEnregistre = _motDePasseExistant is { Length: > 0 };
        }
    }

    private static JsonObject? Section(JsonObject reglages, string chemin)
    {
        JsonNode? noeud = reglages;
        foreach (var cle in chemin.Split("__")) noeud = noeud?[cle];
        return noeud as JsonObject;
    }

    private void Annuler() => Dialogue.Cancel();

    private async Task EnregistrerAsync()
    {
        _occupe = true;
        _erreur = null;
        try
        {
            await Broker.SaveAsync(new SharedBroker
            {
                Host = _hote,
                Port = _port,
                Username = _utilisateur,
                Password = string.IsNullOrEmpty(_motDePasse) ? _motDePasseExistant : _motDePasse,
            });

            if (_unifier)
            {
                foreach (var (id, nom, fiche, section) in _propres)
                {
                    var valeur = Reglages.Get(fiche);
                    if (Section(valeur, section) is { } s)
                        foreach (var cle in ClesBroker) s.Remove(cle);   // le reste de la section (BaseTopic…) reste
                    if (await Reglages.SaveAsync(fiche, valeur) is { Count: > 0 } erreurs)
                        throw new ArgumentException($"{nom} : {string.Join(" ; ", erreurs)}");
                }
            }

            // Les plugins ne lisent leurs réglages qu'au démarrage.
            var relances = Superviseur.UsingShared("mqtt");
            foreach (var plugin in relances) await Superviseur.RestartAsync(plugin.Id);
            Snackbar.Add(relances.Count == 0
                ? "Broker enregistré."
                : $"Broker enregistré ; relancés : {string.Join(", ", relances.Select(p => p.Name))}.", Severity.Success);
            Dialogue.Close(DialogResult.Ok(true));
        }
        catch (ArgumentException ex)
        {
            _erreur = Phrases.Majuscule(ex.Message);
        }
        finally
        {
            _occupe = false;
        }
    }
}
