# Présence du foyer et supervision de la connexion

> Livré le 19/10/2026. Plugin `Krissvia.Plugin.Livebox`, module du cœur `Krissvia.Climate.Presence` (`HomePresence`,
> `PresenceLoop`), widget standard `presence`.

## 1. Deux choses indépendantes, et indépendantes de la box

La box Internet sait deux choses utiles : quels téléphones sont connectés (la **présence**) et l'état de la
connexion (la **supervision**). Elles ne servent pas aux mêmes fonctions et ne doivent pas dépendre du fournisseur :

| | Publié par un plugin de box | Consommé par le cœur |
|---|---|---|
| Présence | un `binary_sensor` de classe `presence` par appareil suivi | `HomePresence` → `sensor.maison_presence` ; demain l'absence programmée et l'alarme |
| Supervision | `connectivity` (lien établi), `public_ip`, `data_rate` (kbit/s) | la supervision (étape suivante de la feuille de route) |

Changer de fournisseur (Free, SFR…) = un autre plugin qui publie les mêmes classes : la présence du foyer, l'alarme
et la supervision ne changent pas. Le plugin Livebox a un interrupteur « Superviser la connexion » : la présence
marche sans.

## 2. Plugin Livebox

API locale « sysbus » (comme DomoMud) : `createContext` avec `Authorization: X-Sah-Login`, puis `X-Context` et le
cookie de session ; une seule réauthentification si la session expire (« Permission denied »). **Lecture seule** :
`Devices:get`, `NMC:getWANStatus`, `NeMo.Intf.dsl0:getMIBs`.

- Réglages (page Services) : adresse, identifiant, mot de passe administrateur, intervalle (60 s), délai d'absence
  (20 min), supervision, et la liste des **appareils suivis** (adresse MAC, nom).
- Le journal du plugin liste les appareils que voit la box, à chaque changement : c'est là qu'on trouve l'adresse
  MAC des téléphones.
- Un appareil suivi est présent s'il est connecté, ou l'a été depuis moins du délai : un téléphone en veille quitte
  le Wi-Fi sans quitter la maison. Son retour compte aussitôt.
- Trois relevés ratés de suite (box injoignable, mot de passe refusé) : toutes les entités deviennent indisponibles.

## 3. Présence du foyer

`HomePresence` regarde **tous** les `binary_sensor` de classe `presence` (activés, non manquants), quelle que soit
leur source, et publie toutes les 10 s, s'il a changé, `sensor.maison_presence` (classe `home_presence`) :
`occupied`, `detection_lost`, `tracked`, `present` / `absent` / `unknown` (noms), `empty_since`.

| Situation | Maison |
|---|---|
| quelqu'un de présent | occupée |
| aucun appareil suivi | occupée (on ne prétend pas savoir) |
| personne de présent, un capteur sans valeur | **occupée par sécurité**, `detection_lost` |
| tous absents | vide, depuis le premier constat |

Le repli « occupée » est celui de DomoMud (02/10/2026) : chauffer une maison vide quelques heures coûte des centimes,
une détection figée sur « vide » laisserait le chauffage en hors-gel jusqu'au retour dans le froid. La carte
« Présence » affiche « INCONNUE » et le dit.
