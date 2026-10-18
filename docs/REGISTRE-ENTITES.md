# Registre d'entités

Le registre est la **seule source de vérité** sur « quelles entités existent ». Thermostats, délestage,
automatismes et dashboards le référencent par `entity_id`, jamais par topic ni par nom.

## Identité

- Le plugin fournit un `unique_id` immuable (adresse IEEE, MAC…). Le registre attribue un `entity_id`
  lisible et **définitif** : `switch.prise_salon`, `sensor.prise_salon_power`.
- Forme : `{famille}.{slug}`. Le slug vient du nom de l'appareil (+ nom ou classe de l'entité). En cas de
  collision : `_2`, `_3`…
- Le `entity_id` ne change **jamais**, même si l'appareil ou l'entité est renommé. Pas de renommage d'identifiant
  en v0 : il faudrait réécrire toutes les références.

## Qui possède quoi

| Plugin (mis à jour à chaque `declare`) | Utilisateur (jamais écrasé) |
|---|---|
| famille, classe, unité, `state_class`, commandes, données `extra`, fabricant, modèle | nom affiché, usage, activée/désactivée, pièce |

Le nom proposé par le plugin n'est utilisé qu'à la **création**.

## Cycle de vie

- `declare` = instantané complet de l'intégration, appliqué en une transaction. Il est idempotent : redéclarer
  la même chose ne modifie rien et ne lève pas `Changed`.
- Une entité absente de l'instantané devient **manquante** (`missing`). Elle n'est pas supprimée, ses références
  restent résolubles, et elle redevient normale si le plugin la redéclare.
- Une déclaration invalide (même entité deux fois) est refusée entièrement : rien n'est écrit.

## Contrôle d'intégrité

Chaque module qui référence des entités implémente `IEntityReferenceSource` (propriétaire, rôle, `entity_id`).
`EntityIntegrityChecker` en tire la liste des problèmes : `Unknown` (n'existe pas), `Missing` (le plugin ne la
déclare plus), `Disabled` (désactivée par l'utilisateur). C'est le même contrôle pour tous les modules.

## Widget

Champ de l'utilisateur (schéma v5, colonne `widget`) : le widget des cartes « automatiques » du tableau de bord
(WIDGETS.md §12). Null : le plus précis, choisi par le catalogue. Comme la pièce ou la puissance, jamais écrasé par
un plugin.

## Entités du cœur

Un module du cœur peut publier ses propres entités, comme un plugin : les thermostats sont des entités `climate`
de l'intégration `coeur.thermostats` (16/10/2026). Mêmes règles que pour un plugin (déclaration complète,
entité absente marquée manquante, champs de l'utilisateur conservés), avec deux différences :

- le **nom** est celui du thermostat, fixé par sa configuration ;
- les **ordres** ne partent vers aucun plugin : `CoreCommandRouter` les remet au module, après les contrôles
  et avant le journal du bus de commandes, comme tout ordre.

Les intégrations du cœur commencent par `coeur.` ; le point étant interdit dans l'identifiant d'un plugin, aucun
plugin ne peut publier sous ce nom ni effacer ces entités par un `declare`.

## Alias

Un alias associe un ancien identifiant à une entité (ex. `domomud:topic:zigbee2mqtt/Prise salon`). Il sert à
l'import depuis DomoMud. Un alias déjà pris par une autre entité est refusé.

## Concurrence et persistance

- Un verrou d'écriture unique, jamais imbriqué ; `Changed` est levé hors verrou.
- Instantané en mémoire, remplacé seulement après une écriture réussie : la résolution d'une entité à chaque
  mesure reçue ne touche pas SQLite.
- SQLite : une connexion par opération, WAL, `foreign_keys` activé, version de schéma dans `PRAGMA user_version`.
