# Krissvia — Architecture (brouillon v0)

> Statut : proposition à valider. Tout ce qui est marqué **[à décider]** attend un avis.
> Source : analyse des services et modèles de DomoMud, et des échanges de conception.

## 1. Objectif

Krissvia est une plateforme domotique en trois blocs, successeur de DomoMud. Elle reprend ce qui
fonctionne dans DomoMud (moteurs de chauffage, délestage, alarme, présence) et sépare ce qui
était monolithique.

| Bloc | Rôle | Langage |
|---|---|---|
| **Cœur** | registre d'entités, états, bus d'événements et de commandes, moteurs, API publique | C# |
| **Plugins** | intégrations : parlent à un équipement ou à un service et publient des entités | C# ou Python, **processus séparés** |
| **Frontend** | tableau de bord personnalisable, widgets associés aux entités | application web (Web Components) |

## 2. Principes

1. **Un plugin est un processus séparé.** Aucun code tiers dans le cœur.
2. **Un plugin fournit des mesures et exécute des ordres. Il ne régule jamais.** Thermostat, délestage,
   alarme et automatismes sont des moteurs du cœur, qui consomment des entités abstraites.
3. **Plugin mort ⇒ entités `unavailable` ⇒ les failsafes du cœur prennent le relais.**
4. **Le registre est la seule source de vérité sur les entités.** Tout se référence par `entity_id`,
   jamais par topic ni par nom (cause de l'incident « Chbre » / « Chambre » du 03/10/2026).
5. **Une mesure absente n'est pas zéro.** Une valeur périmée ou manquante est un état explicite.
6. **Un ordre de sécurité part immédiatement et se vérifie.** Voir §7.

## 3. Les quatre contrats

| # | Contrat | Statut |
|---|---|---|
| 1 | Intégration plugin ↔ cœur (messages JSON sur WebSocket) | v0.2 en cours : `CONTRACT-V0.2-PROPOSITION.md`, transport dans `TRANSPORT.md` |
| 2 | Services sortants (notifications, publication vers Home Assistant…) : capacités appelées par le cœur | à écrire |
| 3 | API publique cœur ↔ frontend (REST + abonnement WebSocket ; clé en en-tête, comparaison en temps constant) | commencée : thermostats et délestage, `API.md` |
| 4 | Manifeste de widget : types d'entités et de plugins que le widget sait afficher | à écrire |

## 4. Le cœur

- **Registre d'entités** : déjà écrit (`Krissvia.Core.Registry`). Identité `entity_id` définitive, champs
  plugin / champs utilisateur séparés, entités absentes marquées `missing`.
- **Magasin d'états** : dernière valeur **et horodatage réel** par entité, persisté. Après un redémarrage,
  une mesure garde sa vraie date (sinon tout contrôle de fraîcheur devient aveugle). Structures
  concurrentes dès le départ.
- **Bus d'événements interne** (`Krissvia.Core.Events.EventBus`, fait le 15/10/2026) : remplace les
  `OnStateChanged` par service. Chaque abonné a sa file et sa boucle : `Publish` ne bloque jamais et
  n'appelle aucun abonné sur le fil du publieur (le gel du circuit par `HaDiscoveryService` dans DomoMud),
  une exception est signalée sans priver les autres abonnés. Files non bornées : perdre un changement
  casserait un déclencheur sur front ; `Diagnostics()` montre un abonné en retard. Événements :
  `EntityStateChanged` (avec l'état **précédent** ; la première valeur n'est pas un front),
  `RegistryChanged`, `PluginConnectionChanged`, `CommandCompleted`.
- **Bus de commandes** (`Krissvia.Core.Commands.CommandBus`, fait le 15/10/2026) : passage unique de tous
  les ordres, dont le PluginHost n'est que le transport (`ICommandDispatcher`). Toute commande renvoie un
  résultat (`accepted`, `failed`, `unreachable`). Entité inconnue ou commande non déclarée ⇒ `failed`
  sans rien envoyer ; entité désactivée ⇒ refus des ordres normaux, **jamais** d'un ordre de sécurité.
  Journal des 200 derniers ordres, publiés sur le bus d'événements. `SendVerifiedAsync` applique le §7.3 :
  seul un état publié **après** l'ordre le confirme, l'état déjà connu n'étant que la croyance du cœur.
- **Entités calculées** : moyennes de sondes (`avg/…` de DomoMud), somme des équipements, consommations
  jour/semaine/mois, coûts. Elles passent en `unavailable` quand leurs entrées manquent, elles ne
  disparaissent jamais.
- **Moteurs** : Climate (thermostats TPI), Délestage, Alarme, Automatismes, Présence et Absence, Énergie.

## 5. Partage cœur / plugins

| DomoMud | Krissvia |
|---|---|
| Thermostat TPI, auto-TPI, planning, absence | **cœur**, module Climate |
| Délesteur | **cœur**, module Délestage |
| Alarme (machine à états, codes, verrou, journal) | **cœur**, module Alarme |
| Automatismes, conditions, déclencheurs | **cœur** |
| Alertes (déduplication, quarantaine) | **cœur** |
| Zigbee2MQTT, MQTT générique (+ modèles Theengs, Shelly), RFXCom | plugins |
| Shelly HTTP (relais, EM) | plugin |
| Linky (trame MQTT) | plugin |
| Livebox (présence, WAN), Proxmox, Raspberry, météo, Tempo | plugins (produisent des entités) |
| ntfy, publication Home Assistant | services sortants (contrat 2) |
| openHASP (platines) | plugin à deux sens (entités + affichage) |

## 6. Énergie : le compteur logique

Les plugins publient ce qu'ils savent mesurer, en unités standard (pince : W, éventuellement A, V, kWh ;
Linky : VA, index par tarif, tarif courant). Le cœur définit un **compteur logique** lié à des
entités par rôle :

