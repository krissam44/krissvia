# Plugins en Python

Un plugin Krissvia est un processus séparé qui parle au cœur par WebSocket (`TRANSPORT.md`). Le SDK C#
n'est qu'une façon de le faire : `sdk/python/krissvia.py` en est l'équivalent Python, en un fichier, et
`plugins/Krissvia.Plugin.Meteo` l'exemple complet : plusieurs lieux, chacun avec ses conditions du moment et sa
prévision sur 7 jours (Open-Meteo), dessinés par le widget standard `meteo`.

## 1. Ce qu'il faut sur la machine

- Python 3.10 ou plus.
- Le paquet `websockets` (13 ou plus) : `pip install websockets`, ou `apt install python3-websockets` sur
  Debian/Ubuntu (Raspberry Pi compris). Il doit être visible de l'utilisateur qui fait tourner Krissvia.
- Rien d'autre pour le SDK : le cœur le livre (`sdk/python/krissvia.py` à côté de son exécutable) et
  l'ajoute au `PYTHONPATH` des plugins qu'il lance.

L'interpréteur est `python` sous Windows, `python3` ailleurs. Un autre (environnement virtuel, version
précise) se règle dans la configuration du cœur :

```json
{ "PluginHost": { "Runtimes": { "python": "/opt/krissvia/venv/bin/python" } } }
```

## 2. La fiche `plugin.json`

La même que pour un plugin C#, avec `runtime` et le script en `executable` :

```json
{
  "id": "meteo",
  "name": "Météo (Python)",
  "description": "…",
  "runtime": "python",
  "executable": "meteo.py",
  "settings": { "type": "object", "properties": { "Meteo": { "type": "object", "properties": {
    "Latitude": { "type": "number", "title": "Latitude" } } } } }
}
```

Le dossier (fiche + script) se pose dans un des dossiers de plugins du cœur (`Plugins:Directories`) ; la page
Services le trouve, l'active, le lance, le relance s'il s'arrête, et montre ce qu'il écrit. Le cœur lance
`python -u meteo.py` dans le dossier du plugin, avec `PYTHONIOENCODING=utf-8` (le journal est lu en UTF-8) et
`PYTHONDONTWRITEBYTECODE=1` (pas de `__pycache__` dans le dossier du plugin).

## 3. Réglages

Les réglages saisis dans la page Services arrivent en variables d'environnement, comme pour un plugin C# :
`Meteo:Latitude` devient `Meteo__Latitude`. `krissvia.setting("Meteo:Latitude", convert=float)` les lit ; une
valeur invalide lève une `ValueError` qui nomme le réglage. Une liste d'objets de la page (`"type": "array"`,
comme les lieux du plugin Météo) arrive en `Lieux__0__Nom`, `Lieux__0__Latitude`, `Lieux__1__Nom`… ;
`krissvia.settings_list("Lieux")` la rend en `[{"nom": …, "latitude": …}, …]`, noms de champs en minuscules
(Windows met en majuscules les noms des variables qu'on parcourt). L'adresse du cœur, le jeton et l'identifiant
(`Krissvia__Url`, `Krissvia__Token`, `Krissvia__PluginId`) sont posés par le cœur : `CoreClient` les lit
seul. `Krissvia__ParentPid` aussi : le PID du cœur, pour s'arrêter avec lui (§ 4).

## 4. Le SDK

```python
import krissvia

class Cave(krissvia.Plugin):
    def snapshot(self):                      # à chaque (re)connexion : declare, TOUS les états, joignabilités
        return [
            krissvia.declare(krissvia.device("cave", "Cave", [
                krissvia.entity("cave:chauffe_eau", "switch", "Chauffe-eau", commands=["turn_on", "turn_off"]),
            ])),
            krissvia.state(krissvia.value("cave:chauffe_eau", self.allume)),
        ]

    async def execute(self, command):        # chaque ordre reçoit un résultat ; une exception devient « failed »
        ...                                  # piloter l'appareil
        return krissvia.accepted()           # ou failed("…"), unreachable()

    async def surveiller(self, client):      # une tâche du plugin, à côté de la connexion
        await client.send(krissvia.state(krissvia.value("cave:chauffe_eau", True)))

krissvia.run(Cave(), Cave.surveiller, plugin_version="1.0.0")
```

- `CoreClient` fait le reste : `hello`, attente du `welcome`, resynchronisation sous le verrou d'envoi (une
  mesure émise pendant ce temps arrive après l'instantané), `ping` toutes les 15 s, reconnexion après un cœur
  muet 45 s, attente d'une minute après un refus (jeton, version), reconnexion sans fin sinon.
- `send()` renvoie `False` hors session : la mesure est perdue, et c'est voulu — la prochaine
  resynchronisation portera l'état à jour.
- Un ordre « accepté » ne prouve pas l'effet : publier ensuite l'état résultant de l'entité.
- Quand une source est injoignable, publier `availability(False, …)` : le cœur rend les entités
  indisponibles au lieu de garder la dernière valeur.
- Un plugin lancé par le cœur **ne lui survit pas** : `CoreClient` vérifie toutes les 5 s que le processus
  `Krissvia__ParentPid` existe encore ; s'il a disparu (cœur tué, planté, arrêté au débogueur), `run()` rend
  la main et `krissvia.run()` annule les tâches du plugin, puis le processus sort. Sous Windows le Job Object
  du cœur le tue de toute façon ; c'est la ceinture pour le reste (docs/TRANSPORT.md § 5). Lancé à la main,
  sans la variable, rien n'est surveillé. Les paramètres `parent_pid`, `parent_check` et `parent_alive`
  (fonction `pid → bool`) servent aux tests. Ne jamais tester l'existence d'un processus avec
  `os.kill(pid, 0)` sous Windows : il le **tue** ; `krissvia.process_alive` passe par `OpenProcess`.

Un plugin peut livrer ses **cartes de tableau de bord** : un dossier `widgets/` à côté de `plugin.json`, un
sous-dossier par widget (`widget.json` et son script). Publiées tant que le plugin est activé (WIDGETS.md §13).

### Lire et commander d'autres entités (contrat 0.3)

Une platine, un clavier : un plugin peut suivre et commander des entités qui ne sont pas à lui — seulement celles
choisies dans **ses réglages** (`"format": "entity"`, `"x-access": "command"` pour commander ; `CONTRACT-V0.3.md`).

```python
class Platine(krissvia.Plugin):
    def on_entity_state(self, m):            # à l'abonnement, puis à chaque changement
        print(m["entity_id"], m.get("value"), m["available"])

async def demarrer(plugin, client):
    await client.subscribe([krissvia.setting("Platine:Thermostat")])        # renvoyé seul à chaque reconnexion
    r = await client.command(krissvia.setting("Platine:Thermostat"), "set_mode", {"mode": "eco"})
    # r["outcome"] : accepted / failed / unreachable ; r["verification"], r["detail"]
```

Une entité non choisie est refusée (`not_allowed`, dit au journal) ; un ordre hors session ou sans réponse rend
`unreachable`, jamais un faux succès.

## 5. Tests

```bash
python -m unittest discover -s sdk/python
python -m unittest discover -s plugins/Krissvia.Plugin.Meteo
```

Contre un faux cœur WebSocket sur la boucle locale : poignée de main et resynchronisation, ordres réussis et
en échec, refus suivi d'une attente, cœur muet suivi d'une reconnexion, messages sans champs vides, lecture des
réglages, arrêt à la disparition du cœur parent (et pas sans la variable). Ceux du plugin Météo n'appellent pas le réseau : lecture des lieux (et de l'ancien réglage d'un seul
lieu), traduction d'une réponse d'Open-Meteo, instantané.

