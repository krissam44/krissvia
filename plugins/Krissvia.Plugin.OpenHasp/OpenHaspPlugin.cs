using System.Collections.Concurrent;
using System.Text.Json;
using Krissvia.Contracts;
using Krissvia.Sdk;
using Krissvia.Sdk.Mqtt;
using Microsoft.Extensions.Logging;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>
/// Les platines openHASP : génère leurs pages quand elles se connectent, y affiche les entités choisies dans les
/// réglages, et relaie leurs appuis au cœur (contrat 0.3). Le plugin ne décide de rien : un appui devient un ordre
/// sur une entité, exactement comme un clic sur le tableau de bord.
/// </summary>
/// <remarks>
/// Chaque platine est aussi un appareil du cœur, avec une entité « Connexion » (son LWT) : une platine débranchée se
/// voit sur le tableau de bord au lieu de se taire.
/// </remarks>
internal sealed partial class OpenHaspPlugin : ICorePlugin, IMqttHandler
{
    public const string Abonnement = "hasp/#";
    public static readonly TimeSpan Periode = TimeSpan.FromSeconds(30);

    private readonly Reglages _reglages;
    private readonly IMqttLink _mqtt;
    private readonly TimeProvider _horloge;
    private readonly ILogger _logger;
    private readonly Republication _republication;
    private readonly IReadOnlyDictionary<string, Platine> _platines;
    private readonly ConcurrentDictionary<string, EntityStateUpdate> _etats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _enLigne = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _luminosites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _enVeille = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _envoi = new(1, 1);
    private volatile bool _brokerConnecte;
    private CoreClient? _coeur;

    public OpenHaspPlugin(Reglages reglages, IMqttLink mqtt, TimeProvider horloge, ILogger<OpenHaspPlugin> logger)
    {
        _reglages = reglages;
        _mqtt = mqtt;
        _horloge = horloge;
        _logger = logger;
        _republication = new Republication(horloge);
        _platines = reglages.Platines.Where(p => p.Noeud.Length > 0)
            .GroupBy(p => p.Noeud, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var doublon in reglages.Platines.GroupBy(p => p.Noeud).Where(g => g.Count() > 1)) NoeudEnDouble(_logger, doublon.Key);
    }

    /// <summary>Renseigné au démarrage : le client a besoin du plugin, et le plugin du client pour commander.</summary>
    public CoreClient? Coeur
    {
        get => _coeur;
        set
        {
            if (_coeur is { } ancien) ancien.EntityStateReceived -= EtatRecu;
            _coeur = value;
            if (value is not null) value.EntityStateReceived += EtatRecu;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Coeur is { } coeur) await coeur.SetSubscriptionsAsync(_reglages.Entites(), ct);
        await Task.WhenAll(_mqtt.RunAsync(Abonnement, this, ct), BoucleAsync(ct));
    }

    /// <summary>
    /// Toutes les 30 s : la date et la prochaine bascule changent sans qu'aucune entité ne bouge, et le filtre renvoie
    /// ce qui n'a pas été répété depuis deux minutes.
    /// </summary>
    private async Task BoucleAsync(CancellationToken ct)
    {
        using var minuterie = new PeriodicTimer(Periode, _horloge);
        try
        {
            while (await minuterie.WaitForNextTickAsync(ct)) await RafraichirAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // ── Cœur ─────────────────────────────────────────────────────────────────

    private void EtatRecu(EntityStateUpdate etat)
    {
        _etats[etat.EntityId] = etat;
        // Hors du fil de réception du cœur ; les envois sont de toute façon sérialisés.
        _ = Task.Run(() => RafraichirAsync(CancellationToken.None));
    }

    public IReadOnlyList<Message> Snapshot()
    {
        var messages = new List<Message>
        {
            new Declare
            {
                Devices = [.. _platines.Keys.Select(n => new DeviceDescriptor
                {
                    UniqueId = n,
                    Name = $"Platine {n}",
                    Manufacturer = "openHASP",
                    Model = "Guition 480×480",
                    Entities = [new EntityDescriptor { UniqueId = n, Kind = EntityKind.BinarySensor, Name = "Connexion", DeviceClass = DeviceClasses.Connectivity }],
                })],
            },
        };
        if (!_enLigne.IsEmpty)
            messages.Add(new StateBatch { States = [.. _enLigne.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = JsonSerializer.SerializeToElement(kv.Value) })] });
        messages.Add(new Availability { Online = _brokerConnecte });
        return messages;
    }