| Rôle | Obligatoire | Consommé par |
|---|---|---|
| `puissance_instantanee` (VA de préférence, sinon W) | oui | délestage, widget de puissance |
| `index_energie` (total croissant) | non | consommations, coûts |
| `tarif_courant` | non | coût par tarif, heures creuses |

- Avec un index : consommation = différence d'index. Sans index : intégration de la puissance, marquée
  « estimée ».
- Le moteur reçoit une valeur `(valeur, unité, horodatage, qualité)`, qualité ∈ {apparente, active, estimée}.
  Une mesure en W est signalée « approchée » ; aucune conversion silencieuse.
- La fraîcheur se juge avec `expected_interval_seconds` déclaré par le plugin (Linky : 2 s).
- Installation monophasée : pas de rôle par phase.
- Le délestage a trois sources, ramenées à deux : une entité de puissance liée, ou la somme des
  équipements (mesure réelle si publiée, sinon puissance nominale).
- Implémentation : `IEntityStateReader` (dans `Core`, que le magasin d'états implémentera),
  `EntityPowerSource`, `EquipmentSumPowerSource` et `PowerReadings.ToReading` (dans
  `Krissvia.Climate.LoadShedding`). Une mesure en W est majorée d'un facteur ≥ 1 réglable avant d'être
  comparée aux seuils en VA.

## 7. Ordres de sécurité

1. Un ordre de sécurité (`priority: safety`) n'attend aucun tick : il est émis tout de suite par le module
   qui le décide.
2. Un drapeau « coupure envoyée » n'est posé qu'après un résultat `accepted`. Un échec est retenté.
3. Une coupure est **vérifiée** : l'état résultant de l'entité (ou la baisse de puissance) la confirme,
   le résultat de la commande seul ne suffit pas.
4. Une coupure échouée ne compte pas comme économie de puissance : l'équipement suivant est délesté.
5. Une protection coupe sans condition, même si le moteur croit que la charge est déjà éteinte.
6. Ces règles valent pour : délestage, fenêtre ouverte, sonde perdue, sirène.

## 8. Module Délestage ↔ Climate

DomoMud passait par un drapeau lu au tick suivant du thermostat (10 s). Dans Krissvia :

1. Le module Climate expose des **inhibitions** par thermostat, identifiées par un motif :
   `delestage`, `fenetre_ouverte`, `sonde_perdue`. Tant qu'une inhibition existe, le thermostat ne chauffe pas.
2. Le délestage appelle `Climate.Inhiber(thermostat, "delestage")`. L'appel, dans l'ordre :
   pose l'inhibition (verrou du thermostat), **puis** émet immédiatement la coupure sur tous ses switches,
   **puis** retourne le résultat.
3. La consigne, le mode, le planning et l'intégrale du thermostat **ne sont pas modifiés**.
4. À la restauration : `Climate.Lever(thermostat, "delestage")`. Le thermostat reprend avec la consigne
   **du moment**, y compris si l'utilisateur ou le planning l'a changée entre-temps.
5. Un équipement non lié à un thermostat est coupé et rétabli directement.
6. L'état de délestage n'est pas persisté : après un redémarrage, il est recalculé depuis la mesure.

**[décidé le 14/10/2026]** Le choix initial était de modifier la consigne puis de la restaurer. Les
inhibitions ont été retenues : une consigne modifiée et persistée doit être restaurée (consigne perdue si le serveur
redémarre pendant le délestage, écrasement d'un changement fait entre-temps, état hors-gel qui
ressemble à un réglage voulu). Une inhibition en mémoire se corrige seule au redémarrage. Si tu veux
néanmoins que la consigne affichée baisse pendant le délestage, c'est un choix d'affichage, sans
conséquence sur la régulation.

**[décidé]** Trois comportements de DomoMud sont corrigés (voir `TESTS-CARACTERISATION.md`, D4 à D6) :
sur-délestage avant que la mesure ait baissé, restauration groupée sans marge, mesure absente lue comme 0.
Implémentation : `Krissvia.Climate.LoadShedding.LoadShedder`.

## 9. Module Alarme : la sirène

La sirène ne sonne plus d'un seul tenant. Réglages (par installation) :

| Réglage | Plage | Défaut proposé |
|---|---|---|
| `duree_sonnerie_secondes` | **10 à 30** | 30 |
| `nombre_sonneries` | 1 à 10 (proposition) | 3 |
| `pause_entre_sonneries_secondes` | 5 à 120 (proposition) | 30 |

Le « 0 = illimité » de DomoMud disparaît : les trois valeurs sont bornées à l'enregistrement.

Déroulement, dans l'état `Declenchee` (qui dure jusqu'au désarmement) :

