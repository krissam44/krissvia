# Plugin Raspberry (santé de la machine)

Plugin Python : la santé de la machine qui fait tourner Krissvia, lue dans `/proc` et `/sys`, sans dépendance.
Pensé pour le Raspberry Pi 4 de l'appartement ; marche sur tout Linux (ce qu'une machine ne sait pas dire n'est
simplement pas déclaré).

## Entités (un appareil : le nom de la machine)

| Entité | Classe | Contenu |
|---|---|---|
| Santé | `system_health` (objet) | tout ce qui suit, plus modèle, charge, Go libres, durée de fonctionnement — c'est la carte |
| Température processeur | `device_temperature` (°C) | première zone thermique. **Pas** `temperature` : ce n'est pas une pièce, elle n'a rien à faire dans les sondes, les moyennes par pièce ni les thermostats |
| Processeur | % | occupation entre deux relevés (`/proc/stat`, attente d'E/S comptée comme inactive) |
| Mémoire | % | d'après `MemAvailable` (ce que le noyau peut rendre), pas `MemFree` |
| Disque | % | système de fichiers du chemin réglé (la racine par défaut) |
| Sous-tension, Bridage | `problem` (binary_sensor) | **en ce moment**, d'après `get_throttled` du microprogramme ; Raspberry Pi seulement |

« Depuis le démarrage » (bits 16 à 19 de `get_throttled`) est dans l'objet Santé : la carte l'affiche en orange.

## Alertes

Les entités `problem` sont surveillées par le cœur (`docs/SUPERVISION.md`) : « Problème : pi Sous-tension » dès
qu'elle passe à vrai, « Problème résolu » au retour. Une alimentation trop faible est la cause classique d'une carte
SD ou d'une base corrompue — c'est l'alerte à ne pas manquer.

## Carte

Livrée par le plugin (`widgets/raspberry`, WIDGETS.md §13), choisie d'office pour l'entité Santé : une barre par
ressource, l'alimentation en tête quand elle pose problème, durée de fonctionnement en bas.

## Réglages (page Services)

| Réglage | Défaut |
|---|---|
| Nom de la machine | son nom réseau (`hostname`) |
| Relevé toutes les (s) | 30 (5 à 600) |
| Disque surveillé | `/` — mettre le dossier de la base si elle est sur un autre disque |

`Raspberry:Racine` (variable `Raspberry__Racine`) change la racine de `/proc` et `/sys` : il ne sert qu'aux essais.

## Tests

```bash
python -m unittest discover -s plugins/Krissvia.Plugin.Raspberry
```

Sur une fausse arborescence `/proc` et `/sys` : un Raspberry qui dit tout, l'occupation entre deux relevés, la
sous-tension en ce moment et depuis le démarrage, une machine qui ne sait rien (n'en déclare que le disque), les
classes (`device_temperature`, `problem`, `system_health`).
