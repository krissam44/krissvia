# Contrat plugin ↔ cœur — v0.3 : lire et commander d'autres entités

> 22/10/2026. Complète la v0.2 (`CONTRACT-V0.2-PROPOSITION.md`, `TRANSPORT.md`). Rien n'est figé tant que la version
> commence par `0.`. Raison : une platine openHASP affiche un thermostat et commande des lampes qui ne sont pas à elle ;
> le digicode de l'alarme arme une alarme qui n'est pas à lui. Jusqu'ici un plugin ne pouvait que publier SES entités
> et exécuter les ordres qui les visaient.

## 1. Ajouts, et seulement des ajouts

Un cœur 0.3 accepte les plugins 0.2 et 0.3 ; le `welcome` renvoie la version retenue (celle du plugin). Un plugin 0.2
ne voit aucune différence. Seul un plugin qui utilise les messages ci-dessous a besoin de parler 0.3.

| Message | Sens | Contenu |
|---|---|---|
| `subscribe` | plugin → cœur | `entity_ids` : la liste COMPLÈTE des entités à suivre ; elle remplace la précédente (liste vide : plus rien) |
| `subscribed` | cœur → plugin | `granted` (identifiants), `denied` (`[{ "entity_id", "reason": "not_allowed" \| "unknown" }]`) |
| `entity_state` | cœur → plugin | `entity_id`, `name`, `kind`, `device_class`, `unit`, `available`, `value` et `measured_at` (absents si jamais reçue) — une fois à l'abonnement, puis à chaque changement |
| `entity_command` | plugin → cœur | `id` (corrélation), `entity_id`, `command`, `args` |
| `entity_command_result` | cœur → plugin | `id`, `outcome` (`accepted` / `failed` / `unreachable`), `verification`, `detail` |
| `link_state` | plugin → cœur | `link` (`mqtt`), `connected`, `detail` (le motif d'un échec, lisible) — à chaque changement (23/10/2026) |

- Un abonnement ne survit pas à la session : le plugin le renvoie à chaque (re)connexion, après sa resynchronisation.
  Les SDK le font seuls.
- L'ordre passe par le **bus de commandes** du cœur, comme celui d'un écran : vérifié, journalisé, signé
  `plugin:{id}`. Une entité qui ne déclare pas la commande, ou un plugin qui ne la porte pas, échoue bruyamment.
- Un ordre sur une entité d'un autre plugin passe par le cœur, jamais directement : c'est lui qui sait où elle vit.
- `link_state` dit ce que le plugin sait de SON lien vers l'extérieur (le broker MQTT). Le cœur ne se connecte pas
  lui-même au broker : la pastille MQTT montre ce que les plugins en disent, c'est-à-dire ce qui compte vraiment.
  Le cœur oublie l'état à la fin de la session ; les SDK le renvoient à chaque connexion.

## 2. Autorisation : ce que l'administrateur a choisi dans les réglages

Un plugin ne lit et ne commande QUE les entités choisies dans **ses réglages** (page Services) :

```json
"Thermostat": { "type": "string", "format": "entity", "x-kinds": ["climate"], "x-access": "command", "title": "Thermostat" },
"Sondes": { "type": "array", "items": { "type": "object", "properties": {
    "Entite": { "type": "string", "format": "entity", "x-kinds": ["sensor"], "x-device-classes": ["temperature"] } } } }
```

- `format: "entity"` : la page Services propose une liste d'entités, filtrée par `x-kinds` et `x-device-classes`.
- `x-access` : `read` (par défaut) ou `command`. Lire ne permet pas de commander.
- Les entités choisies, où qu'elles soient dans les réglages (objets, listes), forment l'**autorisation** du plugin.
  Pas d'écran de droits à part : choisir l'entité, c'est l'autoriser, et la retirer des réglages la retire.
- Refus : `not_allowed` à l'abonnement, `failed` (« entité non autorisée pour ce plugin ») à la commande.
- Un plugin lancé à la main sans réglages enregistrés dans le cœur ne lit et ne commande rien.

**Discipline, pas barrière absolue** : un plugin tourne sur la machine du cœur avec les droits du service ; la règle
empêche l'erreur (une platine qui commanderait tout) et rend visible ce que chaque plugin touche. Elle vaut ce que vaut
le choix des plugins installés (`WIDGETS.md` §9, même raisonnement).

## 3. SDK

- **C#** : `CoreClient.SetSubscriptionsAsync(ids)`, l'événement `EntityStateReceived`, et
  `CommandEntityAsync(entityId, command, args)` qui attend le résultat (délai : 15 s).
- **Python** : `client.subscribe(ids)`, la méthode `Plugin.on_entity_state(message)`, et
  `await client.command(entity_id, command, args)`.
- **État d'un lien** : `CoreClient.ReportLinkAsync("mqtt", connecte, motif)` en C#, `await client.report_link(...)` en
  Python. Un même état sans motif garde le motif connu : « mot de passe refusé » ne doit pas devenir un simple
  « coupé » à la déconnexion qui suit. Avec `Krissvia.Sdk.Mqtt`, le plugin n'a qu'à relayer
  `IMqttHandler.ConnexionImpossibleAsync(motif)`.

## 4. Broker MQTT commun

Le broker se saisit une fois, dans la **pastille MQTT** de la barre du haut (adresse, port, identifiants), et non plus
dans chaque plugin. Un plugin y prend part en marquant sa section de réglages :

```json
"Mqtt": { "type": "object", "x-shared": "mqtt", "properties": { "Host": …, "Port": …, "Username": …, "Password": …, "BaseTopic": … } }
```

- Au lancement, le cœur remplace `Host`, `Port`, `Username` et `Password` de cette section par ceux du broker commun.
  Le reste (`BaseTopic`) ne change pas.
- Un plugin dont l'adresse est renseignée garde **son** broker (cas rare : un deuxième broker). L'adresse n'est donc
  plus obligatoire dans une section partagée : vide, c'est le broker commun.
- Sans broker du tout, le plugin démarre quand même et le dit (`aucun broker réglé`) au lieu de redémarrer en boucle.
- À la première ouverture, la pastille reprend le broker déjà saisi dans un plugin, et propose de le retirer des
  plugins qui en avaient un. Enregistrer relance les plugins MQTT activés : ils ne lisent leurs réglages qu'au démarrage.
- Couleur : verte si tous les plugins MQTT activés joignent le broker, rouge si l'un n'y arrive pas (le motif est dans
  l'infobulle), orange pendant un démarrage, grise sans plugin MQTT activé.
