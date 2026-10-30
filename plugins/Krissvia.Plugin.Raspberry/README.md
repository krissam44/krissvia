# Plugin Raspberry (santé de la machine)

Plugin Python : la santé de la machine qui fait tourner Krissvia, lue dans `/proc` et `/sys`, sans dépendance.
Pensé pour le Raspberry Pi 4 de l'appartement ; marche sur tout Linux (ce qu'une machine ne sait pas dire n'est
simplement pas déclaré).

## Entités (un appareil : le nom de la machine)

| Entité | Classe | Contenu |
|---|---|---|
| Santé | `system_health` (objet) | tout ce qui suit, plus modèle, charges 1/5/15 min, Go libres, durée de fonctionnement, seuils — c'est la carte |
| Température processeur | `device_temperature` (°C) | première zone thermique. **Pas** `temperature` : ce n'est pas une pièce, elle n'a rien à faire dans les sondes, les moyennes par pièce ni les thermostats |
| Processeur | % | occupation entre deux relevés (`/proc/stat`, attente d'E/S comptée comme inactive) |
| Charge | — | charge moyenne sur 1 min (`/proc/loadavg`) ; 4,0 = les quatre cœurs d'un Pi 4 occupés |
| Mémoire | % | d'après `MemAvailable` (ce que le noyau peut rendre), pas `MemFree` |
| Swap | % | seulement s'il y a une swap |
| Disque | % | système de fichiers du chemin réglé (la racine par défaut) |
| Taille de la base | Mo | la base SQLite **plus** ses `-wal` et `-shm` |
| Mémoire *service* | Mo | une par service surveillé, voir plus bas |
| Service *service* arrêté | `problem` (binary_sensor) | vrai quand systemd ne le dit pas `active` |
| Surchauffe, Mémoire pleine, Disque plein | `problem` (binary_sensor) | seuils réglables, avec une marge de retour |
| Sous-tension, Bridage | `problem` (binary_sensor) | **en ce moment**, d'après `get_throttled` du microprogramme ; Raspberry Pi seulement |

« Depuis le démarrage » (bits 16 à 19 de `get_throttled`) est dans l'objet Santé : la carte l'affiche en orange.

## Services et fuites de mémoire

La page Raspberry de DomoMud suivait la mémoire de **processus** (`dotnet`, `node`, `mosquitto`). Le plugin suit
celle des **services systemd** réglés (`krissvia,mosquitto,zigbee2mqtt` par défaut), lue dans leur groupe de
contrôle (`/sys/fs/cgroup/system.slice/<service>.service/memory.stat`) :

- **tous les processus du service comptent** : les plugins de Krissvia sont dans `krissvia.service`, et Zigbee2MQTT
  tourne sous un `node` anonyme qu'un nom de processus ne retrouverait pas ;
- c'est la mémoire **anonyme** (`anon`, le tas) qui est relevée, pas `memory.current` : celle-ci compte aussi le cache
  disque, qui monte pendant des jours sans être une fuite et se rend dès que la mémoire manque.

Le cœur historise toute entité numérique (`docs/HISTORIQUE.md`) : 30 jours de points, puis des moyennes horaires sans
limite. Une fuite se lit sur la courbe de « Mémoire krissvia » (écran Entités, icône 📈, période 7 j ou 30 j) : une
marche d'escalier par jour, là où un service sain dessine un plateau.

Un service que systemd ne connaît pas (`LoadState=not-found` : pas installé sur cette machine, faute de frappe) est
ignoré, faute de quoi il resterait « arrêté » et en alerte à vie. Un service connu mais arrêté au démarrage du
plugin est déclaré quand même ; sa mémoire n'est pas publiée tant qu'il ne tourne pas (pas de 0 inventé, la courbe
s'interrompt).

## Alertes

Les entités `problem` sont surveillées par le cœur (`docs/SUPERVISION.md`) : « Problème : pi Sous-tension » dès
qu'elle passe à vrai, « Problème résolu » au retour. Une alimentation trop faible est la cause classique d'une carte
SD ou d'une base corrompue — c'est l'alerte à ne pas manquer.

Les dépassements ont une **marge de retour** (hystérésis) : une mémoire à 90 % passe en alerte, et n'en sort que sous
85 %. Sans elle, une valeur qui oscille autour du seuil enverrait une alerte et sa résolution à chaque relevé.

## Carte

Livrée par le plugin (`widgets/raspberry`, WIDGETS.md §13), choisie d'office pour l'entité Santé : les problèmes en
tête (alimentation, service arrêté, dépassements), une barre par ressource, puis les services avec leur état et leur
mémoire, et en bas durée de fonctionnement, charges, Go libres et taille de la base.

## Réglages (page Services)

| Réglage | Défaut |
|---|---|
| Nom de la machine | son nom réseau (`hostname`) |
| Relevé toutes les (s) | 30 (5 à 600) |
| Disque surveillé | `/` — mettre le dossier de la base si elle est sur un autre disque |
| Services surveillés | `krissvia,mosquitto,zigbee2mqtt` |
| Base surveillée | celle de Krissvia (`Database__Path`, hérité du service), sinon `/var/lib/krissvia/krissvia.db` |
| Surchauffe à partir de (°C) | 75 (retour sous 70) |
| Mémoire pleine à partir de (%) | 90 (retour sous 85) |
| Disque plein à partir de (%) | 85 (retour sous 83) |

`Raspberry:Racine` (variable `Raspberry__Racine`) change la racine de `/proc` et `/sys` : il ne sert qu'aux essais.

## Tests

```bash
python -m unittest discover -s plugins/Krissvia.Plugin.Raspberry
```

Sur une fausse arborescence `/proc` et `/sys`, et une fausse sortie de `systemctl show` : un Raspberry qui dit tout,
l'occupation entre deux relevés, la sous-tension en ce moment et depuis le démarrage, une machine qui ne sait rien,
les classes (`device_temperature`, `problem`, `system_health`), la swap et les charges, un service inconnu de
systemd ignoré, la mémoire anonyme d'un service (pas son cache), un service arrêté sans mémoire inventée, la taille
de la base avec son journal, la marge de retour d'un seuil.
