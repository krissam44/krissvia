# Plugin Zigbee2MQTT

Relie un Zigbee2MQTT au cœur Krissvia : il lit le réseau sur le broker MQTT, déclare les appareils au cœur,
lui transmet les mesures et exécute ses ordres. Il ne décide jamais de rien : la régulation reste au cœur.

```
Zigbee2MQTT ──MQTT──▶ plugin ──WebSocket (contrat 0.2)──▶ cœur Krissvia
```

## Configuration

`appsettings.json` à côté de l'exécutable, complété par `appsettings.Local.json` (jamais versionné : mot de
passe du broker, jeton), puis par les variables d'environnement et la ligne de commande.

| Clé | Défaut | Rôle |
|---|---|---|
| `Mqtt:Host` | — (obligatoire) | adresse **IP** du broker (la résolution DNS échoue parfois en silence sous systemd) |
| `Mqtt:Port` | 1883 | |
| `Mqtt:Username`, `Mqtt:Password` | aucun | |
| `Mqtt:BaseTopic` | `zigbee2mqtt` | `base_topic` de Zigbee2MQTT |
| `Krissvia:Url` | `ws://127.0.0.1:12795/plugins` | point d'entrée du cœur |
| `Krissvia:PluginId` | `zigbee2mqtt` | devient l'`integration_id` du registre : ne plus le changer ensuite |
| `Krissvia:Token` | aucun | jeton déclaré dans `PluginHost:Tokens` du cœur |

Variables d'environnement : `Mqtt__Host`, `Krissvia__Token`, etc.

## Ce qui devient une entité

L'identité d'une entité est **l'adresse IEEE + la propriété** (`0x00158d0001000001:temperature`). Renommer un
appareil dans Zigbee2MQTT ne change donc rien côté cœur : le plugin suit le nouveau `friendly_name` pour lire
et pour publier.

| Zigbee2MQTT | Entité | Valeur |
|---|---|---|
| `switch` (chaque voie : `state`, `state_l1`…) | `switch` | booléen ; ordres `turn_on`, `turn_off`, `toggle` |
| `light` | `light` | `{"on", "brightness": 0..100}` ; `set_brightness` en % (Zigbee2MQTT compte de 0 à 254) |
| `cover` | `cover` | `{"state", "position"}` ; `open`, `close`, `stop`, `set_position` |
| `temperature`, `local_temperature`, `humidity`, `pressure`, `illuminance`, `co2`, `power`, `energy`, `voltage`, `current`, `battery` | `sensor` | nombre, dans l'unité du contrat (mV → V, mA → A, Wh → kWh, kW → W) |
| `contact`, `occupancy`, `presence`, `water_leak`, `smoke`, `tamper`, `vibration`, `battery_low` | `binary_sensor` | true = état actif : **ouvert**, présence, fuite… |
| `action` | `event` | « single », « double »… |

**Liste blanche délibérée.** Les réglages (`power_on_behavior`, `child_lock`, consignes, délais) et les
diagnostics (`linkquality`) ne deviennent pas des entités. Une propriété **modifiable** n'est jamais une
mesure. On ajoute une propriété à la liste quand un besoin réel apparaît.

Le coordinateur et les appareils désactivés ne sont pas déclarés. Une tête thermostatique (`climate`) ne
donne que sa sonde : la régulation est au cœur.

## Règles qui comptent

- **Contact** : Zigbee2MQTT publie `contact: true` quand c'est **fermé**, mais déclare `value_on: false`.
  Le plugin suit `value_on`, et n'applique sa propre inversion qu'en son absence ; inverser les deux fois
  retournerait toutes les portes.
- **Un appui de bouton n'est jamais rejoué** : les événements ne sont ni gardés ni renvoyés à la
  resynchronisation, sinon chaque reconnexion au cœur rappuierait sur le bouton.
- **Resynchronisation** : à chaque connexion au cœur, le plugin renvoie la déclaration, toutes les dernières
  valeurs connues et les joignabilités.
- **Joignabilité** : `{appareil}/availability` → appareil hors ligne ; `bridge/state` hors ligne ou broker
  perdu → tout le plugin hors ligne. Les deux formats (`offline` brut, `{"state":"offline"}`) sont compris.
- **Ordres** : `accepted` veut dire « publié sur `/set` », Zigbee2MQTT n'acquittant pas ; l'effet se
  constate sur l'état republié par l'appareil. Sans broker, l'ordre échoue tout de suite (`unreachable`) :
  pas de file d'attente, donc jamais d'ordre exécuté en retard.

## Tests

`tests/Krissvia.Plugin.Zigbee2Mqtt.Tests` : la traduction sur un réseau type (`ReseauZigbee`), et la chaîne
complète contre le vrai `PluginHub` par une connexion WebSocket réelle, Zigbee2MQTT et le broker étant
simulés (`SimulateurZ2m`). Reste à valider sur un vrai réseau : les formats viennent de la documentation de
Zigbee2MQTT et des appareils connus de DomoMud, pas d'une capture.
