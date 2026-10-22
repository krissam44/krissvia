# Déploiement sur le Raspberry Pi

> Kit livré le 22/10/2026 : `deploy/publier.ps1` (archive), `deploy/raspberry/` (unité systemd, installation,
> sauvegarde). Cible : le Raspberry Pi 4 de l'appartement (arm64, Raspberry Pi OS / Debian 13, runtime ASP.NET
> Core 10 déjà installé, Mosquitto et Zigbee2MQTT sur le même Pi), où Krissvia remplace DomoMud.

## 1. Ce qui va où

| Sur le Pi | Contenu | Propriétaire | Remplacé à la mise à jour |
|---|---|---|---|
| `/opt/krissvia/server/` | le cœur (`Krissvia.Server.dll`, `wwwroot`, `sdk/python/krissvia.py`) | root, 755 | oui, en entier |
| `/opt/krissvia/plugins/<nom>/` | un dossier par plugin, avec son `plugin.json` | root, 755 | oui, en entier |
| `/opt/krissvia/raspberry/` | les fichiers de ce kit, pour mémoire | root | oui |
| `/var/lib/krissvia/` | `krissvia.db` (+ `-wal`, `-shm`), `keys/`, `plugins-data/`, `widgets/` | **krissvia**, 750 | jamais |
| `/etc/krissvia/krissvia.env` | secrets du service (`Api__Key`) | root, **600** | jamais |
| `/var/backups/krissvia/` | sauvegardes, toutes les 6 h | krissvia, 700 | jamais |

Le programme ne contient rien de propre à la machine : c'est ce qui permet de le remplacer en bloc. Tout le
reste est déjà rangé à côté de la base par le cœur (`Program.cs` : clés des cookies, données des plugins et
widgets locaux suivent `Database:Path`).

Le service écoute en HTTP sur le port **12795**, toutes interfaces (`http://<ip-du-pi>:12795`). DomoMud écoute sur
le 80 : les deux peuvent tourner ensemble pendant la bascule (§ 7). Les plugins se connectent au cœur par
`ws://127.0.0.1:12795/plugins` — l'adresse http d'écoute, ramenée à 127.0.0.1. Passer le cœur en https **seul**
priverait les plugins de cette adresse ; un https se met devant (nginx), pas à la place.

## 2. Publier (sur le PC)

```powershell
.\deploy\publier.ps1
```

Produit `publish/krissvia-<date>-<commit>.tar.gz` (≈ 7 Mo) : le cœur et chaque plugin C# publiés en
`-c Release -r linux-arm64 --self-contained false -p:DebugType=none`, les plugins Python copiés tels quels
(sans `test_*.py` ni `__pycache__`), et `deploy/raspberry/`. `-Sortie <dossier>` change le dossier,
`-Version <nom>` le nom de l'archive.

- **Une seule archive, jamais une copie fichier par fichier.** Chez DomoMud, une copie interrompue a tronqué
  `blazor.web.js` et `MudBlazor.min.js` : toute l'interface était morte, et le journal vide parce que la panne
  était entièrement côté navigateur.
- **Toujours les projets, jamais la solution** : elle contient les tests.
- **Rien dans les `bin/`/`obj/` du dépôt** (`--artifacts-path` sous le dossier de sortie) : on peut publier
  pendant qu'une instance de développement tourne depuis `bin/Debug`.
- **Exclus de l'archive** : `appsettings.Local.json` (jetons des plugins du poste), `appsettings.Development.json`
  (il accepte les plugins sans jeton : il ne doit pas pouvoir s'activer sur le Pi), `*.db*`, `keys/`,
  `plugins-data/`. Le script nomme ce qu'il a écarté.
- Le script prévient si le dépôt a des modifications non validées : elles partiraient dans l'archive.

Pourquoi PowerShell et pas bash : l'archive est écrite avec `System.Formats.Tar`, qui fixe les droits Unix de
chaque fichier. Le cœur lance un plugin C# par son exécutable (`executable` du `plugin.json`, un ELF sans
extension) ; aucun `tar` lancé depuis Windows ne lui donne le droit d'exécution, et le plugin échouerait sur
« Permission denied ». Les scripts de `deploy/raspberry/` sont aussi forcés en LF : un `\r` empêche
`#!/bin/sh` de se lancer et finit dans les valeurs des unités systemd.

