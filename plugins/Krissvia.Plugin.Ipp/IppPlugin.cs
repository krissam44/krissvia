using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;
using Krissvia.Plugin.Ipp.Decouverte;
using Krissvia.Plugin.Ipp.Protocole;
using Krissvia.Sdk;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.Ipp;

/// <summary>Les réglages du plugin (page Services).</summary>
/// <param name="Manuelles">Les adresses saisies, déjà normalisées (<see cref="IppAdresses.TryNormaliser"/>).</param>
/// <param name="Decouverte">Chercher aussi les imprimantes annoncées en mDNS.</param>
/// <param name="SeuilBas">Seuil « bas » d'un consommable dont l'imprimante ne donne pas le sien, en %.</param>
public sealed record OptionsIpp(IReadOnlyList<Uri> Manuelles, bool Decouverte, TimeSpan Periode, int SeuilBas = 10)
{
    /// <summary>La découverte n'est pas refaite à chaque relevé : une imprimante ne change pas d'adresse toutes les minutes.</summary>
    public TimeSpan PeriodeDecouverte { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Durée d'écoute des réponses mDNS.</summary>
    public TimeSpan EcouteDecouverte { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>Une imprimante déjà lue, retenue d'un lancement à l'autre (dossier de données, <c>imprimantes.json</c>).</summary>
public sealed record ImprimanteConnue(string Cle, string Url, string? Nom, string? Modele, bool Manuelle, IReadOnlyList<ConsommableConnu> Consommables);

public sealed record ConsommableConnu(string Id, string Nom);

/// <summary>
/// La mémoire des imprimantes. Sans elle, une imprimante ÉTEINTE au lancement du plugin — le cas ordinaire d'une
/// imprimante de maison — ne serait pas déclarée : ses entités passeraient « disparues » et ses cartes « Entité
/// inconnue », jusqu'à ce qu'on la rallume.
/// </summary>
public sealed class MemoireImprimantes(string? fichier)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<ImprimanteConnue> Load()
    {
        if (fichier is null || !File.Exists(fichier)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ImprimanteConnue>>(File.ReadAllText(fichier), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];   // illisible : on réapprendra au premier relevé
        }
    }

    public void Save(IEnumerable<ImprimanteConnue> imprimantes)
    {
        if (fichier is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        var temporaire = fichier + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(imprimantes.OrderBy(i => i.Cle, StringComparer.Ordinal).ToList(), Json));
        File.Move(temporaire, fichier, overwrite: true);
    }
}

/// <summary>
/// Les imprimantes IPP : découvertes en mDNS ou saisies, relevées par <c>Get-Printer-Attributes</c> — jamais rien
/// d'autre, le plugin ne sait pas imprimer. Pour chacune : un appareil, l'objet « Imprimante » que dessine la carte,
/// l'état (<c>enum</c>), un <c>problem</c> pour la supervision, et un capteur en % par consommable.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Clé</b> : l'UUID de l'imprimante (<c>printer-uuid</c>), qui survit à un changement d'adresse IP et réunit
/// ses deux annonces <c>_ipp</c> et <c>_ipps</c> ; à défaut, son adresse.</item>
/// <item><b>Éteinte</b> : après <see cref="EchecsAvantIndisponible"/> relevés ratés, l'appareil devient indisponible.
/// Ce n'est pas un problème — une imprimante de maison passe sa vie éteinte — donc pas d'alerte.</item>
/// <item>Les relevés (minuterie, commande « Vérifier ») passent un par un : jamais deux requêtes à la même imprimante.</item>
/// </list>
/// </remarks>
internal sealed partial class IppPlugin(
    IIppClient ipp,
    IDecouverteIpp? decouverte,
    TimeProvider clock,
    ILogger<IppPlugin> logger,
    OptionsIpp options,
    MemoireImprimantes? memoire = null) : ICorePlugin
{
    public const string Refresh = "refresh";
    public const int EchecsAvantIndisponible = 2;

    private sealed class Suivi(string cle)
    {
        public string Cle { get; } = cle;
        public required Uri Url { get; set; }
        public bool Manuelle { get; set; }
        public string? Nom { get; set; }
        public string? Modele { get; set; }
        public IReadOnlyList<ConsommableConnu> Consommables { get; set; } = [];
        public ReleveImprimante? Releve { get; set; }
        public Diagnostic? Diagnostic { get; set; }
        public DateTimeOffset? DernierReleve { get; set; }
        public int Echecs { get; set; }
        public bool Disponible { get; set; }

        public ImprimanteConnue Connue() => new(Cle, Url.ToString(), Nom, Modele, Manuelle, Consommables);
    }

    private readonly MemoireImprimantes _memoire = memoire ?? new MemoireImprimantes(null);
    private readonly SemaphoreSlim _serie = new(1, 1);
    private readonly Lock _verrou = new();
    private readonly Dictionary<string, Suivi> _imprimantes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cleParUrl = new(StringComparer.Ordinal);
    private readonly List<ServiceIpp> _decouvertes = [];
    private readonly HashSet<string> _injoignablesSignalees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _valeurs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _envoyes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _disponibilitesEnvoyees = new(StringComparer.Ordinal);
    private DateTimeOffset? _derniereDecouverte;
    private string? _declaration;
    private bool _chargee;

    public CoreClient? Coeur { get; set; }

    public static string EtatId(string cle) => $"{cle}:etat";
    public static string ProblemeId(string cle) => $"{cle}:probleme";
    public static string ConsommableId(string cle, string id) => $"{cle}:conso:{id}";

    public async Task RunAsync(CancellationToken ct)
    {
        using var minuterie = new PeriodicTimer(options.Periode, clock);
        try
        {
            do
            {
                await CycleAsync(ct);
            }
            while (await minuterie.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Au lancement : les imprimantes déjà connues, si elles sont encore voulues — saisie toujours présente dans les
    /// réglages, ou découverte toujours activée. Les autres ne sont plus déclarées : l'utilisateur les a retirées.
    /// </summary>
    private void Charger()
    {
        if (_chargee) return;
        _chargee = true;
        var manuelles = options.Manuelles.Select(u => u.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var c in _memoire.Load())
        {
            if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var url)) continue;
            if (!(c.Manuelle && manuelles.Contains(c.Url)) && !(!c.Manuelle && options.Decouverte)) continue;
            _imprimantes[c.Cle] = new Suivi(c.Cle) { Url = url, Manuelle = c.Manuelle, Nom = c.Nom, Modele = c.Modele, Consommables = c.Consommables };
            _cleParUrl[c.Url] = c.Cle;
        }
    }

    /// <summary>Un relevé de toutes les imprimantes (les tests l'appellent sans attendre la minuterie).</summary>
    internal async Task CycleAsync(CancellationToken ct)
    {
        await _serie.WaitAsync(ct);
        try
        {
            lock (_verrou) Charger();
            await DecouvrirAsync(ct);

            var lues = new HashSet<string>(StringComparer.Ordinal);
            var ratees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (url, manuelle) in Cibles())
            {
                string? connue;
                lock (_verrou) connue = _cleParUrl.GetValueOrDefault(url.ToString());
                if (connue is not null && lues.Contains(connue)) continue;   // déjà lue par une autre adresse

                var cle = await ReleverAsync(url, manuelle, lues, ct);
                if (cle is not null) lues.Add(cle);
                else if (connue is not null) ratees.Add(connue);
            }

            lock (_verrou)
            {
                foreach (var cle in ratees.Except(lues))
                {
                    var s = _imprimantes[cle];
                    if (++s.Echecs >= EchecsAvantIndisponible && s.Disponible)
                    {
                        s.Disponible = false;
                        Eteinte(logger, s.Nom ?? cle, s.Echecs);
                    }
                }
            }

            await PublierAsync(ct);
        }
        finally
        {
            _serie.Release();
        }
    }

    private async Task DecouvrirAsync(CancellationToken ct)
    {
        if (!options.Decouverte || decouverte is null) return;
        var maintenant = clock.GetUtcNow();
        if (_derniereDecouverte is { } d && maintenant - d < options.PeriodeDecouverte) return;
        _derniereDecouverte = maintenant;

        IReadOnlyList<ServiceIpp> trouvees;
        try
        {
            trouvees = await decouverte.ChercherAsync(options.EcouteDecouverte, ct);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            DecouverteImpossible(logger, ex.Message);
            return;
        }

        lock (_verrou)
        {
            foreach (var s in trouvees.Where(s => _decouvertes.All(d => d.Url != s.Url)))
            {
                _decouvertes.Add(s);
                Decouverte(logger, s.Instance, s.Url);
            }
        }
    }

    /// <summary>Les adresses à relever, dans l'ordre de préférence : saisies, déjà connues, découvertes.</summary>
    private List<(Uri Url, bool Manuelle)> Cibles()
    {
        var cibles = new List<(Uri, bool)>();
        var vues = new HashSet<string>(StringComparer.Ordinal);
        lock (_verrou)
        {
            foreach (var u in options.Manuelles)
                if (vues.Add(u.ToString())) cibles.Add((u, true));
            foreach (var s in _imprimantes.Values.OrderBy(s => s.Cle, StringComparer.Ordinal))
                if (vues.Add(s.Url.ToString())) cibles.Add((s.Url, s.Manuelle));
            foreach (var s in _decouvertes)
                if (vues.Add(s.Url.ToString())) cibles.Add((s.Url, false));
        }

        return cibles;
    }

    /// <summary>Relève une adresse ; rend la clé de l'imprimante, ou null si elle n'a pas répondu.</summary>
    private async Task<string?> ReleverAsync(Uri url, bool manuelle, IReadOnlySet<string> dejaLues, CancellationToken ct)
    {
        IppResponse reponse;
        try
        {
            reponse = await ipp.GetPrinterAttributesAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IppFormatException or IppStatusException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested) throw;
            bool premiere;
            lock (_verrou) premiere = _injoignablesSignalees.Add(url.ToString());
            if (premiere) Injoignable(logger, url, ex.Message);
            return null;
        }

        var releve = ReleveImprimante.Lire(reponse, options.SeuilBas);
        var diagnostic = Diagnostic.Evaluer(releve);
        var cle = releve.Uuid ?? CleAdresse(url);
        if (dejaLues.Contains(cle))
        {
            // Sa deuxième annonce (_ipps après _ipp) : retenue pour ne plus l'interroger à chaque relevé.
            lock (_verrou) _cleParUrl[url.ToString()] = cle;
            return cle;
        }

        var memoriser = false;
        lock (_verrou)
        {
            _injoignablesSignalees.Remove(url.ToString());
            if (!_imprimantes.TryGetValue(cle, out var s))
            {
                _imprimantes[cle] = s = new Suivi(cle) { Url = url, Manuelle = manuelle };
                Nouvelle(logger, releve.Nom ?? cle, releve.Modele ?? "?", url);
                memoriser = true;
            }

            var consommables = releve.Consommables.Select(c => new ConsommableConnu(c.Id, c.Nom)).ToList();
            memoriser |= s.Url != url || s.Nom != releve.Nom || s.Modele != releve.Modele || (manuelle && !s.Manuelle)
                         || !s.Consommables.SequenceEqual(consommables);
            if (s.Diagnostic is { } avant && avant.Probleme != diagnostic.Probleme)
            {
                if (diagnostic.Probleme) Probleme(logger, releve.Nom ?? cle, string.Join(", ", diagnostic.Alertes));
                else ProblemeResolu(logger, releve.Nom ?? cle);
            }
            else if (s.Diagnostic is null && diagnostic.Probleme) Probleme(logger, releve.Nom ?? cle, string.Join(", ", diagnostic.Alertes));

            s.Url = url;
            s.Manuelle |= manuelle;
            s.Nom = releve.Nom;
            s.Modele = releve.Modele;
            s.Consommables = consommables;
            s.Releve = releve;
            s.Diagnostic = diagnostic;
            s.DernierReleve = clock.GetUtcNow();
            s.Echecs = 0;
            s.Disponible = true;
            _cleParUrl[url.ToString()] = cle;
            if (memoriser) _memoire.Save(_imprimantes.Values.Select(i => i.Connue()));
        }

        return cle;
    }

    /// <summary>Sans UUID (imprimante ancienne) : l'adresse, réduite à des caractères sûrs.</summary>
    public static string CleAdresse(Uri url) =>
        string.Concat($"{url.Host}-{url.Port}{url.AbsolutePath}".Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')).Trim('-');

    private async Task PublierAsync(CancellationToken ct)
    {
        Declare? declaration;
        List<EntityState> etats;
        List<Availability> disponibilites;
        lock (_verrou)
        {
            var d = Declaration();
            var json = JsonSerializer.Serialize(d);
            declaration = json == _declaration ? null : d;
            _declaration = json;

            etats = [.. Etats().Where(e => _envoyes.GetValueOrDefault(e.EntityUniqueId) != e.Value.GetRawText())];
            foreach (var e in etats) _envoyes[e.EntityUniqueId] = e.Value.GetRawText();

            disponibilites = [.. _imprimantes.Values
                .Where(s => !_disponibilitesEnvoyees.TryGetValue(s.Cle, out var envoyee) || envoyee != s.Disponible)
                .Select(s => new Availability { DeviceUniqueId = s.Cle, Online = s.Disponible })];
            foreach (var a in disponibilites) _disponibilitesEnvoyees[a.DeviceUniqueId!] = a.Online;
        }

        if (Coeur is not { } coeur) return;
        // Déclarer d'abord : une imprimante apparue doit exister au registre avant que son état n'arrive.
        if (declaration is not null) await coeur.SendAsync(declaration, ct);
        if (etats.Count > 0) await coeur.SendAsync(new StateBatch { States = etats }, ct);
        foreach (var a in disponibilites) await coeur.SendAsync(a, ct);
    }

    /// <summary>Sous le verrou. Seules les imprimantes relevées ont des états : une imprimante connue mais éteinte n'en a pas.</summary>
    private List<EntityState> Etats()
    {
        var etats = new List<EntityState>();
        foreach (var s in _imprimantes.Values.OrderBy(s => s.Cle, StringComparer.Ordinal))
        {
            if (s is not { Releve: { } r, Diagnostic: { } d, DernierReleve: { } quand }) continue;
            etats.Add(Noter(s.Cle, Objet(s, r, d, quand), quand));
            etats.Add(Noter(EtatId(s.Cle), Diagnostic.LibelleEtat(r.Etat), quand));
            etats.Add(Noter(ProblemeId(s.Cle), d.Probleme, quand));
            foreach (var c in r.Consommables.Where(c => c.Niveau is not null))
                etats.Add(Noter(ConsommableId(s.Cle, c.Id), c.Niveau!.Value, quand));
        }

        return etats;
    }

    /// <summary>La valeur que dessine la carte « Imprimante ».</summary>
    private static JsonObject Objet(Suivi s, ReleveImprimante r, Diagnostic d, DateTimeOffset quand)
    {
        var plusBas = r.Consommables.Where(c => c.Niveau is not null).MinBy(c => c.Niveau);
        return new JsonObject
        {
            ["name"] = r.Nom, ["model"] = r.Modele, ["url"] = s.Url.ToString(),
            ["state"] = r.Etat switch
            {
                EtatImprimante.Prete => "idle",
                EtatImprimante.EnImpression => "processing",
                EtatImprimante.Arretee => "stopped",
                _ => "unknown",
            },
            ["state_label"] = Diagnostic.LibelleEtat(r.Etat),
            ["message"] = r.Message,
            ["reasons"] = new JsonArray([.. r.Motifs.Select(m => (JsonNode?)m)]),
            ["alerts"] = new JsonArray([.. d.Alertes.Select(a => (JsonNode?)a)]),
            ["problem"] = d.Probleme,
            ["markers"] = new JsonArray([.. r.Consommables.Select(c => (JsonNode?)new JsonObject
            {
                ["id"] = c.Id, ["name"] = c.Nom, ["level"] = c.Niveau, ["some_remaining"] = c.Restant,
                ["low"] = c.Bas, ["low_threshold"] = c.SeuilBas, ["type"] = c.Type, ["color"] = c.Couleur,
            })]),
            ["lowest"] = plusBas is null ? null : new JsonObject { ["name"] = plusBas.Nom, ["level"] = plusBas.Niveau },
            ["last_read"] = quand.ToString("O"),
        };
    }

    private EntityState Noter(string id, object valeur, DateTimeOffset quand)
    {
        var json = valeur is JsonNode n ? JsonSerializer.SerializeToElement(n) : JsonSerializer.SerializeToElement(valeur);
        _valeurs[id] = json;
        return new EntityState { EntityUniqueId = id, Value = json, Timestamp = quand };
    }

    /// <summary>Sous le verrou.</summary>
    private Declare Declaration() => new()
    {
        Devices = [.. _imprimantes.Values.OrderBy(s => s.Cle, StringComparer.Ordinal).Select(s => new DeviceDescriptor
        {
            UniqueId = s.Cle, Name = s.Nom ?? s.Modele ?? "Imprimante", Manufacturer = Fabricant(s.Modele), Model = s.Modele,
            Entities =
            [
                new EntityDescriptor { UniqueId = s.Cle, Kind = EntityKind.Sensor, Name = "Imprimante", DeviceClass = DeviceClasses.Printer, Commands = [Refresh] },
                new EntityDescriptor { UniqueId = EtatId(s.Cle), Kind = EntityKind.Sensor, Name = "État", DeviceClass = DeviceClasses.Enum },
                new EntityDescriptor { UniqueId = ProblemeId(s.Cle), Kind = EntityKind.BinarySensor, Name = "Problème", DeviceClass = DeviceClasses.Problem },
                .. s.Consommables.Select(c => new EntityDescriptor
                {
                    UniqueId = ConsommableId(s.Cle, c.Id), Kind = EntityKind.Sensor, Name = c.Nom, Unit = Units.Percent, StateClass = StateClass.Measurement,
                }),
            ],
        })],
    };

    /// <summary>« Samsung M2020 Series » → « Samsung » : le premier mot du modèle, que toutes les marques y mettent.</summary>
    private static string? Fabricant(string? modele) => modele?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    public IReadOnlyList<Message> Snapshot()
    {
        var messages = new List<Message>();
        lock (_verrou)
        {
            Charger();
            messages.Add(Declaration());
            if (_valeurs.Count > 0)
                messages.Add(new StateBatch { States = [.. _valeurs.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = kv.Value })] });
            foreach (var s in _imprimantes.Values)
            {
                messages.Add(new Availability { DeviceUniqueId = s.Cle, Online = s.Disponible });
                _disponibilitesEnvoyees[s.Cle] = s.Disponible;
            }
        }

        return messages;
    }

