# Krissvia.Plugin.OpenHasp

Platines murales openHASP (Guition 480×480). **Krissvia génère leurs pages** : plus de `pages.jsonl` à déposer, ni
d'identifiants d'objets à recopier. Le plugin s'appuie sur le contrat 0.3 (`docs/CONTRACT-V0.3.md`) : il suit les
entités choisies dans ses réglages et transmet les appuis au cœur comme des ordres, signés `plugin:openhasp`.

## Les pages

| Page | Contenu | Présente si |
|---|---|---|
| 1 Thermostat | titre, ambiante, consigne −/+, mode et statut, bandeau de chauffe, six modes, bouton « Prog » (planning) | la platine a un thermostat |
| 2 Températures | jusqu'à six sondes en arcs, en rangées de trois (la dernière rangée est centrée), **les mêmes sur toutes les platines** | au moins une sonde |
| 3 Horloge | heure (tenue par la platine), date et température extérieure, prochaine bascule du planning (« Confort à 18:00 »), jusqu'à trois interrupteurs **propres à chaque platine**, curseur de luminosité | toujours |
| 4 Alarme | digicode : état et décompte, capteur en cause ou défaut, code masqué, clavier 0-9 / C / ⌫, **Armer** et **Désarmer** | une alarme est choisie et la platine a « Digicode » coché (par défaut) |

La barre du bas ne propose que les pages présentes (à quatre, « Températures » devient « Sondes » : le mot ne tient
plus dans le bouton). Une consigne réglée au −/+ n'allume plus aucun mode : aucun ne la
décrit. La prochaine bascule vient du thermostat de la platine (`next_change` de l'entité `climate`) et reste vide quand
le planning est arrêté ou que l'absence l'emporte.

## Quand les pages sont envoyées

À chaque `online` de la platine (son LWT, retenu par le broker) : la platine est effacée (`clearpage all`), reçoit ses
pages ligne par ligne (`command/jsonl`), puis tout son affichage. Donc au démarrage du plugin, et à chaque redémarrage
de la platine — qui repart de rien. Le même `online` relu après une simple reconnexion au broker ne repeint pas.

Ensuite, seul ce qui change est envoyé, et ce qui n'a pas changé est renvoyé toutes les deux minutes (une platine peut
avoir perdu un attribut sans qu'on le sache). Changer les réglages relance le plugin, donc regénère les pages.

## Réglages (page Services)

- **Broker** : celui des platines.
- **Platines**, une par pièce : son **nom de nœud** (celui réglé dans la platine), un titre, son **thermostat**, ses
  **interrupteurs** (interrupteur ou lumière, avec un libellé). Choisir une entité, c'est autoriser le plugin à la
  commander.
- **Températures** : les sondes de la page 2, avec les bornes de leur arc (une sonde extérieure : −10 à 40).
- **Affichage** : sonde extérieure (sous l'horloge), couleur de fond, contraste minimum des textes (3 par défaut, 0 pour
  aucune correction), luminosité au réveil, extinction en veille.

Hors du cœur, les mêmes réglages vont dans `appsettings.Local.json` (modèle : `appsettings.Local.example.json`).

## Préparer une platine

1. Dans son interface web : broker MQTT et **nom de nœud unique entre installations** (`appart1`… à l'appartement).
   Le topic `hasp/{nœud}/…` ne dit pas quelle installation possède la platine : deux sites sur un même broker se
   repeindraient l'un l'autre, et toutes les vérifications locales passeraient (vécu sur DomoMud le 03/10/2026).
2. Pour la veille : `idle1` / `idle2` (secondes, la veille longue arrive à `idle1 + idle2`), puis **redémarrer** — une
   écriture de `config/gui` n'est appliquée qu'au redémarrage. La platine ne fait qu'annoncer sa veille ; c'est le
   plugin qui éteint (`backlight off`) et rallume.
3. Rien à déposer : un ancien `pages.jsonl` est effacé à la connexion.

**Venant de DomoMud** : arrêter le pont openHASP de DomoMud (ou DomoMud) avant d'activer ce plugin. Deux éditeurs sur
les mêmes nœuds se repeindraient l'un l'autre en continu.

## Ce qui est repris de DomoMud, et pourquoi

- Couleurs de texte relevées si elles deviennent illisibles sur le fond choisi (`CouleurOpenHasp`) ; les aplats (boutons
  de mode, bandeau) gardent leur couleur, sinon le code couleur se délave.
- Couleur d'un arc : `line_color10`. `arc_color` n'existe pas, et openHASP ignore en silence un attribut inconnu.
- Fond : un objet plein écran déclaré en premier sur chaque page, uni (`bg_grad_color` = `bg_color`, `bg_grad_dir` 0).
- Polices 16, 24, 32 et 48 seulement, celles de la mise en page déployée ; séparateurs en ASCII (« · » sort en carré
  vide). Les libellés de mode sont ceux de la platine (« Confort -1 ») : le « − » du cœur n'est pas dans sa police.
- Luminosité : plancher 20 (à 1 l'écran est noir, et le curseur qui le rallumerait est dessus) ; gardée en mémoire
  seulement : tant que personne n'a touché le curseur, rien n'est imposé à la platine.
- Veille : une platine endormie est exclue de la republication périodique, sinon elle se rallumerait dix secondes plus
  tard ; le réveil rend la luminosité choisie, ou celle par défaut, jamais « rien ». Les deux formes du message
  (`off`, `{"idle":"off"}`) sont acceptées.
- Interrupteurs : allumer ou éteindre selon l'état connu, jamais `toggle`, que tous les appareils ne portent pas.

Chaque platine est aussi un appareil du cœur, avec une entité **Connexion** (son LWT).

## Le digicode (page 4)

- Le code tapé reste **dans la mémoire du plugin**, une saisie par platine : la platine n'affiche que des étoiles, le
  journal ne l'écrit jamais, il part une seule fois dans les arguments de `arm` / `disarm` (comme depuis la carte du
  tableau de bord : mêmes codes, même blocage après trois codes faux), puis il est oublié.
- Une saisie abandonnée **expire au bout de 30 s** : le suivant qui passe ne peut pas finir le travail d'un autre.
- Pas d'ordre sans code : un code vide serait refusé et compterait pour le blocage. Le refus du cœur s'affiche tel
  quel (« Code incorrect », « Trop de codes faux : clavier bloqué 42 s », « Armement refusé : Porte d'entrée »).
- **Au début du décompte d'entrée et au déclenchement**, toutes les platines qui portent le digicode passent sur la
  page 4, écran rallumé s'il dormait : celui qui rentre n'a pas à chercher la page pendant que le décompte tourne.
  Pas à chaque seconde du décompte, ni au premier état reçu par un plugin qui redémarre.
- Bloqué dehors du clavier (trois codes faux) : la page `/alarme` arme et désarme sans code pour un compte connecté.

**Ce que le broker voit** : openHASP publie chaque appui (`hasp/appart1/state/p4b13`), donc qui écoute le broker
peut reconstituer le code touche par touche. C'est la nature d'une platine MQTT, pas un choix du plugin : le broker
doit être local, avec mot de passe, et sans pont vers l'extérieur.
