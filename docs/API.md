# API publique

> Contrat 3 de l'architecture, en cours : thermostats et délestage. JSON en snake_case, énumérations en texte :
> le même format que la configuration enregistrée en base.

## Accès

Toute l'API (`/api/…`) exige l'en-tête `X-Api-Key`, comparé en temps constant à `Api:Key`
(`appsettings.Local.json` ou variable d'environnement `Api__Key`, jamais un fichier versionné).
Sans clé configurée, l'API n'est ouverte qu'en développement ; en production elle répond **503**
plutôt que de s'ouvrir à tout le réseau.

Une session ouverte dans l'interface d'administration (cookie `krissvia`) vaut la clé : les écrans appellent
l'API sans la connaître. Le cookie est `SameSite=Lax`, donc un autre site ne peut pas le faire partir avec un
`POST`.

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
| PUT | `/api/thermostats/{id}/schedule` | `{"days": {…}, "default_mode": "eco"}` | Remplace le planning **seul** : mode, activation et consigne ajustée restent tels quels. Refuse un créneau de durée nulle ou hors de 00:00–23:59 |

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

Une fin à `00:00:00` veut dire minuit du soir ; une fin avant le début fait un créneau de nuit, du soir de son jour
**jusqu'au lendemain matin** (lundi 22:00 → 06:00 couvre la nuit du lundi au mardi). DomoMud comprenait le soir et
le matin du même jour : à convertir à l'import.

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
    { "name": "Somme", "kind": "equipment_sum" }
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
  `active_to_apparent_factor`, 1,10 par défaut) ou `equipment_sum` : la somme des switches allumés dont la puissance
  est déclarée dans les Entités (ou la mesure de leur appareil), sans liste à tenir.
- Priorité 1 = délesté en premier. Un équipement lié à un thermostat (`thermostat_id`) est coupé par
  inhibition : consigne et mode intacts, reprise avec la consigne du moment.
