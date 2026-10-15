# Plugin téléinfo (Linky)

Lit la trame du compteur Linky (mode standard), publiée en JSON sur MQTT par un module branché sur la prise
TIC, et la transmet au cœur. Lecture seule : le plugin ne publie rien sur le broker et le compteur ne reçoit
aucun ordre.

```
module TIC ──MQTT (JSON)──▶ plugin ──WebSocket (contrat 0.2)──▶ cœur Krissvia
```

## Configuration

`appsettings.json`, complété par `appsettings.Local.json` (jamais versionné ; modèle :
`appsettings.Local.example.json`), puis variables d'environnement et ligne de commande.

| Clé | Défaut | Rôle |
|---|---|---|
| `Mqtt:Host`, `Port`, `Username`, `Password` | — | broker (adresse **IP**) |
| `Teleinfo:Topic` | `energy/linky` | topic de la trame ; dans DomoMud, c'est le topic de l'équipement de type Linky |
| `Teleinfo:DeviceId` | `linky` | identité de l'appareil au registre : ne plus la changer ensuite |
| `Teleinfo:Name` | `Linky` | nom proposé (les entités deviennent `sensor.linky_…`) |
| `Teleinfo:SilenceSeconds` | 60 | sans trame depuis ce délai, le plugin se déclare hors ligne |
| `Krissvia:Url`, `PluginId` (`teleinfo`), `Token` | | cœur |

## Trame reconnue

Le JSON de DomoMud (`LinkyData`) : étiquettes en majuscules, valeurs en chaînes. La casse des étiquettes ne
compte pas, et des nombres sont acceptés aussi.

| Étiquette | Entité | Valeur |
|---|---|---|
| `SINSTS` | `sensor.linky_apparent_power` | puissance apparente, **VA** — la source du délestage |
| `EAST` | `sensor.linky_index_total` | index, kWh (la trame est en Wh) |
| `EASF01`, `EASF02` | `sensor.linky_index_1`, `_index_2` | index par tarif fournisseur, kWh |
| `IRMS1`, `URMS1` | `sensor.linky_current`, `_voltage` | A, V |
| `LTARF` | `sensor.linky_tarif` | libellé du tarif en cours |
| `NTARF` | `sensor.linky_numero_tarif` | numéro du tarif |

`DATE` et `HEURE` ne sont pas utilisés : l'instant de mesure est l'heure de réception (la trame arrive en
continu, le décalage est négligeable).

## Règles

- **Une valeur illisible est ignorée, jamais lue comme zéro** : un `SINSTS` à 0 ferait tout rétablir au
  délesteur.
- **Un compteur qui se tait rend le plugin hors ligne** (60 s par défaut), au lieu de laisser croire que la
  dernière puissance est toujours vraie. Le délesteur passe alors à la source suivante de sa liste.
