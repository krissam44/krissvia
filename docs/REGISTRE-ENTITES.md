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

Le nom affiché **suit** le nom proposé par le plugin tant que l'utilisateur ne l'a pas changé (29/10/2026, schéma v7,
colonne `proposed_name`) : renommer un appareil dans Zigbee2MQTT ou dans les entités manuelles de MQTT Discovery renomme
la carte. Dès que l'utilisateur choisit un autre nom dans l'écran Entités, ce nom n'est plus jamais touché. Les entités
enregistrées avant le schéma v7 gardent leur nom : rien ne dit s'il avait été choisi. L'`entity_id`, lui, ne change pas.

**Appareil renommé.** Un appareil à l'identifiant nouveau dont toutes les entités déjà connues viennent d'un seul appareil
que la déclaration ne mentionne plus est cet appareil, renommé : il garde son identifiant interne et sa pièce. C'est le
cas des entités manuelles, dont l'appareil est identifié par son nom ; un identifiant matériel ne change jamais.

## Cycle de vie

- `declare` = instantané complet de l'intégration, appliqué en une transaction. Il est idempotent : redéclarer
  la même chose ne modifie rien et ne lève pas `Changed`.
- Une entité absente de l'instantané devient **manquante** (`missing`). Elle n'est pas supprimée, ses références
  restent résolubles, et elle redevient normale si le plugin la redéclare.
- Une déclaration invalide (même entité deux fois) est refusée entièrement : rien n'est écrit.
- **Supprimer** (23/10/2026) : seulement une entité **manquante** (bouton « Supprimer » de sa fenêtre dans l'écran
  Entités, ou `DELETE /api/entities/{id}`). Une entité encore déclarée reviendrait à la prochaine connexion de son
  plugin : refusée, la désactiver est le moyen de l'écarter (l'écran Entités masque les désactivées sur demande).
  Refusée aussi tant que quelque chose la vise — thermostat, programmation, délestage, alarme, réglages d'un plugin,
  automatisme, carte du tableau de bord — avec la liste de ces usages (`EntityRemoval`). Partent avec elle : ses
  alias, son dernier état, et son appareil s'il n'a plus d'autre entité. Son historique de mesures reste en base.

## Contrôle d'intégrité

Chaque module qui référence des entités implémente `IEntityReferenceSource` (propriétaire, rôle, `entity_id`).
`EntityIntegrityChecker` en tire la liste des problèmes : `Unknown` (n'existe pas), `Missing` (le plugin ne la
déclare plus), `Disabled` (désactivée par l'utilisateur). C'est le même contrôle pour tous les modules.

## Usage

Champ de l'utilisateur : à quoi sert l'entité, quand le plugin ne peut pas le savoir. Un plugin voit un relais ou
une prise ; l'utilisateur sait qu'il alimente un radiateur. Seul usage défini aujourd'hui : `radiateur`, pour un
interrupteur (`EntityUsages`), choisi dans le champ « Type » de l'écran Entités. Il donne la catégorie
« Radiateur » (icône et couleur propres), place l'interrupteur en tête des radiateurs proposés à un thermostat, et
lui donne sa section dans « Proposer ». Les widgets peuvent cibler un usage (`usages` d'un slot, WIDGETS.md §3).

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