## 3. Copier

```powershell
scp publish\krissvia-20261022-7a3d9eb.tar.gz christian@home56:/tmp/
```

## 4. Installer (sur le Pi)

Prérequis, une fois :

```bash
sudo apt install sqlite3 python3-websockets
```

`sqlite3` pour la sauvegarde ; `python3-websockets` pour les plugins Python (`PLUGINS-PYTHON.md` § 1 : Python
3.10 ou plus, `websockets` 13 ou plus — Debian 13 fournit les deux). Le paquet doit être visible de
l'utilisateur `krissvia` : celui d'apt l'est, un `pip install --user` fait sous `christian` ne l'est pas. Pour un
environnement virtuel, voir la ligne `PluginHost__Runtimes__python` de `krissvia.env`.

Puis :

```bash
mkdir /tmp/krissvia && tar -xzf /tmp/krissvia-20261022-7a3d9eb.tar.gz -C /tmp/krissvia
sudo /tmp/krissvia/raspberry/installer.sh
```

`installer.sh` (idempotent, le même pour la mise à jour) :

1. vérifie `/usr/bin/dotnet` et le runtime ASP.NET Core 10 ; si `dotnet` est ailleurs, le message donne le lien
   symbolique à créer ;
2. crée l'utilisateur système `krissvia` et les dossiers du § 1, avec leurs propriétaires. **Le dossier de la
   base appartient à `krissvia`**, pas seulement le fichier : SQLite écrit `-wal` et `-shm` à côté, et un dossier
   en lecture seule donne « attempt to write a readonly database » au premier démarrage ;
3. arrête le service s'il tourne et **attend l'arrêt vérifié** (§ 6) ;
4. remplace `/opt/krissvia/{server,plugins,raspberry}` ;
5. crée `/etc/krissvia/krissvia.env` (600) avec une clé d'API tirée au hasard — seulement s'il n'existe pas ;
6. installe `krissvia.service`, la sauvegarde (`/usr/local/bin/sauvegarde-krissvia.sh` + service + timer), et
   active le timer ;
7. prévient si `websockets` manque, ou si DomoMud est en service (§ 7) ;
8. démarre Krissvia et vérifie qu'il tient.

L'unité (`deploy/raspberry/krissvia.service`) :

```ini
User=krissvia
WorkingDirectory=/opt/krissvia/server
ExecStart=/usr/bin/dotnet /opt/krissvia/server/Krissvia.Server.dll
Environment=ASPNETCORE_URLS=http://0.0.0.0:12795
Environment=Database__Path=/var/lib/krissvia/krissvia.db
Environment=Plugins__Directories__0=/opt/krissvia/plugins
EnvironmentFile=/etc/krissvia/krissvia.env
Restart=always
```

À l'arrêt, systemd tue tout le cgroup du service : les plugins partent avec le cœur.

## 5. Premier démarrage

1. **Le compte.** Tant qu'aucun compte n'existe, le cœur écrit un lien à usage unique dans le journal :

   ```bash
   sudo journalctl -u krissvia --no-pager | grep installation
   ```

   `Aucun compte. Créez le premier sur /installation?jeton=…` : ouvrir `http://<ip-du-pi>:12795/installation?jeton=…`.
   Le jeton change à chaque démarrage tant que le compte n'est pas créé.

2. **Les plugins** (**Paramètres › Services**, `/parametres/services`). Tous ceux de l'archive sont listés, aucun ne
   tourne (un plugin n'est lancé qu'une fois activé) : chacun s'active, se règle et se lance depuis sa carte.
   Le broker est sur le Pi : `127.0.0.1` pour Zigbee2MQTT et Téléinfo (Mosquitto, port 1883, avec ses
   identifiants). La carte montre ce que le plugin écrit ; c'est là, ou dans le journal du cœur pour un
   lancement impossible, qu'apparaît un « Permission denied » (exécutable sans droit, § 2) ou un
   `ModuleNotFoundError: websockets` (§ 4).

