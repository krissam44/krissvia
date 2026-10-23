# Historique des entités

> Livré le 22/10/2026. `Krissvia.Core.History` (règles, en fonctions pures), `SqliteHistoryRepository` (schéma v6),
> boucle `HistoryLoop`, API `GET /api/entities/{id}/history`, `kv.history` des widgets. Sert de base aux courbes de
> l'interface et à l'auto-TPI, qui identifie le comportement thermique d'une pièce sur plusieurs jours.

## 1. Ce qui est gardé

Toutes les entités, sans réglage : il n'y a rien à cocher pour qu'une courbe existe le jour où on la cherche.

| Valeur de l'entité | Série(s) | Exemple |
|---|---|---|
| nombre | l'entité | `sensor.salon_temperature` |
| vrai / faux | l'entité, en 1 / 0 | `switch.radiateur_salon` |
| lumière `{"on": …}` | l'entité, en 1 / 0 | `light.lampe_salon` |
| objet | un champ par série, parmi `temperature`, `target`, `power`, `outdoor_temperature`, `humidity`, `heating` | `climate.salon:temperature`, `climate.salon:power` |
| texte | rien | tarif `HC`, état d'une machine |

En booléen, la moyenne d'une heure est la **part du temps** où c'était vrai : 0,25 pour un radiateur allumé un quart
d'heure. D'un objet, seuls les champs listés : ceux d'un thermostat (dont l'extérieur et la puissance TPI, qu'il
faut à l'auto-TPI) et la puissance d'un délesteur. Une machine virtuelle ou l'alarme ne deviennent pas vingt
courbes que personne ne regarde.

## 2. Densité

- **Un point par changement, au plus un par minute et par série.** Un Linky publie toutes les 2 s : 43 000 points
  par jour pour une courbe qu'on regarde à la minute rempliraient la carte SD du Pi. Dans la minute qui suit un point,
  un changement attend ; s'il y en a plusieurs, le dernier gagne, avec son propre instant. C'est le **rythme
  d'écriture** qui est borné, pas l'écart entre deux instants : deux points peuvent être à 16 s l'un de l'autre si
  chacun a attendu son tour, la courbe disant quand la valeur a changé.
- **Une valeur inchangée ne s'écrit pas**, sauf toutes les **30 min** (battement) : une courbe plate se distingue
  ainsi d'un capteur qui ne publie plus. Un capteur qui republie sans cesse la même valeur garde ce battement.
- **Une entité injoignable écrit un trou** (valeur nulle) dans chacune de ses séries, plutôt qu'une dernière valeur
  qui s'étirerait jusqu'à son retour. Un trou n'a pas de battement.
- Les points partent par lots toutes les 30 s (et à l'arrêt), jamais une écriture par mesure ; un lot qui échoue
  repart au passage suivant.

Ordre de grandeur : une sonde de température, quelques centaines de points par jour ; un Linky, 1 440.

## 3. Conservation

| Table | Contenu | Gardé |
|---|---|---|
| `history_points` | points bruts (`series_id`, `ts` en secondes Unix, `value`) | **30 jours** |
| `history_hourly` | une ligne par heure : moyenne **pondérée par le temps**, min, max, dernière valeur, couverture | sans limite |
| `history_series` | la clé texte d'une série, écrite une fois | — |

- La moyenne d'une heure suit la série en escalier (une valeur vaut jusqu'au point suivant) en partant de la valeur
  en vigueur au début de l'heure ; un trou ne compte ni dans la moyenne ni dans la **couverture** (part de l'heure où
  la valeur était connue).
- Consolidation toutes les 10 min, depuis la dernière heure consolidée (l'heure en cours est recalculée jusqu'à
  « maintenant ») ; une base neuve ou longtemps arrêtée est rattrapée au premier passage.
- **La consolidation passe avant la purge**, et la purge ne descend jamais sous la dernière heure consolidée :
  purger d'abord effacerait des points avant de les avoir totalisés (DomoMud, `conso_jour`).
- Les heures sont des heures UTC : en France, une journée locale tombe sur des heures entières, été comme hiver.

## 4. Lire une courbe

`GET /api/entities/{id}/history?from=…&to=…&field=…&resolution=…`

- `from` / `to` : ISO 8601 ; par défaut les dernières 24 h. 400 jours au plus.
- `field` : le champ d'un objet (`temperature`, `target`, `power`…). Sans lui, sur un thermostat, la réponse 400
  liste les champs disponibles.
- `resolution` : `raw` (points bruts, 3 jours au plus) ou `hour` ; par défaut `raw` jusqu'à 3 jours, `hour` au-delà.

```json
{ "entity_id": "climate.salon", "field": "temperature", "resolution": "raw", "from": "…", "to": "…",
  "points": [ { "t": "2026-10-22T09:58:12+00:00", "v": 19.2 }, { "t": "…", "v": null } ] }
```

En `hour`, chaque point porte aussi `min`, `max`, `last` et `coverage`, et `v` est la moyenne. En `raw`, le premier
point peut précéder `from` : c'est la valeur en vigueur au début de la fenêtre.

Pour un widget : `kv.history(slot, from, to, { field })` rend `[{ value, measured_at, min, max }]` (WIDGETS.md §5).

## 5. Les courbes de l'interface (23/10/2026)

Une fenêtre **Historique**, comme la courbe des thermostats de DomoMud. On l'ouvre depuis l'icône 📈 d'une ligne de
l'écran Entités, le bouton 📈 d'une carte thermostat, ou un clic sur une carte de mesure (température, puissance,
Linky, énergie, ouverture, valeur).

- **Périodes** : 6 h (une case = 5 min), 24 h (15 min), 7 j (1 h), 30 j (6 h). Jusqu'à 3 jours les points bruts, au-delà
  les heures consolidées.
- **Une grille régulière**, quelle que soit la densité des points (`HistoryCurve`, pur et testé) : la moyenne d'une case
  est **pondérée par le temps** passé à chaque valeur (la série est en escalier) ; un trou ne compte pas, et une case
  sans valeur reste vide, la courbe s'interrompt au lieu de relier le capteur à son retour ; la dernière valeur court
  jusqu'à « maintenant », pas au-delà. Les heures consolidées pèsent par leur couverture.
- **Ce qui est tracé** dépend de l'entité : un thermostat montre ambiante (avec sa bande min / max), consigne en
  pointillés et extérieure, puis sa puissance TPI en barres (à défaut, la part du temps en chauffe) ; une mesure, sa
  courbe et sa bande ; un interrupteur ou un capteur tout-ou-rien, la part du temps où il était actif, en barres, et la
  durée cumulée. Des tuiles résument la période ; survoler une case donne ses valeurs.
- Tracé en SVG (`CourbeSvg`), comme DomoMud : `MudChart` ne sait pas laisser un trou dans une courbe.
- Un widget ouvre la fenêtre par `kv.openHistory(slot)` (WIDGETS.md §5) ; un widget de mesure le fait d'un clic s'il
  déclare `static historique = "slot"` (base `commun.js`).

## 6. Pas encore

- Consommation par jour d'un compteur (index croissant) : se déduira des heures (`last` de chaque heure), à écrire
  avec l'écran Électricité.
- Widgets Blazor : `WidgetContext` n'a pas encore d'équivalent de `kv.history`.