    /// <summary><c>refresh</c> (« Vérifier ») : un relevé de cette imprimante, tout de suite.</summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        Suivi? s;
        lock (_verrou) s = _imprimantes.GetValueOrDefault(request.EntityUniqueId);
        if (s is null) return Echec(request, $"« {request.EntityUniqueId} » : imprimante inconnue");
        if (request.Command != Refresh) return Echec(request, $"commande « {request.Command} » inconnue");

        await _serie.WaitAsync(ct);
        try
        {
            var cle = await ReleverAsync(s.Url, s.Manuelle, new HashSet<string>(), ct);
            if (cle is null)
            {
                lock (_verrou) _injoignablesSignalees.Remove(s.Url.ToString());   // le prochain relevé raté le redira
                return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Unreachable, Detail = "l'imprimante ne répond pas (éteinte ?)" };
            }

            await PublierAsync(ct);
            return new CommandResult { Id = request.Id, Outcome = CommandOutcome.Accepted };
        }
        finally
        {
            _serie.Release();
        }
    }

    private static CommandResult Echec(CommandRequest r, string detail) => new() { Id = r.Id, Outcome = CommandOutcome.Failed, Detail = detail };

    [LoggerMessage(Level = LogLevel.Information, Message = "Imprimante annoncée sur le réseau : « {Nom} » ({Url}).")]
    private static partial void Decouverte(ILogger logger, string nom, Uri url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Découverte mDNS impossible : {Motif}")]
    private static partial void DecouverteImpossible(ILogger logger, string motif);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nouvelle imprimante : « {Nom} », {Modele}, à {Url}.")]
    private static partial void Nouvelle(ILogger logger, string nom, string modele, Uri url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Url} ne répond pas : {Motif}")]
    private static partial void Injoignable(ILogger logger, Uri url, string motif);

    [LoggerMessage(Level = LogLevel.Information, Message = "« {Nom} » ne répond plus depuis {Echecs} relevés (éteinte ?) : indisponible.")]
    private static partial void Eteinte(ILogger logger, string nom, int echecs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "« {Nom} » signale un problème : {Alertes}.")]
    private static partial void Probleme(ILogger logger, string nom, string alertes);

    [LoggerMessage(Level = LogLevel.Information, Message = "« {Nom} » : problème résolu.")]
    private static partial void ProblemeResolu(ILogger logger, string nom);
}
