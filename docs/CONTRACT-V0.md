# Contrat plugin ↔ cœur — v0.1 (instable)

> Tant que la version commence par `0.`, ce contrat peut changer sans préavis.
> La référence exécutable est `src/Krissvia.Contracts` ; les tests
> `tests/Krissvia.Contracts.Tests` fixent le JSON.

## Principes

- Un plugin est un **processus séparé** : aucun code tiers dans le cœur.
- Le contrat est indépendant du transport (WebSocket, gRPC, MQTT). Les messages sont du JSON UTF-8.
- Noms de champs en `snake_case`, énumérations en chaînes `snake_case`, champ `type` pour désigner le message
  (il n'a pas besoin d'être le premier champ).
- Les champs absents ne sont pas écrits (pas de `null`).
- Le plugin **normalise** : le cœur ne voit que les unités et conventions ci-dessous.

## Messages

| `type` | Sens | Rôle |
|---|---|---|
| `hello` | plugin → cœur | Ouverture de session : id, version du plugin, version du contrat, jeton |
| `configure` | cœur → plugin | Configuration (validée par le JSON Schema du plugin) |
| `declare` | plugin → cœur | **Instantané complet** des appareils et entités du plugin ; à renvoyer à chaque changement. Ce qui disparaît est marqué « manquant » dans le registre, jamais supprimé |
| `state` | plugin → cœur | Nouvelles valeurs, par lot |
| `availability` | plugin → cœur | Joignabilité d'un appareil (ou du plugin si `device_unique_id` est absent) |
| `discovered` | plugin → cœur | Appareils détectés non configurés (auto-import) |
| `command` | cœur → plugin | Ordre sur une entité |
| `command_result` | plugin → cœur | Issue d'une commande : `accepted`, `failed`, `unreachable` |
| `ping` | deux sens | Battement de cœur (délais fixés dans `PluginHost`) |
| `shutdown` | deux sens | Fermeture propre |

## Modèle

Un **appareil** (`DeviceDescriptor`) porte des **entités** (`EntityDescriptor`). Chaque entité a une famille
technique (`kind`) : `sensor`, `binary_sensor`, `switch`, `light`, `cover`, `event`.

L'**usage** (radiateur, chauffe-eau, éclairage…) n'est **pas** dans le contrat : il est choisi par l'utilisateur
dans le cœur. De même, le cœur attribue lui-même l'identifiant stable (`entity_id`) ; le plugin ne fournit que
son `unique_id`.

Valeur d'une entité (`value`) : nombre pour `sensor`, booléen pour `binary_sensor` et `switch`, objet pour
`light` et `cover`, chaîne pour `event`.

## Conventions

| Sujet | Règle |
|---|---|
| Identifiants | `unique_id` **immuable**, jamais un nom affiché ; unique au sein du plugin |
| Température | °C |
| Puissance, énergie | W, kWh |
| Luminosité, position, batterie, humidité | 0 à 100 |
| `opening` | `true` = **ouvert** (le plugin inverse si le protocole dit l'inverse) |
| `occupancy` | `true` = présence détectée |
| Dates | ISO 8601, UTC |
| Entité sans valeur récente ou appareil injoignable | `availability` à `false`, jamais « dernière valeur en cache » |

## Commandes

| Famille | Commandes standard |
|---|---|
| `switch`, `light` | `turn_on`, `turn_off`, `toggle` |
| `light` | `set_brightness` (`args` : `{"brightness": 0..100}`) |
| `cover` | `open`, `close`, `stop`, `set_position`, plus des presets propres au plugin |

- `priority: "safety"` : ordre de sécurité (délestage, fenêtre ouverte, sonde morte). Le plugin l'exécute en
  premier et répond **toujours** par un `command_result`.
- `ttl_seconds` : durée de validité ; passé ce délai, le plugin ignore l'ordre.
- `unreachable` : l'ordre n'est pas passé. Le cœur ne doit pas supposer le contraire.

## Perte d'un plugin

Si le heartbeat s'arrête, le cœur marque les entités du plugin `unavailable`. Les moteurs du cœur (failsafe
sonde morte, délestage) réagissent alors comme pour n'importe quelle perte de mesure.

## Exemple

```json
{"type":"hello","plugin_id":"zigbee2mqtt","plugin_version":"0.1.0","contract_version":"0.1"}

{"type":"declare","devices":[{"unique_id":"0x00158d0001a2b3c4","name":"Prise salon","entities":[
  {"unique_id":"0x00158d0001a2b3c4:switch","kind":"switch","commands":["turn_on","turn_off"]},
  {"unique_id":"0x00158d0001a2b3c4:power","kind":"sensor","device_class":"power","unit":"W","state_class":"measurement","commands":[]}]}]}

{"type":"state","states":[{"entity_unique_id":"0x00158d0001a2b3c4:power","value":1200.5}]}

{"type":"command","id":"7f3a","entity_unique_id":"0x00158d0001a2b3c4:switch","command":"turn_off","priority":"safety","ttl_seconds":5}

{"type":"command_result","id":"7f3a","outcome":"accepted"}
```

## Points ouverts

- Transport retenu (WebSocket + JSON ou gRPC) et délais du heartbeat.
- États structurés (prévisions météo) : `kind` dédié ou valeur objet.
- Auto-extinction côté appareil (`lease`) pour les radiateurs et chauffe-eau, via `args`.
- Publication du JSON Schema de chaque message pour les SDK non C#.