3. **La clé d'API**, pour Node-RED ou l'appli mobile (en-tête `X-Api-Key`) :

   ```bash
   sudo grep Api__Key /etc/krissvia/krissvia.env
   ```

   Sans clé, l'API répond 503 en production plutôt que de s'ouvrir à tout le réseau.

4. **ntfy** (**Paramètres › Notifications**), pour que les alertes — et l'échec d'une sauvegarde — arrivent sur le téléphone.

## 6. Mettre à jour

Avec le script, comme une installation : publier, copier, extraire dans un dossier **vide**, puis
`sudo /tmp/krissvia/raspberry/installer.sh`.

À la main, l'ordre est le même, et l'étape 2 n'est pas facultative :

```bash
sudo systemctl stop krissvia
systemctl is-active krissvia; pgrep -u krissvia -a     # 2. « inactive », et aucun processus
sudo rm -rf /opt/krissvia/server /opt/krissvia/plugins
sudo tar -xzf /tmp/krissvia-<version>.tar.gz -C /opt/krissvia server plugins
sudo systemctl start krissvia
```

**Vérifier l'arrêt.** L'unité porte `Restart=always` : un service qu'on croit arrêté — un `kill`, un arrêt
mal tapé — peut être revenu dix secondes plus tard, et deux processus sur la même base SQLite, c'est exactement
ce qui a corrompu celle de DomoMud le 02/10/2026 (un index et une page référencée deux fois, récupérés par
`.recover`). `pgrep -u krissvia` doit aussi être vide : un plugin qui survivrait continuerait d'écrire dans
`plugins-data/`. `installer.sh` fait cette vérification et refuse d'aller plus loin si l'arrêt n'est pas obtenu
en trente secondes.

Supprimer `server/` et `plugins/` avant d'extraire : extrait par-dessus, un fichier retiré d'une version
resterait dans la suivante. On ne perd rien, rien de propre à la machine n'y vit (§ 1).

## 7. Remplacer DomoMud

**Krissvia n'importe pas la base de DomoMud.** Équipements, thermostats, plannings, délestage, automatismes et
groupes de sondes se ressaisissent ; les équipements Zigbee réapparaissent seuls par le plugin Zigbee2MQTT, les
Shelly s'ajoutent par leur adresse.

**DomoMud et Krissvia sur le même broker pilotent les mêmes radiateurs.** Chacun envoie ses ordres
`zigbee2mqtt/…/set` et ses requêtes HTTP aux Shelly selon sa propre régulation : l'un allume pendant que l'autre
coupe, le délestage de l'un rallume ce que l'autre vient de délester, et chacun croit son relais dans l'état
qu'il a commandé. Deux façons de faire, pas trois :

- **Bascule franche** : arrêter DomoMud, **vérifier l'arrêt** (même piège `Restart=always` que § 6), le
  désactiver, puis configurer Krissvia en entier.

  ```bash
  sudo systemctl disable --now domomud
  systemctl is-active domomud; pgrep -af DomoMud     # « inactive », et rien
  ```

- **Période d'observation** : laisser DomoMud piloter, et n'utiliser Krissvia qu'en lecture — plugins activés,
  sondes, compteurs, tableau de bord, supervision — **sans aucun thermostat, délestage, programmation ni
  automatisme qui commande un équipement**. Une fois satisfait, bascule franche comme ci-dessus.

`installer.sh` affiche un avertissement tant que `domomud` est en service ; il ne l'arrête pas lui-même, c'est un
choix qui vous revient.

DomoMud reste installé (`/opt/domomud`, sa base, ses sauvegardes) : en cas de besoin,
`sudo systemctl disable --now krissvia` puis `sudo systemctl enable --now domomud` le remettent en service tel
qu'il était. Les deux timers de sauvegarde peuvent coexister (00 h 00 pour DomoMud, 00 h 15 pour Krissvia).

## 8. Sauvegarde et restauration

