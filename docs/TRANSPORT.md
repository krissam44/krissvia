# Transport plugin ↔ cœur — décision du 15/10/2026

> Tranche le premier point ouvert de `CONTRACT-V0.2-PROPOSITION.md` (§5). Implémenté dans
> `Krissvia.PluginHost`, couvert par `Krissvia.PluginHost.Tests`.

## 1. Décision : WebSocket + JSON

Le plugin ouvre une connexion WebSocket vers le cœur, sur `ws://<cœur>/plugins`. Chaque message du
contrat est **un objet JSON dans une trame texte**, au format fixé par `ContractJson` (snake_case,
enums en chaînes, champ `type`).

| Critère | WebSocket + JSON | gRPC | MQTT |
|---|---|---|---|
| Client en Python, Node, Go… | bibliothèque standard ou quasi | génération de code, outillage protobuf | oui |
| Lisible à l'œil (`websocat`, outils du navigateur) | oui | non | oui |
| Session : savoir qu'un plugin est mort | la connexion elle-même | la connexion elle-même | LWT, et un broker de plus dans la chaîne |
| Ordres cœur → plugin avec réponse corrélée | oui | oui | à reconstruire sur des topics |
| Dépendance supplémentaire | aucune (ASP.NET Core) | Grpc.AspNetCore + HTTP/2 | un broker |

Le volume est faible (quelques messages par seconde, le Linky toutes les 2 s) : la compacité de
protobuf n'achète rien. Ce qui compte est qu'un plugin Python s'écrive en une soirée, et qu'on puisse
lire ce qui passe. MQTT est écarté parce qu'il remet un intermédiaire entre le cœur et ses plugins —
c'est précisément ce qui rendait la mort d'une intégration invisible dans DomoMud.

Le contrat reste indépendant du transport : `IMessageChannel` (trames texte) est la seule chose que
le PluginHost connaisse, ce qui permet aux tests de tourner sur une paire de canaux en mémoire.

## 2. Déroulé d'une session

```
plugin                                  cœur
  │── connexion WebSocket ───────────────▶│
  │── hello {plugin_id, contract_version, token} ─▶│   10 s maximum après l'ouverture
  │◀── welcome {contract_version, heartbeat_seconds, timeout_seconds} ──│
  │       ou rejected {reason, detail}, puis fermeture
  │── declare {devices} ─────────────────▶│
  │── state {states} (TOUS les états) ───▶│   resynchronisation, à chaque (re)connexion
  │◀─────── command / ── command_result ─▶│
  │◀── ping ──▶ (toutes les 15 s, dans chaque sens)
  │── shutdown ──────────────────────────▶│   ou simple fermeture
```

## 3. Règles

| Règle | Raison |
|---|---|
| `plugin_id` : `[a-z0-9][a-z0-9_-]{0,63}` | il devient l'`integration_id` du registre, pour toujours |
| Version : en 0.x, il faut la **même** mineure ; à partir de 1.0, même majeure et mineure ≤ celle du cœur | en 0.x tout peut casser |
| Jeton par plugin (`PluginHost:Tokens`), comparé en temps constant ; un plugin non listé est **refusé** sauf `AllowUnregisteredPlugins` | sinon n'importe quelle machine du réseau déclare des entités ou capte des ordres |
| Ping toutes les 15 s dans chaque sens, sans réponse attendue : **toute** trame reçue prouve que l'autre vit | symétrique, rien à corréler |
| 45 s de silence ⇒ session morte (constaté entre 45 et 60 s) | trois battements manqués ; un Wi-Fi qui hoquette ne coupe pas tout |
| Session morte ou fermée ⇒ toutes les entités du plugin **indisponibles** | principe 3 : les failsafes du cœur prennent le relais |
| Un nouveau `hello` du même plugin **remplace** l'ancienne session (au lieu d'un refus « déjà connecté » envisagé en v0.2) | un plugin qui redémarre revient avant que le cœur ait constaté la mort de l'ancienne connexion ; le refuser priverait ses entités de mesures jusqu'à 45 s. L'ancienne session ne rend alors **pas** les entités indisponibles |
| Type de message inconnu ⇒ ignoré et journalisé ; trame illisible ⇒ ignorée et journalisée ; la session continue | un contrat 0.3 ajoutera des messages ; une trame ratée ne doit pas couper toutes les mesures |
| Mesure d'une entité jamais déclarée ⇒ ignorée, signalée **une fois** | un capteur bavard ne noie pas le journal |
| `availability` peut viser une entité, un appareil, ou le plugin entier | v0.2 §3 |
| `online: true` ne rend disponibles que les entités **mesurées pendant la session** | une valeur restaurée au démarrage du cœur n'est pas une mesure |
| Entité qui n'est plus déclarée ⇒ indisponible (et `missing` au registre) | elle ne recevra plus de valeur |
| Commande sans `command_result` dans le délai (TTL, sinon 10 s) ⇒ `unreachable` | le cœur ne suppose jamais qu'un ordre est passé |
| Session fermée pendant l'attente, plugin absent ⇒ `unreachable` ; entité inconnue ⇒ `failed` | une référence vers rien échoue bruyamment (incident « Chbre » du 03/10/2026) |
| Trame binaire, ou de plus de 1 Mio ⇒ fermeture de la connexion | ce n'est pas le contrat |