    public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct) =>
        Task.FromResult(new CommandResult { Id = request.Id, Outcome = CommandOutcome.Failed, Detail = "une platine ne se commande pas : elle commande" });

    // ── Broker ───────────────────────────────────────────────────────────────

    public async Task ConnecteAsync()
    {
        _brokerConnecte = true;
        if (Coeur is { } coeur) await coeur.SendAsync(new Availability { Online = true });
    }

    public async Task DeconnecteAsync()
    {
        _brokerConnecte = false;
        if (Coeur is { } coeur) await coeur.SendAsync(new Availability { Online = false });
    }

    /// <summary>
    /// Ne publie jamais sur ce fil : la réception doit continuer pendant qu'une publication attend son acquittement.
    /// Tout ce qui envoie part donc sur une tâche à part.
    /// </summary>
    public async Task MessageAsync(string topic, string charge)
    {
        var morceaux = topic.Split('/');
        if (morceaux.Length < 3 || morceaux[0] != "hasp" || !_platines.TryGetValue(morceaux[1], out var platine)) return;
        var noeud = platine.Noeud;

        if (morceaux.Length == 3 && morceaux[2] == "LWT")
        {
            var enLigne = charge.Trim().Equals("online", StringComparison.OrdinalIgnoreCase);
            var avant = _enLigne.TryGetValue(noeud, out var e) ? e : (bool?)null;
            _enLigne[noeud] = enLigne;
            if (avant != enLigne && Coeur is { } coeur)
                await coeur.SendAsync(new StateBatch { States = [new EntityState { EntityUniqueId = noeud, Value = JsonSerializer.SerializeToElement(enLigne) }] });

            // Une platine qui (re)démarre est repartie de rien ; au démarrage du plugin, on ignore ce qu'elle affiche.
            // Le même « online » relu après une reconnexion au broker (message retenu) ne repeint pas : rien n'a changé.
            if (enLigne && avant != true) _ = Task.Run(() => GenererAsync(platine));
            else if (!enLigne && avant == true) PlatineHorsLigne(_logger, noeud);
            return;
        }

        if (morceaux.Length == 4 && morceaux[2] == "state"
            && Evenements.Interpreter(platine, morceaux[3], charge, _etats) is { } reaction)
            _ = Task.Run(() => ReagirAsync(platine, reaction));
    }

    // ── Platines ─────────────────────────────────────────────────────────────

    /// <summary>Efface la platine, pose les pages générées, puis la peint.</summary>
    internal async Task GenererAsync(Platine platine)
    {
        var noeud = platine.Noeud;
        var lignes = MiseEnPage.Generer(platine, _reglages);
        await _envoi.WaitAsync();
        try
        {
            _republication.OublierNoeud(noeud);
            var ok = await _mqtt.PublishAsync(Commande(noeud, "clearpage"), "all", CancellationToken.None);
            foreach (var ligne in lignes)
            {
                if (!ok) break;
                ok = await _mqtt.PublishAsync(Commande(noeud, "jsonl"), ligne, CancellationToken.None);
            }

            if (ok) ok = await _mqtt.PublishAsync(Commande(noeud, "page"), MiseEnPage.PagesPresentes(platine, _reglages)[0].ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            if (!ok)
            {
                // Retentée au prochain « online » : la platine n'a peut-être reçu qu'une partie de ses pages.
                _enLigne.TryRemove(noeud, out _);
                GenerationRatee(_logger, noeud);
                return;
            }

            PagesGenerees(_logger, noeud, lignes.Count);
            await PeindreAsync(platine);
        }
        finally
        {
            _envoi.Release();
        }
    }

    internal async Task RafraichirAsync(CancellationToken ct)
    {
        await _envoi.WaitAsync(ct);
        try
        {
            foreach (var platine in _platines.Values)
                if (_enLigne.TryGetValue(platine.Noeud, out var enLigne) && enLigne) await PeindreAsync(platine);
        }
        finally
        {
            _envoi.Release();
        }
    }

    /// <summary>Appelé sous <see cref="_envoi"/>. S'arrête au premier envoi raté : le broker n'est plus là.</summary>
    private async Task PeindreAsync(Platine platine)
    {
        var noeud = platine.Noeud;
        foreach (var (attribut, valeur) in Ecran.Attributs(platine, _reglages, _etats, _horloge.GetLocalNow().DateTime))
            if (!await EnvoyerAsync(noeud, attribut, valeur)) return;

        // Une platine en veille est noire exprès : la republication périodique la rallumerait dix secondes après.
        // Et tant que personne n'a touché le curseur, on n'impose rien à une platine déjà réglée à son goût.
        if (!_enVeille.ContainsKey(noeud) && _luminosites.TryGetValue(noeud, out var luminosite))
            await LuminositeAsync(noeud, luminosite);
    }

    private async Task<bool> LuminositeAsync(string noeud, int valeur) =>
        await EnvoyerAsync(noeud, "backlight", JsonSerializer.Serialize(new { state = "on", brightness = valeur }))
        && await EnvoyerAsync(noeud, Objets.Attribut(Objets.PageHorloge, Objets.Pourcentage, "text"), Luminosite.Libelle(valeur))
        // Le curseur lui-même : une écriture programmée n'émet pas d'événement, pas de boucle à craindre.
        && await EnvoyerAsync(noeud, Objets.Attribut(Objets.PageHorloge, Objets.Curseur, "val"), valeur.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task<bool> EnvoyerAsync(string noeud, string attribut, string valeur)
    {
        if (!_republication.AEnvoyer(noeud, attribut, valeur)) return true;
        if (!await _mqtt.PublishAsync(Commande(noeud, attribut), valeur, CancellationToken.None)) return false;
        _republication.Noter(noeud, attribut, valeur);
        return true;
    }

    internal async Task ReagirAsync(Platine platine, Reaction reaction)
    {
        var noeud = platine.Noeud;
        switch (reaction)
        {
            case Ordre ordre:
                if (Coeur is not { } coeur) return;
                var resultat = await coeur.CommandEntityAsync(ordre.Entite, ordre.Commande, ordre.Args);
                if (resultat.Outcome == CommandOutcome.Accepted) OrdreAccepte(_logger, noeud, ordre.Entite, ordre.Commande);
                else OrdreRefuse(_logger, noeud, ordre.Entite, ordre.Commande, resultat.Outcome, resultat.Detail ?? "");
                // L'écran suivra l'état que l'entité republie : jamais d'affichage d'un ordre qui n'a pas abouti.
                break;

            case ChangerLuminosite luminosite:
                _luminosites[noeud] = luminosite.Valeur;
                await SousVerrouAsync(() => LuminositeAsync(noeud, luminosite.Valeur));
                break;

            case Veille { Etat: "short" or "long" }:
                if (!_reglages.Affichage.EteindreEnVeille || !_enVeille.TryAdd(noeud, 0)) return;
                await SousVerrouAsync(async () =>
                {
                    _republication.Oublier(noeud, "backlight");
                    var ok = await _mqtt.PublishAsync(Commande(noeud, "backlight"), JsonSerializer.Serialize(new { state = "off" }), CancellationToken.None);
                    if (!ok) _enVeille.TryRemove(noeud, out _);
                    return ok;
                });
                EcranEteint(_logger, noeud);
                break;

            case Veille { Etat: "off" }:
                if (!_enVeille.TryRemove(noeud, out _)) return;
                // Jamais « rien » ici : c'est cette valeur qui rallume un écran que le plugin vient d'éteindre.
                var niveau = _luminosites.TryGetValue(noeud, out var v) ? v : Luminosite.Borner(_reglages.Affichage.LuminositeDefaut);
                await SousVerrouAsync(() =>
                {
                    _republication.Oublier(noeud, "backlight");
                    return LuminositeAsync(noeud, niveau);
                });
                break;
        }
    }

    private async Task SousVerrouAsync(Func<Task<bool>> action)
    {
        await _envoi.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _envoi.Release();
        }
    }

    private static string Commande(string noeud, string attribut) => $"hasp/{noeud}/command/{attribut}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Platine {Noeud} : pages générées ({Objets} objets).")]
    private static partial void PagesGenerees(ILogger logger, string noeud, int objets);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platine {Noeud} : pages non envoyées (broker injoignable) ; nouvel essai à sa prochaine connexion.")]
    private static partial void GenerationRatee(ILogger logger, string noeud);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platine {Noeud} hors ligne.")]
    private static partial void PlatineHorsLigne(ILogger logger, string noeud);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nœud « {Noeud} » déclaré deux fois dans les réglages : seule la première platine compte.")]
    private static partial void NoeudEnDouble(ILogger logger, string noeud);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platine {Noeud} : {Commande} sur {Entite}.")]
    private static partial void OrdreAccepte(ILogger logger, string noeud, string entite, string commande);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platine {Noeud} : {Commande} sur {Entite} — {Resultat} : {Detail}")]
    private static partial void OrdreRefuse(ILogger logger, string noeud, string entite, string commande, CommandOutcome resultat, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platine {Noeud} : écran éteint (veille).")]
    private static partial void EcranEteint(ILogger logger, string noeud);
}
