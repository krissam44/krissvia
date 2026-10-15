# API publique

> Contrat 3 de l'architecture, en cours : thermostats et délestage. JSON en snake_case, énumérations en texte :
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
| POST | `/api/thermostats/{id}/target` | `{"temperature": 19.5}` ou `null` | Ajuste la consigne (5 à 30 °C) ; `null` revient à celle du mode. Refusée en mode `off` (Off est ferme : choisir d'abord un mode) |
| POST | `/api/thermostats/{id}/schedule` | `{"active": true}` | Active ou suspend le planning |

Modes : `off`, `frost_protection`, `eco`, `comfort2`, `comfort1`, `comfort`. Choisir un mode efface la consigne
ajustée : pour chauffer à 24 °C, `mode` **puis** `target`.

Dans `state`, `status` peut valoir `coasting` (inertie) : la pièce dépasse la consigne de plus de
`coasting_high` (1 °C) et le moteur la laisse redescendre jusqu'à consigne + `coasting_low` avant de reprendre.
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

## Délestage

Un seul document de configuration par installation.

| Méthode | Route | Effet |
|---|---|---|
| GET | `/api/load-shedding` | `configuration`, `status` (puissance lue, seuils, équipements délestés, coupures non confirmées), `load_error` |
| PUT | `/api/load-shedding` | Remplace la configuration ; vérifiée, enregistrée, appliquée aussitôt |

```json
{
  "enabled": true,
  "subscription_kva": 6,
  "alert_percent": 80,
  "restore_percent": 70,
  "sources": [
    { "name": "Linky", "power_entity_id": "sensor.linky_apparent_power", "power_quality": "apparent" },
    { "name": "Pince", "power_entity_id": "sensor.pince_maison_apparent_power_0", "power_quality": "apparent" },
    { "name": "Somme", "kind": "equipment_sum",
      "metered_equipment": [ { "switch_entity_id": "switch.radiateur_chambre_rdc", "nominal_power": 1500 } ] }
  ],
  "devices": [
    { "id": "cumulus", "name": "Chauffe-eau", "priority": 1, "nominal_power": 2000,
      "restore_delay_seconds": 120, "switch_entity_ids": ["switch.cumulus"] },
    { "id": "chambre_rdc", "priority": 5, "nominal_power": 1500,
      "switch_entity_ids": ["switch.radiateur_chambre_rdc"], "thermostat_id": "chambre_rdc" }
  ]
}
```

- Seuils déduits de l'abonnement : 6 kVA à 80 / 70 % donnent 4 800 / 4 200 VA.
- **Sources par ordre de préférence** : chaque tick prend la première dont la mesure a moins de 10 s
  (`max_reading_age_seconds`) ; `status.active_source` dit laquelle, et le journal note chaque bascule. Une
  source unique qui se tait laisserait le délesteur sans décision : la protection disparaîtrait en silence.
- Une source est `entity` (Linky en VA, `power_quality: "apparent"` ; pince en W, `"active"`, majorée de
  `active_to_apparent_factor`, 1,10 par défaut) ou `equipment_sum` (`metered_equipment` : switch, mesure
  facultative, puissance nominale).
- Priorité 1 = délesté en premier. Un équipement lié à un thermostat (`thermostat_id`) est coupé par
  inhibition : consigne et mode intacts, reprise avec la consigne du moment.
- Toute coupure est vérifiée (état publié après l'ordre, 5 s) ; non confirmée, elle ne compte pas comme
  économie, l'équipement suivant est délesté, et elle est retentée toutes les 2 s.
- Aucune source fraîche : aucune décision, `status.source_unavailable` à `true` — jamais « 0 VA ».

Le Linky arrive par le plugin téléinfo (`plugins/Krissvia.Plugin.Teleinfo`), la pince Shelly par le plugin
Shelly (`plugins/Krissvia.Plugin.Shelly`), qui publie la **puissance apparente** (`…_apparent_power`, en VA) :
à prendre en `apparent`, sans majoration. Relevé du 15/10/2026 : 339 W pour 737 VA — la majoration de 10 % de la
puissance active aurait donné la moitié de la réalité.

