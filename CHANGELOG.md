# Journal des versions

La version est dans `Directory.Build.props`. Elle s'affiche dans le menu ⋮ et donne son nom à l'archive de
`deploy/publier.ps1`. Chaque PR l'incrémente et ajoute ici son entrée : le 2ᵉ chiffre pour une nouveauté, le 3ᵉ pour
une correction ou un ajustement d'un écran existant (`docs/DEPLOIEMENT.md` § 6). L'appli mobile a sa propre version.

## 1.5.0 — 27/11/2026

**Tableau de bord**
- Mode édition : une carte se déplace vers une autre page (menu « Déplacer vers une autre page » de la carte, ou
  « Vers … » dans son menu compact) : à la fin de la section choisie, ou dans une section « Cartes déplacées » créée
  sur une page vide. Elle garde son widget, ses liens, sa taille et son nom ; « Enregistrer » valide, « Annuler » la
  ramène.

## 1.4.0 — 21/11/2026

**Services**
- Listes de réglages : « Copier » sous une liste, « Coller » sous la même liste d'un autre élément la remplace (après
  confirmation si elle n'est pas vide) — la page perso d'une platine, ses liaisons, passe à une autre en deux clics.

## 1.3.1 — 21/11/2026

**Services**
- Fenêtre d'une ligne de réglages qui contient elle-même des listes (une platine) plus large ; dans les tableaux de
  réglages, le n° de ligne, les nombres (n° d'objet…) et les boutons ne prennent que leur place.

## 1.3.0 — 21/11/2026

**OpenHasp**
- Page perso : une valeur « Si oui » / « Si non » (et « Si inconnu ») selon l'état d'une entité vraie ou fausse, qui
  peut aussi changer l'image d'un `img` (`src`) ou une couleur (`text_color`, `bg_color`) : une porte ouverte ou
  fermée, une lampe allumée ou éteinte.

## 1.2.2 — 21/11/2026

**Électricité**
- Graphiques plus hauts ; tableaux par jour et par semaine sur fond teinté (bleu, violet) ; colonne « Moyenne / jour »
  dans le tableau des semaines, sur les jours mesurés (#141).

## 1.2.1 — 21/11/2026

**Électricité**
- Un onglet par compteur au lieu de cartes empilées ; les tableaux par jour et par semaine prennent la hauteur de la
  fenêtre (#139).

## 1.2.0 — 20/11/2026

**Électricité**
- Tableau jour par jour depuis le premier relevé du compteur, et non plus sur 30 jours ; à côté, un tableau par semaine
  (du lundi au dimanche, numéro ISO) avec la consommation et le coût (#137).

## 1.1.0 — 20/11/2026

Tout ce qui a été fusionné depuis l'affichage de la version (#69), soit les PR #70 à #135.

**Chauffage et confort**
- Fenêtre ouverte : délais réglés sous le capteur, et repli Éco ou hors-gel au choix (#130).
- Plusieurs capteurs d'ouverture par thermostat, pour une baie à deux vantaux (#131).
- Widget « Thermostat (cadran) » puis retouches de la roue (#85, #86).

**Automatismes**
- Création et réglage dans une fenêtre à onglets (#79).
- Déclencheur « lever ou coucher du soleil » avec décalage (#126).
- Journal durable des exécutions sur 90 jours, avec l'heure réelle de chaque action (#127, #129).

**Supervision et alertes**
- Écran Supervision lisible : pastilles par famille et onglets (#134).
- Notification ntfy quand l'IP publique change (#132).
- « Depuis » calculé sur le dernier changement d'état (`changed_at`) (#125).

**Tableau de bord et widgets**
- Widgets personnalisés installés depuis l'interface (#73).
- Grille de 24 colonnes, hauteur par demi-ligne, sections côte à côte et colorées (#74, #75, #91, #96, #99).
- Fenêtres déplaçables (#81), confirmations dans la carte (#102), corrections d'affichage (#95, #120, #124, #128).

**Entités, historique, électricité**
- Entités : colonne Valeur au choix, suppression d'une entité disparue, entités désactivées masquées, nom qui suit le plugin (#78, #80, #82, #93, #105).
- Usage « Disjoncteur » (#122).
- Courbes d'historique sur 6 h, 24 h, 7 j et 30 j (#83).
- Écran Électricité : puissance du jour et consommation sur 30 jours (#84).

**Plugins**
- Nouveaux plugins : MQTT Discovery pour ESPHome, avec les entités MQTT génériques (#87, #103) ; Imprimantes IPP, avec les pièces en SNMP (#97, #98) ; Matter (#133).
- Raspberry : services, mémoire, swap et seuils d'alerte (#121, #123).
- Proxmox : carte du nœud et disque des machines (#77).
- openHASP : page perso en JSONL, Repeindre et Redémarrer une platine, écran façon DomoMud (#70, #88, #100).
- Fiche d'un service et réglages : mise en page, réglages non enregistrés signalés, saisie d'un entier corrigée (#76, #101, #104, #106).
- Journal des plugins dans journalctl (#92).

**Socle**
- Broker MQTT commun et pastille MQTT (#72, #89).
- SQLite en `synchronous = FULL` sur chaque connexion (#71).
- Délai propre pour la dernière écriture à l'arrêt (#107).

**Appli mobile** (versions mobiles à part, 0.2)
- Écrans Thermostats, Sondes, Radiateurs, Prises, Alarme et Énergie, avec les API `/api/sensors`, `/api/actuators` et `/api/energy` (#108 à #113).
- Plusieurs lieux dans une seule appli (#117).
- APK signé (#115).
- Présence, puis ouvertures et détecteurs, sous l'alarme, avec `/api/detectors` (#116, #135).

**Documentation**
- Familles et classes des widgets dans WIDGETS.md (#94).
- Corrections diverses (#90, #114, #118, #119).

## 1.0.0

Version par défaut du SDK .NET, jamais incrémentée jusqu'ici : tout ce qui précède #70.