## 4. Réglages (`appsettings.json`, section `PluginHost`)

```json
{
  "PluginHost": {
    "Tokens": { "zigbee2mqtt": "…" },
    "AllowUnregisteredPlugins": false,
    "HelloTimeout": "00:00:10",
    "Heartbeat": "00:00:15",
    "Timeout": "00:00:45",
    "CommandTimeout": "00:00:10"
  }
}
```

Les jetons ne vont pas dans un fichier versionné : `appsettings.Local.json`, variable d'environnement
(`PluginHost__Tokens__zigbee2mqtt`) ou fichier d'environnement du service systemd.

## 5. Un plugin lancé par le cœur ne lui survit pas

Constaté le 19/10/2026 : après un arrêt brutal du cœur (débogueur, kill, plantage), les plugins lancés par la
page Services tournaient encore, parent mort. Le `Kill(entireProcessTree)` du superviseur ne sert qu'aux
arrêts **voulus**. Un orphelin continue d'interroger ses appareils et ses API extérieures, et frappe à la porte
du cœur suivant avec un jeton périmé (refusé, nouvel essai toutes les minutes). Deux garde-fous :

| Où | Quoi | Pourquoi ça tient |
|---|---|---|
| Windows | `ProcessPluginLauncher` met chaque plugin dans un **Job Object** `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`TravailWindows`), dont il garde le handle autant que le cœur vit | à la mort du cœur, même brutale, c'est Windows qui ferme ses handles : le job se ferme et tue ce qu'il contient |
| Linux | systemd tue le cgroup du service | rien à faire côté cœur en production |
| Partout | le cœur pose `Krissvia__ParentPid` (son PID, après les réglages : aucun ne peut l'écraser) ; les deux SDK vérifient toutes les 5 s que ce processus existe et **arrêtent le plugin** sinon | couvre ce qui reste : cœur lancé à la main sous Linux, job refusé par le système |

Côté C#, `CoreClientOptions.ParentPid` se lit de la section `Krissvia` comme le reste ; chaque `Program.cs`
de plugin appelle `builder.Services.AddParentProcessWatch(coeur)`, qui arrête l'hôte proprement
(`StopApplication`). Côté Python, `CoreClient.run()` rend la main et `krissvia.run()` annule les tâches du
plugin. **Sans la variable** (plugin lancé à la main, depuis Visual Studio ou une autre machine), rien n'est
surveillé : le comportement est celui d'avant.

Limite connue : un processus que le plugin lancerait lui-même entre son démarrage et son entrée dans le job
échapperait au job ; aucun plugin ne lance de processus.

## 6. Ce qui reste ouvert

- **`configure`** : le message existe, mais le cœur n'a pas encore de configuration de plugin à
  envoyer (ni stockage, ni validation par JSON Schema). D'ici là, un plugin lit sa configuration
  localement (arguments, variables d'environnement).
- **`discovered`** : reçu et journalisé ; l'auto-import n'est pas branché.
- **TLS** : `ws://` sur le réseau local pour commencer. Si un plugin tourne sur une autre machine que
  le cœur, `wss://` derrière le reverse proxy, le jeton seul ne protégeant pas d'une écoute.
- **Kit de conformité** (v0.2 §4) : les tests de `Krissvia.PluginHost.Tests` en sont le côté cœur ; il
  reste à en faire un exécutable contre lequel un plugin d'un autre langage se teste.
- **Autres langages** : le SDK Python (`sdk/python/krissvia.py`) refait `CoreClient` en Python, et le cœur
  lance un plugin Python lui-même (`"runtime": "python"` dans `plugin.json`) — voir `PLUGINS-PYTHON.md`.
