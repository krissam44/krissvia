# Supervision

> Livré le 22/10/2026. `Krissvia.Core.Supervision` (`SupervisionRules`, fonction pure ; `SupervisionWatch`, délais de
> grâce et alertes), `SupervisionLoop` (toutes les minutes), page **Supervision** (`/supervision`), API
> `GET /api/supervision`. Les alertes passent par `AlertService` (`NOTIFICATIONS.md` §2) : une par incident, fin dite
> seulement si elle avait été signalée.

## 1. Ce qui est surveillé

| Famille | Règle | Délai | Clé |
|---|---|---|---|
| **Plugin arrêté** | plugin **activé** (page Services) et pas connecté au cœur ; lancé ailleurs et connecté, il compte comme connecté | 5 min (1 à 120) | `plugin:{id}` |
| **Connexion Internet** | `binary_sensor` de classe `connectivity` à faux — quelle que soit la box (`PRESENCE.md` §1). Indisponible : rien, c'est l'affaire du plugin | 5 min (1 à 120) | `internet:{entité}` |
| **Présence perdue** | appareils suivis sans réponse (`HomePresence`) : maison tenue pour occupée par sécurité | aucun | `presence` |
| **Espace disque** | libre à côté de la base sous le seuil — une carte SD ou un SSD plein arrête tout | 1 Go (100 Mo à 100 Go) | `disque` |
| **Capteur muet** | aucune mesure depuis le seuil, ou indisponible | 120 min (10 min à 48 h) | `capteur:{entité}` |
| **Problème signalé** | `binary_sensor` de classe `problem` à vrai : un plugin le dit (sous-tension d'un Raspberry…), le cœur n'a pas à le connaître. Début = premier constat (le plugin republie à chaque relevé) | aucun | `probleme:{entité}` |
| **Pile faible** | `battery` en % au seuil ou dessous ; en vrai/faux, à vrai. Fin au-dessus du seuil + 5 (marge) | 20 % | `pile:{entité}` |

Réglages : page Supervision (collection `alertes`, document `reglages`, à côté de l'alerte gel). Chaque page relit
le document au moment d'enregistrer : l'une n'écrase pas ce que l'autre vient de changer.

## 2. Règles reprises de DomoMud

- **Seuls les capteurs bavards peuvent être muets** : température, humidité, puissances, pression, luminosité, CO₂,
  ouvertures et détecteurs (qui publient un battement). Un interrupteur, une lumière, un bouton ou un libellé ne
  parlent qu'à un changement : deux jours de silence y sont normaux, et DomoMud noyait les vraies alertes sous celles-là.
- **Jamais entendu ne conclut rien** : pas de faux positif au premier démarrage.
- **Répit de dix minutes au démarrage** : les capteurs n'ont pas encore republié, tout serait « muet ».
- **Quarantaine** d'un cycle complet après le retour d'un capteur : un capteur à la limite basculerait sans fin.
- **Marge** de 5 points avant de dire une pile remplacée.

## 3. Écarts voulus

- **Les capteurs d'un plugin arrêté ne sont pas signalés un par un** : l'alerte du plugin les couvre, trente alertes
  « capteur muet » noieraient la seule qui compte.
- **Le début d'une panne est le premier constat**, quand la donnée ne le dit pas : une box republie « coupée » à chaque
  relevé, l'instant de la mesure ne dit pas depuis quand.
- **Une coupure Internet que l'alerte n'a pas pu dire est racontée une fois au retour.** Pendant la coupure, un
  serveur ntfy public est injoignable : l'alerte échoue et réessaie (`NOTIFICATIONS.md` §2) ; sans ce cas, elle
  partirait au retour suivie aussitôt de « rétablie ». Au retour, un seul message : « connexion coupée de 10:53 à
  10:56 (après 3 min) ». Un serveur ntfy sur le réseau local, lui, reste joignable : l'alerte part pendant la coupure.
- **Ce qui n'est plus surveillé est oublié sans message** (plugin désactivé, capteur supprimé, famille éteinte) :
  ce n'est pas un retour à la normale.

## 4. Délestage

`LoadSheddingAlerts`, appelé après chaque tick du délesteur (2 s) mais **à côté** de sa boucle : un ntfy injoignable
prend 10 s par essai, et la protection électrique ne doit jamais l'attendre.

| Alerte | Quand | Fin |
|---|---|---|
| **Délestage en cours** (DomoMud) | au moins un équipement coupé : puissance, seuil, liste | « terminé » quand tout est rétabli |
| **Coupure non confirmée**, par équipement | coupé, mais aucun état ne le confirme : il consomme peut-être encore, le disjoncteur peut sauter malgré le délestage | « coupure confirmée », ou « rétabli » |
| **Délestage sans mesure** | aucune puissance fraîche depuis 5 min : il ne décide plus rien (D6) | « mesure retrouvée » |

- L'épisode se lit sur la **liste des équipements coupés**, pas sur la bascule du tick : après un redémarrage en plein
  délestage, la bascule est passée, l'alerte doit être là quand même.
- **Quarantaine de 15 min** entre deux épisodes : une soirée de cuisine ne fait pas dix alertes.
- Délestage désactivé (ou cette famille éteinte, page Supervision) : les alertes en cours sont oubliées sans message.

## 5. API

`GET /api/supervision` : `checked_at`, `sensors_in_grace`, `items` (`key`, `category`, `name`, `problem`, `alerting`
— panne au-delà de son délai —, `detail`, `since`) et `alerts` (toutes les alertes en cours, gel compris).

## 6. Pas encore

- MQTT perdu (famille DomoMud) : le MQTT est l'affaire des plugins. Un plugin qui perd son broker reste connecté au
  cœur ; ses capteurs, indisponibles ou simplement silencieux, finissent signalés un par un comme muets. Une alerte
  unique « broker perdu » demanderait que le plugin le publie (une entité `connectivity` à lui, par exemple).
