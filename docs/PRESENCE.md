# Présence du foyer et supervision de la connexion

> Livré le 19/10/2026. Plugin `Krissvia.Plugin.Livebox`, module du cœur `Krissvia.Climate.Presence` (`HomePresence`,
> `PresenceLoop`), widget standard `presence`.

## 1. Deux choses indépendantes, et indépendantes de la box

La box Internet sait deux choses utiles : quels téléphones sont connectés (la **présence**) et l'état de la
connexion (la **supervision**). Elles ne servent pas aux mêmes fonctions et ne doivent pas dépendre du fournisseur :

| | Publié par un plugin de box | Consommé par le cœur |
|---|---|---|
| Présence | un `binary_sensor` de classe `presence` par appareil suivi | `HomePresence` → `sensor.maison_presence` ; l'absence (§5), demain l'alarme |
| Supervision | `connectivity` (lien établi), `public_ip`, `data_rate` (kbit/s) | la supervision (`SUPERVISION.md`) : alerte « connexion coupée » |

Changer de fournisseur (Free, SFR…) = un autre plugin qui publie les mêmes classes : la présence du foyer, l'alarme
et la supervision ne changent pas. Le plugin Livebox a un interrupteur « Superviser la connexion » : la présence
marche sans.

## 2. Les deux objets que publie une box (contrat pour tout plugin de box)

Les cartes « Connexion Internet » et « Appareils du réseau » se lient à une **classe**, pas à la Livebox : un plugin
Freebox qui publie les mêmes objets a les mêmes cartes, sans rien changer au cœur ni aux widgets.

**`internet_connection`** (sensor, valeur objet) — un champ absent ou `null` n'est pas montré (pas de DSL sur la fibre) :

```json
{ "up": true, "link_type": "vdsl", "public_ip": "203.0.113.7",
  "down_kbps": 34200, "up_kbps": 5300, "down_max_kbps": 34900, "up_max_kbps": 5300,
  "snr_down_db": 6.4, "snr_up_db": 6.4, "attenuation_down_db": 31.6, "attenuation_up_db": 53.2, "profile": "8b" }
```

**`network_hosts`** (sensor, valeur objet), commande **`set_tracked`** `{"mac": "AA:BB:…", "tracked": true}` :

```json
{ "hosts": [ { "mac": "AA:BB:CC:DD:EE:01", "name": "Pixel", "ip": "192.168.1.20", "type": "Mobile", "active": true, "tracked": true } ] }
```

Suivre un appareil, c'est publier pour lui un `binary_sensor` de classe `presence` (section 4). Le plugin garde la
liste des suivis dans son **dossier de données** (`Krissvia__DataDirectory`, que le cœur crée pour chaque plugin à côté
de la base : `plugins-data/{id}`), pas dans ses réglages : on la change depuis la grille.

## 3. Plugin Livebox

API locale « sysbus » (comme DomoMud) : `createContext` avec `Authorization: X-Sah-Login`, puis `X-Context` et le
cookie de session ; une seule réauthentification si la session expire (« Permission denied »). **Lecture seule** :
`Devices:get`, `NMC:getWANStatus`, `NeMo.Intf.dsl0:getMIBs`.

- Réglages (page Services) : adresse, identifiant, mot de passe administrateur, intervalle (60 s), délai d'absence
  (20 min), supervision. Les **appareils suivis** se cochent dans la grille « Appareils du réseau » ; le réglage
  « Appareils suivis (départ) » n'est lu qu'au premier démarrage.
- Le journal du plugin liste les appareils que voit la box, à chaque changement : c'est là qu'on trouve l'adresse
  MAC des téléphones.
- Un appareil suivi est présent s'il est connecté, ou l'a été depuis moins du délai : un téléphone en veille quitte
  le Wi-Fi sans quitter la maison. Son retour compte aussitôt.
- Trois relevés ratés de suite (box injoignable, mot de passe refusé) : toutes les entités deviennent indisponibles.

## 4. Présence du foyer

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

## 5. Absence

> Livré le 19/10/2026. `Krissvia.Climate.Presence.AbsenceService` (collection `absence`, document `maison`), page
> `/absence`, API `/api/absence`.

**Qui décide** (C9, hiérarchie de DomoMud) : le forçage manuel (présent ou absent, jusqu'au retour en automatique),
sinon une absence programmée en cours, sinon la détection de présence (§4). Sans détection encore calculée, la maison
est occupée.

**Absence programmée** (C8) : du départ au retour, enregistrée — un redémarrage en pleine absence la retrouve.
- Elle s'efface à la date de retour.
- « Rentrer plus tôt annule l'absence » n'agit qu'une fois le **départ constaté** (maison vue vide pendant la
  période), sinon programmer son départ depuis son salon l'annulerait au calcul suivant. Le constat est enregistré.
- Seul quelqu'un de réellement **présent** l'annule. Une détection perdue rend la maison « occupée » par sécurité,
  mais une box injoignable au milieu des vacances n'est pas un retour : l'annulation ferait chauffer la maison vide
  jusqu'à la date prévue. (Écart voulu avec DomoMud.)

**Thermostats** : chacun choisit de suivre l'absence (onglet « Températures », faux par défaut) et son mode
d'absence (hors-gel par défaut). Maison vide, ce mode remplace planning et consigne ajustée (un « +0,5 » laissé avant
de partir ne chauffe pas la maison vide), et le préchauffage de planning ne s'applique pas. Avant un **retour
programmé**, le préchauffage anticipé remonte la pièce à la consigne de l'heure d'arrivée (mode du planning à cette
heure-là, ou mode manuel), avec la pente apprise corrigée par la prévision, **sans** le plafond de 3 h des créneaux :
remonter du hors-gel en demande souvent plus. Un mode d'absence Off n'empêche pas cette remontée ; un Off choisi hors
absence reste ferme. L'état publié du thermostat porte `away` ; sa carte affiche « Absence ».

| API | |
|---|---|
| `GET /api/absence` | `state` (`away`, `source` : `none`, `detection`, `scheduled`, `manual` ; `return`), `period`, `forced`, `presence` |
| `PUT /api/absence/period` | `{"departure": "…", "return": "…", "cancel_on_presence": true}` (dates ISO 8601 avec fuseau) |
| `DELETE /api/absence/period` | supprime la période |
| `PUT /api/absence/forced` | `{"away": true \| false \| null}` (null : automatique) |