1. Sonnerie 1 : sirène ON pendant la durée réglée.
2. Pause (sirène OFF), puis sonnerie suivante, jusqu'à `nombre_sonneries`.
3. Après la dernière : sirène OFF, l'état reste `Declenchee` (c'est lui qui raconte l'intrusion).
4. Désarmement : sirène OFF **sans condition** (priorité `safety`, vérifiée comme au §7).
5. Un nouveau capteur qui détecte pendant `Declenchee` n'est pas relancé (comportement actuel conservé).

L'état de la machine reçoit deux champs, `SonnerieEnCours` (n°) et `EnSonnerie` (sonne ou pause) ;
le compteur existant sert de minuterie. La machine reste une fonction pure, testable avec une
horloge simulée. Les deux sirènes (intérieure, extérieure) suivent le même programme.

## 10. Frontend

- Application web qui ne parle qu'à l'API publique (REST + WebSocket).
- **Widgets en Web Components**, associés par règles : `kind` + `device_class` + usage choisissent le widget
  par défaut ; un plugin peut en livrer ; l'utilisateur peut en changer.
- Disposition : JSON en base validé par JSON Schema, éditée visuellement ; une vue YAML en import/export.
- Menu construit à partir des manifestes de plugins (clés stables).
- Blazor reste possible pour les écrans d'administration.
- Modèle de confiance des widgets : code de confiance installé par l'administrateur, à documenter.

## 11. Feuille de route

1. ~~Contrat v0.2, `PluginHost` et transport (WebSocket)~~ — fait le 15/10/2026 (`TRANSPORT.md`).
2. ~~Plugin Zigbee2MQTT interne, avec un simulateur pour les tests~~ — fait le 15/10/2026, reste à valider sur un vrai réseau.
3. ~~Cœur : magasin d'états, bus d'événements, bus de commandes~~ — fait le 15/10/2026. Reste à faire
   passer les coupures du délesteur et des inhibitions par `SendVerifiedAsync` (§7, règles 2 et 3) — fait
   le 15/10/2026 : une coupure ne compte (économie, inhibition soldée) qu'une fois confirmée par l'état,
   délai `CutVerificationTimeout` (5 s) ; le délesteur coupe par vagues parallèles.
4. Port des moteurs, chacun précédé de ses tests de caractérisation : Climate (régulation des thermostats
   faite le 15/10/2026, voir TESTS-CARACTERISATION.md pour ce qui reste), Délestage, Présence,
   Alarme, Automatismes.
