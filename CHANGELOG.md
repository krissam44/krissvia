# Journal des versions

La version est dans `Directory.Build.props`. Elle s'affiche dans le menu ⋮ et donne son nom à l'archive de
`deploy/publier.ps1`. Elle change à chaque publication qui apporte du nouveau : le 2ᵉ chiffre pour des nouveautés,
le 3ᵉ pour des corrections seules (`docs/DEPLOIEMENT.md` § 6). L'appli mobile a sa propre version.

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