`sauvegarde-krissvia.timer` lance `sauvegarde-krissvia.sh` toutes les 6 h (00, 06, 12, 18 h 15, rattrapée après
une coupure), sous l'utilisateur `krissvia`. Reprise de celle de DomoMud, mêmes règles :

- **`sqlite3 .backup`, jamais `cp`** : en mode WAL, le `.db` seul est un instantané incomplet.
- **Contrôle d'intégrité avant la rotation** : une copie qui échoue à `PRAGMA integrity_check` est jetée et les
  précédentes restent intactes. Sans cela, une base qui se corrompt remplace en un jour toutes les copies saines.
- **Sous l'utilisateur du service, jamais root** : en root, SQLite peut laisser un `krissvia.db-shm` à root à
  côté de la base, et le cœur ne démarre plus.
- **Alerte ntfy en cas d'échec**, avec les réglages relus dans la dernière copie saine et mis en cache dans
  `/var/backups/krissvia/.ntfy` — le jour où la base est illisible, c'est le cache qui prévient encore.

Chaque sauvegarde est **une** archive `krissvia-AAAAMMJJ-HHMMSS.tar.gz` : la base, plus `plugins-data/`,
`widgets/` et `keys/`. 28 copies conservées (7 jours) ; `Environment=RETENTION=56` dans le `.service` pour plus.

```bash
sudo systemctl start sauvegarde-krissvia.service       # premier essai, sans attendre
journalctl -u sauvegarde-krissvia -n 20 --no-pager -o cat
systemctl list-timers sauvegarde-krissvia.timer
```

Restaurer :

```bash
sudo systemctl stop krissvia
systemctl is-active krissvia; pgrep -u krissvia -a     # inactive, rien
sudo -u krissvia mkdir /var/lib/krissvia/restauration
sudo -u krissvia tar -xzf /var/backups/krissvia/krissvia-AAAAMMJJ-HHMMSS.tar.gz -C /var/lib/krissvia/restauration
sudo -u krissvia sqlite3 /var/lib/krissvia/restauration/krissvia.db "PRAGMA integrity_check;"
sudo -u krissvia sh -c 'cd /var/lib/krissvia && rm -f krissvia.db-wal krissvia.db-shm && mv restauration/krissvia.db krissvia.db'
# plugins-data/, widgets/, keys/ : les reprendre de restauration/ au besoin, puis supprimer le dossier
sudo systemctl start krissvia
```

Supprimer `-wal` et `-shm` est correct ici, et seulement ici : la copie restaurée est complète (`.backup` y a
réintégré le journal), alors que ces fichiers appartiennent à l'ancienne base. Sur une base en service, les
supprimer détruirait des transactions.

**Limite connue** : les sauvegardes sont sur le même SSD que la base. Elles protègent d'une corruption logicielle,
pas d'une panne du support — prévoir une recopie hors du Pi.

## 9. Diagnostic

| Symptôme | Cause probable |
|---|---|
| `attempt to write a readonly database` au démarrage | dossier `/var/lib/krissvia` pas à `krissvia`, ou un `-shm` à root : `sudo chown -R krissvia: /var/lib/krissvia` |
| Plugin C# : « Permission denied » dans sa carte | archive faite sans `publier.ps1` ; `installer.sh` remet le droit, ou `sudo chmod 755 /opt/krissvia/plugins/<nom>/<exécutable>` |
| Plugin Python : `No module named 'websockets'` | `sudo apt install python3-websockets` (§ 4) |
| Plugin qui se connecte puis est refusé (« jeton ») | plugin lancé à la main hors du cœur : en production seuls ceux que le cœur lance sont acceptés |
| Boutons morts, liens du menu fonctionnels, journal vide | fichiers JS tronqués : republier et réinstaller depuis l'archive, jamais une copie fichier par fichier |
| L'API répond 503 | `Api__Key` absent de `/etc/krissvia/krissvia.env` |
| Le service redémarre en boucle | `journalctl -u krissvia -n 50 --no-pager` ; souvent `dotnet` absent de `/usr/bin` ou le runtime 10 manquant |

Le journal du cœur : `journalctl -u krissvia -f`. Ce qu'écrit chaque plugin est dans sa carte de la page Services.
