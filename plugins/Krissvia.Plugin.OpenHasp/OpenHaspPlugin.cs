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

    /// <summary>Effacer la platine et lui renvoyer toutes ses pages et valeurs, comme à son démarrage.</summary>
    public const string Repaint = "repaint";

    /// <summary>Redémarrer l'écran ; il se repeint de lui-même à son retour en ligne.</summary>
    public const string Reboot = "reboot";

    public static string EcranId(string noeud) => $"{noeud}:ecran";
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
    private readonly ConcurrentDictionary<string, Clavier> _claviers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _peintes = new(StringComparer.Ordinal);
    private string? _etatAlarme;
    private readonly SemaphoreSlim _envoi = new(1, 1);
    private volatile bool _brokerConnecte;
    private DateTimeOffset _connecteLe;

    /// <summary>
    /// Les messages retenus arrivent dans l'instant qui suit l'abonnement : un « online » reçu plus tard a été publié en
    /// direct par la platine, donc elle vient de (re)démarrer.
    /// </summary>
    public static readonly TimeSpan FenetreRetenus = TimeSpan.FromSeconds(5);
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
        // Dit une fois au démarrage, pas à chaque peinture : une ligne écartée l'est jusqu'à ce qu'on corrige les réglages.
        foreach (var platine in _platines.Values.Where(p => p.APagePerso))
            foreach (var raison in PagePerso.Lire(platine.PagePerso).Ecartees) LignePersoEcartee(_logger, platine.Noeud, raison);
    }

    /// <summary>
    /// Par où partent les ordres : le cœur, sauf dans les tests, qui regardent ce qui part — en particulier le code du
    /// digicode, qui ne doit voyager que là.
    /// </summary>
    internal Func<string, string, JsonElement?, Task<EntityCommandResponse>>? Commander { get; set; }

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

    internal void EtatRecu(EntityStateUpdate etat)
    {
        _etats[etat.EntityId] = etat;
        var alerte = false;
        if (etat.EntityId == _reglages.Alarme.Entite)
        {
            var apres = EtatAlarme.Lire(etat)?.Etat;
            alerte = Ecran.AppelleLeClavier(_etatAlarme, apres);
            _etatAlarme = apres;
        }

        // Hors du fil de réception du cœur ; les envois sont de toute façon sérialisés.
        _ = Task.Run(async () =>
        {
            if (alerte) await AppelerLeClavierAsync();
            await RafraichirAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Quelqu'un entre, ou l'alarme sonne : chaque platine en ligne qui porte le digicode le montre, écran rallumé.
    /// Celui qui rentre chez lui n'a pas à chercher la page pendant que le décompte tourne.
    /// </summary>
    internal async Task AppelerLeClavierAsync()
    {
        foreach (var platine in _platines.Values.Where(p => p.ADigicode(_reglages) && _enLigne.TryGetValue(p.Noeud, out var l) && l))
        {
            var noeud = platine.Noeud;
            await SousVerrouAsync(async () =>
            {
                if (_enVeille.TryRemove(noeud, out _))
                {
                    _republication.Oublier(noeud, "backlight");
                    await LuminositeAsync(noeud, _luminosites.TryGetValue(noeud, out var v) ? v : Luminosite.Borner(_reglages.Affichage.LuminositeDefaut));
                }

                return await _mqtt.PublishAsync(Commande(noeud, "page"), Objets.PageAlarme.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            });
        }
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
                    Entities =
                    [
                        new EntityDescriptor { UniqueId = n, Kind = EntityKind.BinarySensor, Name = "Connexion", DeviceClass = DeviceClasses.Connectivity },
                        new EntityDescriptor { UniqueId = EcranId(n), Kind = EntityKind.Sensor, Name = "Écran", DeviceClass = DeviceClasses.DisplayPanel, Commands = [Repaint, Reboot] },
                    ],
                })],
            },
        };
        if (!_enLigne.IsEmpty)
            messages.Add(new StateBatch
            {
                States = [.. _enLigne.Select(kv => new EntityState { EntityUniqueId = kv.Key, Value = JsonSerializer.SerializeToElement(kv.Value) })
                    .Concat(_enLigne.Keys.Where(_platines.ContainsKey).Select(EtatEcran))],
            });
        messages.Add(new Availability { Online = _brokerConnecte });
        return messages;
    }

    /// <summary>
    /// <c>repaint</c> et <c>reboot</c>, sur l'entité « Écran » d'une platine. Les autres entités ne se commandent pas :
    /// une platine commande, elle n'est pas commandée.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        CommandResult Resultat(CommandOutcome o, string? detail = null) => new() { Id = request.Id, Outcome = o, Detail = detail };
        var platine = _platines.Values.FirstOrDefault(p => EcranId(p.Noeud) == request.EntityUniqueId);
        if (platine is null) return Resultat(CommandOutcome.Failed, "une platine ne se commande pas : elle commande ; seul son écran sait se repeindre ou redémarrer");
        if (!_brokerConnecte) return Resultat(CommandOutcome.Unreachable, "broker injoignable");
        if (!(_enLigne.TryGetValue(platine.Noeud, out var enLigne) && enLigne))
            return Resultat(CommandOutcome.Unreachable, "platine hors ligne : elle se repeindra d'elle-même à son retour");

        switch (request.Command)
        {
            case Repaint:
                return await GenererAsync(platine) ? Resultat(CommandOutcome.Accepted) : Resultat(CommandOutcome.Unreachable, "pages non envoyées (broker)");

            case Reboot:
                // « reboot » en charge du topic de commande : la forme documentée par openHASP, et jamais une charge vide
                // (qui, sur un topic d'attribut, serait une lecture). La platine repart, publie « online », et se repeint.
                var ok = await _mqtt.PublishAsync($"hasp/{platine.Noeud}/command", "reboot", ct);
                if (ok) Redemarrage(_logger, platine.Noeud);
                return ok ? Resultat(CommandOutcome.Accepted) : Resultat(CommandOutcome.Unreachable, "broker injoignable");

            default:
                return Resultat(CommandOutcome.Failed, $"commande « {request.Command} » inconnue");
        }
    }

    /// <summary>La valeur de l'entité « Écran » : ce que dessine sa carte.</summary>
    private EntityState EtatEcran(string noeud) => new()
    {
        EntityUniqueId = EcranId(noeud),
        Value = JsonSerializer.SerializeToElement(new
        {
            node = noeud,
            online = _enLigne.TryGetValue(noeud, out var l) && l,
            pages = _platines.TryGetValue(noeud, out var p) ? MiseEnPage.PagesPresentes(p, _reglages).Count : 0,
            last_paint = _peintes.TryGetValue(noeud, out var quand) ? quand : (DateTimeOffset?)null,
            asleep = _enVeille.ContainsKey(noeud),
        }),
    };

    private Task PublierEcranAsync(string noeud) =>
        Coeur?.SendAsync(new StateBatch { States = [EtatEcran(noeud)] }) ?? Task.CompletedTask;

    // ── Broker ───────────────────────────────────────────────────────────────

    public async Task ConnecteAsync()
    {
        _connecteLe = _horloge.GetUtcNow();
        _brokerConnecte = true;
        if (Coeur is { } coeur)
        {
            await coeur.ReportLinkAsync("mqtt", true);   // pastille MQTT du cœur
            await coeur.SendAsync(new Availability { Online = true });
        }
    }

    public async Task DeconnecteAsync()
    {
        _brokerConnecte = false;
        if (Coeur is { } coeur)
        {
            await coeur.ReportLinkAsync("mqtt", false);
            await coeur.SendAsync(new Availability { Online = false });
        }
    }

    public async Task ConnexionImpossibleAsync(string motif)
    {
        if (Coeur is { } coeur) await coeur.ReportLinkAsync("mqtt", false, motif);
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
                await coeur.SendAsync(new StateBatch { States = [new EntityState { EntityUniqueId = noeud, Value = JsonSerializer.SerializeToElement(enLigne) }, EtatEcran(noeud)] });

            if (ARepeindre(avant, enLigne, _horloge.GetUtcNow() - _connecteLe)) _ = Task.Run(() => GenererAsync(platine));
            else if (!enLigne && avant == true) PlatineHorsLigne(_logger, noeud);
            return;
        }

        if (morceaux.Length == 4 && morceaux[2] == "state"
            && Evenements.Interpreter(platine, _reglages, morceaux[3], charge, _etats) is { } reaction)
            _ = Task.Run(() => ReagirAsync(platine, reaction));
    }

    /// <summary>
    /// Faut-il effacer la platine et lui renvoyer ses pages ? Une platine qui (re)démarre affiche le <c>pages.jsonl</c> de
    /// sa mémoire, plus ce que Krissvia lui avait envoyé.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Premier « online » vu par le plugin : oui, on ignore ce qu'elle affiche.</item>
    /// <item>Retour après un « offline » : oui.</item>
    /// <item>« online » alors qu'on la croyait déjà en ligne : seulement s'il arrive après <see cref="FenetreRetenus"/>.
    /// Dans la fenêtre, c'est le message retenu que le broker rejoue après une reconnexion — rien n'a changé. Après, c'est
    /// la platine qui le publie : débranchée et rebranchée en quelques secondes, elle s'est reconnectée avant que le
    /// broker ait vu sa déconnexion, et aucun « offline » n'est jamais venu (vu le 23/10/2026 : la platine gardait son
    /// ancien écran).</item>
    /// </list>
    /// </remarks>
    internal static bool ARepeindre(bool? avant, bool enLigne, TimeSpan depuisConnexion) =>
        enLigne && (avant != true || depuisConnexion >= FenetreRetenus);

    // ── Platines ─────────────────────────────────────────────────────────────

    /// <summary>Efface la platine, pose les pages générées, puis la peint. Faux si le broker a lâché en route.</summary>
    internal async Task<bool> GenererAsync(Platine platine)
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
                return false;
            }

            PagesGenerees(_logger, noeud, lignes.Count);
            await PeindreAsync(platine);
            _peintes[noeud] = _horloge.GetUtcNow();
        }
        finally
        {
            _envoi.Release();
        }

        await PublierEcranAsync(noeud);
        return true;
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
        var clavier = _claviers.TryGetValue(noeud, out var c) ? c.Etat() : null;
        foreach (var (attribut, valeur) in Ecran.Attributs(platine, _reglages, _etats, _horloge.GetLocalNow().DateTime, clavier))
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
        valeur = Charge(attribut, valeur);
        if (!_republication.AEnvoyer(noeud, attribut, valeur)) return true;
        if (!await _mqtt.PublishAsync(Commande(noeud, attribut), valeur, CancellationToken.None)) return false;
        _republication.Noter(noeud, attribut, valeur);
        return true;
    }

    /// <summary>
    /// Pour openHASP, une commande à charge VIDE est une lecture (<c>update = length &gt; 0</c> dans son répartiteur) :
    /// <c>p4b3.text</c> vide ne vide rien, la platine renvoie l'ancien texte — le dernier « * » du code restait affiché
    /// après le retour arrière. Un texte vide part donc en espace, invisible et bien une écriture.
    /// </summary>
    internal static string Charge(string attribut, string valeur) =>
        valeur.Length == 0 && attribut.EndsWith(".text", StringComparison.Ordinal) ? " " : valeur;

    internal async Task ReagirAsync(Platine platine, Reaction reaction)
    {
        var noeud = platine.Noeud;
        switch (reaction)
        {
            case Ordre ordre:
                var resultat = await CommanderAsync(ordre.Entite, ordre.Commande, ordre.Args);
                if (resultat.Outcome == CommandOutcome.Accepted) OrdreAccepte(_logger, noeud, ordre.Entite, ordre.Commande);
                else OrdreRefuse(_logger, noeud, ordre.Entite, ordre.Commande, resultat.Outcome, resultat.Detail ?? "");
                // L'écran suivra l'état que l'entité republie : jamais d'affichage d'un ordre qui n'a pas abouti.
                break;

            case ToucheClavier touche:
                Clavier(noeud).Appuyer(touche.Touche);
                await SousVerrouAsync(async () => { await PeindreAsync(platine); return true; });
                break;

            case CommandeAlarme alarme when _reglages.Alarme.Entite is { Length: > 0 } entite:
                var clavier = Clavier(noeud);
                if (clavier.Prendre() is not { } code)
                {
                    // Pas d'ordre sans code : un code vide serait refusé ET compterait pour le blocage.
                    clavier.Signaler("Taper le code d'abord");
                }
                else
                {
                    var reponse = await CommanderAsync(entite, alarme.Commande, JsonSerializer.SerializeToElement(new { code }));
                    // Jamais le code dans le journal : la commande et l'entité suffisent.
                    if (reponse.Outcome == CommandOutcome.Accepted) OrdreAccepte(_logger, noeud, entite, alarme.Commande);
                    else OrdreRefuse(_logger, noeud, entite, alarme.Commande, reponse.Outcome, reponse.Detail ?? "");
                    clavier.Signaler(reponse.Outcome switch
                    {
                        CommandOutcome.Accepted => alarme.Commande == StandardCommands.Arm ? "Armement en cours" : "Code accepté",
                        CommandOutcome.Unreachable => "Krissvia injoignable",
                        _ => Majuscule(reponse.Detail) ?? "Refusé",
                    });
                }

                await SousVerrouAsync(async () => { await PeindreAsync(platine); return true; });
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

    private Clavier Clavier(string noeud) => _claviers.GetOrAdd(noeud, _ => new Clavier(_horloge));

    private Task<EntityCommandResponse> CommanderAsync(string entite, string commande, JsonElement? args) =>
        Commander?.Invoke(entite, commande, args)
        ?? Coeur?.CommandEntityAsync(entite, commande, args)
        ?? Task.FromResult(new EntityCommandResponse { Id = "", Outcome = CommandOutcome.Unreachable, Detail = "pas de cœur" });

    private static string? Majuscule(string? texte) => texte is { Length: > 0 } t ? char.ToUpperInvariant(t[0]) + t[1..] : null;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platine {Noeud}, page perso : {Raison} — ligne écartée.")]
    private static partial void LignePersoEcartee(ILogger logger, string noeud, string raison);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platine {Noeud} : redémarrage demandé.")]
    private static partial void Redemarrage(ILogger logger, string noeud);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platine {Noeud} : écran éteint (veille).")]
    private static partial void EcranEteint(ILogger logger, string noeud);
}
