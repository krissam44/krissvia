# Plugin Shelly

Interroge les appareils Shelly en HTTP sur le réseau local : mesures d'énergie (pinces) et relais.

```
Shelly ◀──HTTP──▶ plugin ──WebSocket (contrat 0.2)──▶ cœur Krissvia
```

## Configuration

**Lancé par le cœur (page Services)** : les réglages se saisissent dans la fiche du service, qui les lui
transmet au lancement ; ils l'emportent sur les fichiers ci-dessous. **Lancé à la main** (Visual Studio,
service systemd) :

`appsettings.json`, complété par `appsettings.Local.json` (jamais versionné ; modèle :
`appsettings.Local.example.json`).

```json
{ "Shelly": { "PollSeconds": 2, "Devices": [ { "Host": "192.168.1.203", "Name": "Pince maison" } ] } }
```

Seuls l'adresse et le nom sont à donner : le plugin interroge `/shelly` au démarrage et reconnaît la génération,
le nombre de voies de mesure et de relais. L'identité au registre est l'**adresse MAC** : changer l'IP de
l'appareil ne change aucune entité.

## Ce qui devient une entité

| Shelly | Entité | Valeur |
|---|---|---|
| voie de mesure | `…_power` | puissance active, W |
| | `…_apparent_power` | **puissance apparente, VA** — mesurée (génération 2, `aprt_power`) ou calculée √(P² + Q²) (génération 1) |
| | `…_voltage`, `…_energy` (gén. 1), `…_current` (gén. 2) | V, kWh, A |
| relais | `switch.…` | `turn_on`, `turn_off`, `toggle` |

La puissance apparente est celle que comparent l'abonnement et le disjoncteur. Relevé du 15/10/2026 sur le Shelly
EM de la maison : **339 W pour 737 VA**. Une majoration forfaitaire de la puissance active (+10 %) aurait donné
373 VA, la moitié de la réalité : pour le délestage, prendre la source `…_apparent_power` en `apparent`.

## Règles

- **Interrogation toutes les 2 s, pas MQTT** : en génération 1, les mesures ne partent en MQTT que toutes les
  30 s environ, trop lentement pour le délestage (mesure de moins de 10 s).
- Appareils interrogés **en parallèle** : un appareil injoignable ne retarde pas les autres.
- Une voie marquée invalide (`is_valid: false`, pince débranchée) ne publie rien : jamais de zéro.
- Après un ordre, l'état est **relu et publié avant de répondre au cœur** : c'est cet état qui confirme une
  coupure vérifiée.
- Trois lectures ratées de suite : appareil hors ligne ; il revient à la première réponse. Un appareil
  injoignable au démarrage n'est déclaré qu'à sa première réponse.

Génération 1 vérifiée sur un Shelly EM (`SHEM`) ; génération 2 (`/rpc/…`) écrite d'après la documentation de
Shelly, pas encore essayée sur un appareil réel.
