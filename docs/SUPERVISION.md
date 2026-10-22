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

## 4. API

`GET /api/supervision` : `checked_at`, `sensors_in_grace`, `items` (`key`, `category`, `name`, `problem`, `alerting`
— panne au-delà de son délai —, `detail`, `since`) et `alerts` (toutes les alertes en cours, gel compris).

## 5. Pas encore

- Proxmox (machines virtuelles) : dans DomoMud un service du cœur ; dans Krissvia ce sera un plugin, qui publiera des
  entités que cette supervision surveillera comme les autres.
- **Délestage en cours** (famille DomoMud) : pas encore notifié dans Krissvia.
- MQTT perdu (famille DomoMud) : le MQTT est l'affaire des plugins. Un plugin qui perd son broker reste connecté au
  cœur ; ses capteurs, indisponibles ou simplement silencieux, finissent signalés un par un comme muets. Une alerte
  unique « broker perdu » demanderait que le plugin le publie (une entité `connectivity` à lui, par exemple).
