# Plugin MQTT Discovery

Reconnaît les appareils qui s'annoncent sur le broker **à la manière de Home Assistant** (MQTT Discovery) : modules
ESPHome, Tasmota, OpenMQTTGateway, et tout ce qui publie des `homeassistant/<composant>/…/config`. Il déclare les
appareils au cœur, transmet leurs mesures, suit leur joignabilité et exécute les ordres. Il ne décide jamais de rien.

```
module ESPHome ──MQTT (config retenue + états)──▶ plugin ──WebSocket (contrat 0.3)──▶ cœur Krissvia
```

## Configuration

Le broker est celui de la **pastille MQTT** de la barre du haut (adresse vide dans la fiche du service). Seul
réglage propre : `Mqtt:DiscoveryPrefix`, `homeassistant` par défaut, comme ESPHome.

| Clé | Défaut | Rôle |
|---|---|---|
| `Mqtt:Host`, `Mqtt:Port`, `Mqtt:Username`, `Mqtt:Password` | broker commun | à ne saisir que pour un broker propre à ce plugin |
| `Mqtt:DiscoveryPrefix` | `homeassistant` | le `discovery_prefix` des appareils |
| `Krissvia:Url`, `Krissvia:PluginId`, `Krissvia:Token` | comme les autres plugins | `PluginId` = `mqtt-discovery`, à ne plus changer |

## Un module ESPHome