5. API publique et premier frontend avec les widgets de base. Commencé le 15/10/2026 : page **Services**
   (Blazor + MudBlazor, `/parametres/services`) — une ligne par plugin installé (`plugin.json` à côté de l'exécutable), statut, interrupteur,
   fiche avec journal. Le cœur lance lui-même les plugins activés (désactivés par défaut), avec un jeton neuf à
   chaque lancement, les relance après un plantage (2 s à 1 min) et refuse d'en lancer un déjà connecté
   ailleurs. Réglages : chaque `plugin.json` les décrit (sous-ensemble de JSON Schema), la fiche du service en
   tire un formulaire, et le cœur les transmet **au lancement** par variables d'environnement (prioritaires sur
   `appsettings.Local.json`), puis relance le plugin s'ils changent — plus simple et plus sûr qu'une
   reconfiguration à chaud. Le message `configure` reste pour les plugins d'une autre machine.
   **Authentification** (15/10/2026) : toute l'interface exige une connexion, en développement comme en
   production. Comptes dans la collection `comptes` (PBKDF2-SHA256, 100 000 itérations, sel par compte) ; le
   premier se crée sur `/installation` avec un jeton à usage unique écrit dans le journal du serveur, tiré
   seulement tant qu'aucun compte n'existe — sinon le premier venu du réseau créerait le compte avant le
   propriétaire. Verrou global : 5 échecs, puis une minute de refus, quel que soit l'identifiant tapé. On ne
   supprime ni son propre compte ni le dernier. Connexion et installation sont des formulaires HTML simples
   (le cookie se pose dans une réponse HTTP, pas dans un circuit Blazor), protégés par le jeton antiforgery ;
   l'adresse de retour n'est suivie que si elle est locale. Cookie `krissvia` (HttpOnly, SameSite=Lax,
   14 jours glissants) ; clés Data Protection dans `keys/`, à côté de la base, pour qu'un redémarrage ne
   déconnecte personne — ce dossier se protège comme la base. Une session ouverte vaut la clé d'API sur
   `/api` et `/debug`.
   **Écran Thermostats** (15/10/2026, page d'accueil `/`) : une carte par thermostat (température, consigne
   régulée, statut, puissance, motifs de coupure, boutons de mode, ±0,5 °C, planning) et une fiche de
   configuration dont les listes ne proposent que des entités du registre de la bonne famille. Les boutons passent
   par `ThermostatService`, comme l'API. Un changement de configuration réveille la boucle du moteur
   (`ThermostatService.Changed`, levé verrou rendu) : un mode choisi atteint le radiateur tout de suite au lieu
   d'attendre jusqu'à 10 s. **Éditeur de planning** (`/thermostats/{id}/planning`) : créneaux par jour, mode
   hors créneau, copie d'un jour vers la semaine, le week-end ou les jours ouvrés, et un aperçu de la semaine
   calculé en interrogeant le planning lui-même quart d'heure par quart d'heure — il montre ce que fera le moteur,
   créneaux de nuit et chevauchements compris. Un créneau de nuit (22:00 → 06:00) finit le LENDEMAIN matin
   (écart voulu avec DomoMud, C11). Les chevauchements sont signalés (le plus matinal l'emporte, C12) ;
   un créneau de durée nulle est refusé. L'enregistrement ne remplace que le planning
   (`SetScheduleAsync`, `PUT /api/thermostats/{id}/schedule`). **Écran Délestage** (`/delestage`) : état en direct (jauge
   avec les deux seuils, source utilisée, absence de mesure signalée — jamais lue comme 0 VA —, équipements
   délestés et coupures non confirmées) et toute la configuration (abonnement, seuils, sources ordonnées, somme
   d'équipements, équipements délestables ; lier un thermostat reprend ses radiateurs). Reste à faire : les entités.
6. Un plugin Python d'exemple.
7. Import des données DomoMud, une seule fois, pour les deux installations (6 kVA et 12 kVA).

## 12. Décisions ouvertes

- Plages de la sirène hors 10 à 30 s, et valeurs par défaut.
- Seuils du délestage : 80 % / 70 % pour le 6 kVA ; retenus aussi pour le 12 kVA jusqu'à avis contraire.
- Forme des états structurés (prévision météo).