- Toute coupure est vérifiée (état publié après l'ordre, 5 s) ; non confirmée, elle ne compte pas comme
  économie, l'équipement suivant est délesté, et elle est retentée toutes les 2 s.
- Aucune source fraîche : aucune décision, `status.source_unavailable` à `true` — jamais « 0 VA ».

Le Linky arrive par le plugin téléinfo (`plugins/Krissvia.Plugin.Teleinfo`), la pince Shelly par le plugin
Shelly (`plugins/Krissvia.Plugin.Shelly`), qui publie la **puissance apparente** (`…_apparent_power`, en VA) :
à prendre en `apparent`, sans majoration. Relevé du 15/10/2026 : 339 W pour 737 VA — la majoration de 10 % de la
puissance active aurait donné la moitié de la réalité.

## Programmations des équipements

Allumer ou éteindre des switches (prises, chauffe-eau…) selon un planning hebdomadaire, avec les mêmes règles de
créneaux que les thermostats (un créneau de nuit finit le lendemain matin).

| Méthode | Route | Effet |
|---|---|---|
| GET | `/api/schedules` | Liste : `settings` + `state` (`desired`, `applied`, `next_change`) |
| PUT | `/api/schedules/{id}` | Crée ou remplace ; l'identifiant vient de l'URL |
| DELETE | `/api/schedules/{id}` | Supprime ; les équipements restent dans l'état où ils sont |

```json
{
  "name": "Chauffe-eau heures creuses",
  "enabled": true,
  "switch_entity_ids": ["switch.cumulus"],
  "default_on": false,
  "days": { "monday": [ { "start": "22:00:00", "end": "06:00:00", "on": true } ] }
}
```

- La programmation n'agit qu'aux **changements** de créneau : un allumage à la main tient jusqu'au suivant. Au
  démarrage, et après toute modification, l'état attendu est appliqué une fois. Chaque ordre est vérifié et renvoyé
  tant qu'il n'est pas confirmé.
- Un switch n'a qu'un pilote : refusé s'il est le radiateur d'un thermostat ou s'il appartient déjà à une autre
  programmation. (Limite connue : l'enregistrement d'un thermostat ne vérifie pas encore l'inverse.)
- Avec le délestage : une programmation ne rallume jamais un équipement délesté, et le rétablissement remet l'état
  qu'elle veut à ce moment-là — un chauffe-eau dont le créneau s'est terminé pendant le délestage reste éteint.


## Entités et flux en direct

Ce dont un tableau de bord ou un widget a besoin (WIDGETS.md §8) : les entités, leur dernier état, les ordres.

| Méthode | Route | Corps | Effet |
|---|---|---|---|
| GET | `/api/entities` | | Toutes les entités : registre, pièce, dernier état |
| GET | `/api/entities/{id}` | | Une entité ; **404** si inconnue |
| POST | `/api/entities/{id}/commands` | `{"command": "turn_on", "args": {…}, "source": "widget:jauge-cuve"}` | Ordre **vérifié** : `{"outcome": "accepted", "verification": "confirmed"}`, et `detail` en cas d'échec |
| GET (WebSocket) | `/api/live` | | Flux en direct ; `?entities=a,b` restreint aux entités nommées |

```json
{
  "entity_id": "switch.cave_chauffe_eau", "name": "Chauffe-eau", "kind": "switch",
  "commands": ["turn_on", "turn_off"], "room": "Cave",
  "enabled": true, "missing": false, "integration_id": "zigbee2mqtt",
  "state": { "value": true, "available": true, "measured_at": "2026-10-16T07:08:38+00:00" }
}
```

- Les champs vides (`room`, `unit`, `device_class`…) sont omis, sauf `state`, qui vaut **null** si l'entité n'a jamais publié : ce n'est pas zéro. `available: false` : plugin arrêté ou
  appareil injoignable, la valeur est la dernière connue.
- Un ordre est refusé (**400**) si l'entité ne l'a pas déclaré. `outcome` : `accepted`, `failed`, `unreachable` ;
  `verification` : `confirmed` (l'état a suivi), `not_confirmed` (accepté, mais rien constaté dans les 10 s),
  `not_applicable` (bascule, luminosité…), `not_sent`. Un widget n'affiche pas « allumé » parce qu'il a cliqué : il
  attend l'état, qui arrive par le flux. `source` (minuscules, chiffres, `:_.-`) signe l'ordre au journal :
  `api:widget:jauge-cuve`.
- Une entité désactivée refuse les ordres (sauf ceux de sécurité du cœur).

**Thermostats** : chaque thermostat est aussi l'entité `climate.{nom}` (intégration `coeur.thermostats`).
`extra` porte `modes` (`[{"id": "comfort", "label": "Confort"}, …]`, à afficher tels quels), `min_target`,
`max_target`, `target_step`, `radiators` et `configuration` (chemin de l'écran de réglage, `/thermostats/{id}`) ;
l'état est un objet : `temperature`, `humidity` (si l'appareil de la sonde en mesure une), `outdoor_temperature`
(mesurée), `outdoor_forecast` (moyenne prévue sur 2 h quand l'anticipation météo est active, voir PLUGINS-PYTHON.md §7),
`target`, `target_adjusted`, `mode`, `schedule_active`, `heating`, `power` (part de chauffe du cycle TPI, 0 à 1),
`cycle_seconds`, `coasting_high` et `coasting_low` (seuils d'inertie, en °C au-dessus de la consigne),
`window_open`, `status` et `status_label` (« Chauffe », « Inertie », « Délesté »…, calculé par le cœur : à
afficher tel quel). Ordres : `set_mode` `{"mode": "eco"}` (suspend le planning), `set_schedule`
`{"active": true}` (reprend ou suspend le planning), `set_target` `{"temperature": 19.5}` ou `null`, et
`adjust_target` `{"delta": 0.5}`, appliqué par le serveur à la consigne réellement suivie — deux « + » rapides
font +1 °C, là où deux `set_target` calculés par l'écran repartiraient de la même valeur. Ces ordres n'ont pas
d'état à vérifier (`not_applicable`) : le nouvel état arrive par le flux. Un refus porte son motif dans `detail`
(« thermostat arrêté (mode off)… »).

**Flux** (`/api/live`, WebSocket) : à la connexion, `{"type": "entities", "entities": […]}` ; puis
`{"type": "states", "states": {"switch.cave_chauffe_eau": {…état…}}}` à chaque changement, et de nouveau
`entities` quand le registre change (entité ajoutée, renommée, désactivée). Les changements sont regroupés : un
client lent reçoit l'état le plus récent, une seule fois, jamais une file de retard. Le client n'envoie rien. Une
connexion venue d'une page d'un autre site (en-tête `Origin` différent) est refusée (**403**) : sinon cette page
ouvrirait le flux avec le cookie de session.

## Widgets et tableau de bord

| Méthode | Route | Corps | Effet |
|---|---|---|---|
| GET | `/api/widgets` | | Catalogue : `widgets` (manifeste, `script_url`, `origin` = `standard`, `local` ou `blazor`, et `blazor` = nom du composant pour un widget Blazor) et `errors` (widgets écartés, avec leur raison) |
| GET | `/api/dashboards` | | Les pages, dans l'ordre (une page `principal` vide si rien n'est enregistré) |
| GET | `/api/dashboards/{id}` | | Une page ; **404** si inconnue |
| PUT | `/api/dashboards/{id}` | une page (voir ci-dessous) | Crée ou remplace ; l'identifiant vient de l'URL |
| DELETE | `/api/dashboards/{id}` | | Supprime ; jamais la dernière page (**400**) |

```json
{
  "name": "Confort", "order": 0,
  "sections": [
    { "id": "thermo", "title": "Thermostats", "collapsed": false,
      "cards": [ { "id": "a1", "widget": "thermostat", "entities": { "thermostat": "climate.salon" }, "cols": 3 } ] }
  ]
}
```

Une carte **suit son entité** quand elle n'a pas de `widget` : `{"id": "a2", "entity": "sensor.salon"}` prend le
widget choisi pour l'entité (champ `widget` du registre, réglé dans l'écran Entités) ou le plus précis, et lie seule
les entités du même appareil. Avec `widget`, ce sont ses `entities` qui comptent (WIDGETS.md §12).

Identifiant de page : minuscules, chiffres et `-`. Une carte est unique dans sa page (pas seulement dans sa
section). `cols` (1 à 12, en douzièmes d'un grand écran : 3 = quatre cartes par ligne) et `rows` (1 à 4) sont
facultatifs : sans eux, la taille du manifeste. La réponse porte `"grid": 12`, le nombre de colonnes dans lequel
`cols` est compté ; à l'envoi, `grid` est facultatif, mais toute autre valeur que 12 est refusée. Un widget ou une
entité disparus ne bloquent pas l'enregistrement : la carte affiche le widget « valeur » et un bandeau.
