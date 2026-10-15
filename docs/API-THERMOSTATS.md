# API des thermostats

> Premier morceau de l'API publique (contrat 3 de l'architecture). JSON en snake_case, énumérations en texte :
> le même format que la configuration enregistrée en base.

## Accès

Toute l'API (`/api/…`) exige l'en-tête `X-Api-Key`, comparé en temps constant à `Api:Key`
(`appsettings.Local.json` ou variable d'environnement `Api__Key`, jamais un fichier versionné).
Sans clé configurée, l'API n'est ouverte qu'en développement ; en production elle répond **503**
plutôt que de s'ouvrir à tout le réseau.

## Routes

| Méthode | Route | Corps | Effet |
|---|---|---|---|
| GET | `/api/thermostats` | | Liste : `settings` + `state` (statut, consigne, température, puissance, motifs d'inhibition…) |
| GET | `/api/thermostats/{id}` | | Un thermostat |
| PUT | `/api/thermostats/{id}` | réglages (voir ci-dessous) | Crée ou remplace ; l'identifiant vient de l'URL |
| DELETE | `/api/thermostats/{id}` | | Supprime ; ses coupures (fenêtre, sonde) sont levées |
| POST | `/api/thermostats/{id}/mode` | `{"mode": "comfort"}` | Choisit un mode : **suspend le planning** et efface la consigne ajustée |
| POST | `/api/thermostats/{id}/target` | `{"temperature": 19.5}` ou `null` | Ajuste la consigne (5 à 30 °C) ; `null` revient à celle du mode |
| POST | `/api/thermostats/{id}/schedule` | `{"active": true}` | Active ou suspend le planning |

Modes : `off`, `frost_protection`, `eco`, `comfort2`, `comfort1`, `comfort`.
Erreurs : **400** avec `{"error": "…"}` lisible tel quel, **404** pour un thermostat inconnu.

## Créer un thermostat

Seuls la sonde et les radiateurs sont obligatoires ; le reste prend les valeurs de DomoMud
(confort 20 °C, éco 16,5 °C, hors-gel 7 °C, cycle 600 s, failsafe 60 min…).

```json
{
  "name": "Chambre RdC",
  "temperature_entity_id": "sensor.sonde_chbre_rdc_onenuo_temperature",
  "switch_entity_ids": ["switch.radiateur_chambre_rdc"],
  "manual_mode": "eco"
}
```

Chaque entité est vérifiée au registre **avec sa famille** (sonde = `sensor`, radiateur = `switch`,
ouverture = `binary_sensor`) : un thermostat qui vise un appareil inexistant est refusé à la saisie.

Planning : `"schedule_active": true` et

```json
"schedule": {
  "default_mode": "eco",
  "days": {
    "monday": [ { "start": "06:00:00", "end": "08:30:00", "mode": "comfort" } ],
    "saturday": [ { "start": "22:00:00", "end": "07:00:00", "mode": "comfort2" } ]
  }
}
```

Une fin à `00:00:00` veut dire minuit du soir ; une fin avant le début fait un créneau de nuit, actif le soir
**et le matin du même jour** (comportement de DomoMud).