## 6. Le plugin Météo

- Un appareil par lieu. Son entité `…_meteo` (`device_class: weather`) a pour valeur un objet : `temperature`,
  `apparent_temperature`, `humidity`, `wind_speed`, `precipitation`, `pressure`, `code` (WMO), `condition`,
  `is_day`, `sunrise`, `sunset` (heure locale du lieu) et `forecast` (7 jours : `date`, `code`, `condition`, `max`,
  `min`, `precipitation_probability`). Le widget `meteo` la dessine.
- La température, l'humidité, la pression et le vent sont aussi publiés en entités numériques : ce sont elles
  qu'un thermostat prend comme température extérieure, et qu'un graphique suivra.
- L'identifiant d'un lieu est tiré de son nom s'il est vide ; il fixe les identifiants des entités, donc ne plus
  le changer. Sans aucun lieu, le plugin relit l'ancien réglage d'un seul lieu (appareil `meteo`).

## 7. Anticipation météo des thermostats

L'entité météo porte aussi `hourly` : la température prévue heure par heure sur 48 h, heures en UTC
(`[{"time": "2026-10-19T12:00:00Z", "temperature": 6.4}]`). Un thermostat peut la choisir comme **prévision
météo** (écran de configuration, onglet Général) :

- Le feed-forward `Ki × (consigne − extérieur)` prend alors la **moyenne prévue sur les 2 prochaines heures**
  (échantillonnée toutes les 30 min, interpolée) au lieu de la température mesurée : une maison réagit au froid
  avec des heures de retard, la mesure arrive quand le refroidissement a déjà commencé. C'est le « palier ③ »
  de DomoMud, sauf qu'ici chaque thermostat choisit son lieu au lieu d'un lieu unique « qui pilote le chauffage ».
- La mesure reste celle qu'on affiche ; la carte du thermostat montre « (prév. x) » quand l'écart atteint 0,5 °C.
- Une prévision indisponible, plus vieille que 3 h, ou qui ne couvre pas les 2 h à venir est ignorée : le moteur
  reprend la sonde extérieure (`OutdoorForecast`, fonction pure couverte par les tests).
- L'interrupteur « Anticiper avec la prévision » garde l'entité choisie mais revient à la mesure seule, pour
  comparer avec et sans.
- La prévision corrige aussi le **préchauffage anticipé** : avant un créneau plus chaud du planning, le moteur
  chauffe en avance de (consigne à venir − ambiante) × pente, la pente (min/°C) étant apprise sur les montées à
  pleine puissance. Par froid annoncé pendant la montée, la pente s'allonge : `Ki × (consigne − extérieur)` est la
  part de puissance qui sert à tenir la consigne, le reste sert à monter, et la durée suit l'inverse de ce reste
  (facteur borné à [0,5 ; 3]). Statut « Préchauffage » pendant ce temps ; avance de 3 h au plus.