Dans le YAML : `discovery: true` (c'est le défaut quand le bloc `mqtt:` est présent), des capteurs **non**
`internal`, nommés. Exemple pour une sonde SHT30 :

```yaml
esphome:
  name: esp32-sht30-etage
  friendly_name: ESP32-SHT30-Etage

mqtt:
  broker: 192.168.1.60
  username: krissvia
  password: !secret mqtt_password
  discovery: true
  discovery_unique_id_generator: mac   # identifiants propres à chaque module
  topic_prefix: krissvia/esp32-sht30-etage
  log_topic: null

sensor:
  - platform: sht3xd
    address: 0x45
    update_interval: 60s
    temperature:
      name: Température
      filters:
        - offset: -3.2
    humidity:
      name: Humidité
```

ESPHome publie alors, **retenus** : la configuration de chaque capteur sous `homeassistant/sensor/…/config`, les
mesures sous `krissvia/esp32-sht30-etage/sensor/<objet>/state`, et `online` / `offline` (dernière volonté) sous
`krissvia/esp32-sht30-etage/status`. Le plugin s'abonne à ces topics à mesure qu'il lit les configurations.

## Entités manuelles

Pour ce qui ne s'annonce pas — Rfx2Mqtt, un script, un vieux module —, la fiche du service (page **Services**) porte
une liste **Entités manuelles** : une ligne par valeur lue sur un topic, sans écrire de plugin. Chaque ligne est
traduite en la configuration qu'aurait publiée l'appareil, puis suit exactement le chemin d'une entité découverte
(lecture, conversions d'unités, ordres, joignabilité, déclaration complète).

| Champ | Rôle |
|---|---|
| Appareil | regroupe les entités (« Sonde salon ») |
| Nom | nom de l'entité ; vide : l'entité principale de l'appareil |
| Type | `sensor`, `binary_sensor`, `switch`, `light` |
| Classe, Unité | celles du contrat ; un capteur sans l'une ni l'autre est un **libellé**, pas un nombre ; `energy` est un compteur croissant |
| Topic d'état | **exact** (pas de `+` ni de `#` : les entités sont rangées par topic) |
| Champ JSON | `temperature`, `a.b`, `liste.0` ; vide : la charge entière |
| Valeurs « oui » / « non » | vides toutes les deux : `ON`/`OFF`, `true`/`false`, `1`/`0`, sans tenir compte de la casse ; saisies : seules reconnues |
| Topic de commande | interrupteur ou lumière : on y publie les valeurs oui / non (`ON` / `OFF`) ; vide : lecture seule |
| Topic de disponibilité | facultatif, `online` / `offline` |

**Identité = topic + champ** (`manuel:{topic}#{champ}`). Renommer l'entité ou son appareil ne casse rien ; changer de
topic ou de champ crée une autre entité, l'ancienne devient disparue (supprimable dans l'écran Entités). Une ligne
inutilisable (joker, type inconnu) ou en double est écartée, avec un avertissement au journal. Enregistrer la fiche
relance le plugin, comme tout réglage.

Exemple, une sonde Oregon publiée par Rfx2Mqtt avec le préfixe `domomud` :

| Appareil | Nom | Type | Classe | Topic d'état | Champ | Unité |
|---|---|---|---|---|---|---|
| Sonde salon | Température | Capteur | Température | `domomud/sensor/th/salon/temperature` | | °C |
| Sonde salon | Humidité | Capteur | Humidité | `domomud/sensor/th/salon` | `humidity` | % |
| Sonde salon | Pile | Capteur | Pile | `domomud/sensor/th/salon/battery` | | % |
| Sonde salon | Pile faible | Capteur binaire | Pile | `domomud/sensor/th/salon/battery_low` | | |
| Pluviomètre | Intensité | Capteur | | `domomud/sensor/rain/jardin/rain_rate` | | mm/h |

Pas de conversion m/s → km/h pour le vent : saisir l'unité que publie l'appareil.

## Ce qui devient une entité

| Composant | Entité | Valeur et ordres |
|---|---|---|
| `sensor` avec unité, classe de mesure ou classe numérique | `sensor` | nombre, dans l'unité du contrat (°F → °C, Wh → kWh, kW → W, mV → V, mA → A, Pa/kPa/mbar → hPa) |
| `sensor` sans unité ni classe (version, adresse IP) | `sensor` de classe `enum` | libellé |
| `binary_sensor` | `binary_sensor` | true = `payload_on` ; `door`/`window`/`garage_door` → `opening`, `motion` → `occupancy` |
| `switch` | `switch` | booléen ; `turn_on`, `turn_off`, `toggle` si `command_topic` |
| `light` (schéma `json` ou par défaut) | `light` | `{"on", "brightness": 0..100}` ; `set_brightness` au schéma json, à l'échelle `brightness_scale` |

Les autres composants (`cover`, `climate`, `number`, `select`, `button`…) et les réglages
(`entity_category: config`) sont ignorés, avec une ligne au journal. Les classes et unités inconnues passent telles
quelles (`signal_strength` en dBm, par exemple).

## Règles qui comptent

- **Identité = appareil + `unique_id`.** Par défaut, ESPHome donne le même `unique_id` à la même sonde de deux
  modules (`ESPsensortemperature`) : seul l'identifiant de l'appareil (son adresse MAC) les distingue. Sans
  `unique_id`, c'est le topic de configuration qui sert d'identité.
- **Une déclaration complète, différée d'une seconde.** Le cœur prend chaque déclaration pour la liste entière : les
  configurations retenues arrivent une à une à la connexion, et les déclarer au fil de l'eau ferait passer les
  autres entités pour disparues. Un état reçu avant la déclaration de son entité est gardé et part avec elle.
- **Un module hors ligne le reste.** Son dernier état est retenu sur le broker et rejoué à chaque connexion ; le cœur
  rendrait l'entité disponible. Le plugin redit « hors ligne » derrière chaque état d'une entité qu'il sait injoignable.
- **La sonde interne d'un ESP32** (température de classe `temperature`, catégorie `diagnostic`) devient une
  `device_temperature` : celle de la puce n'a rien à faire dans les sondes de pièce.
- **Gabarits : un sous-ensemble, pas Jinja.** `{{ value }}`, `{{ value_json.a.b }}`, `{{ value_json['a'] }}` et les
  filtres `float`, `int`, `round(n)`, `lower`, `upper`, `trim`, `string`, `is_defined`. Un gabarit hors de ce
  sous-ensemble (calcul, condition) fait **ignorer** l'entité, avec une ligne au journal : une valeur fausse affichée
  comme une mesure serait pire qu'une entité absente.
- **Une bascule se calcule sur l'état connu** : un appareil Home Assistant ne connaît pas `TOGGLE`. Sans état reçu,
  elle est refusée.
- **Une configuration vidée** (charge vide retenue) retire l'entité ; le cœur la garde, marquée disparue.

Non pris en charge pour l'instant : `expire_after`, `json_attributes_topic`, les schémas `template` des lumières, les
couleurs.
