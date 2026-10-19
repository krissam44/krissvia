# Plugins en Python

Un plugin Krissvia est un processus séparé qui parle au cœur par WebSocket (`TRANSPORT.md`). Le SDK C#
n'est qu'une façon de le faire : `sdk/python/krissvia.py` en est l'équivalent Python, en un fichier, et
`plugins/Krissvia.Plugin.Meteo` l'exemple complet (Open-Meteo : température, humidité, pression).

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
valeur invalide lève une `ValueError` qui nomme le réglage. L'adresse du cœur, le jeton et l'identifiant
(`Krissvia__Url`, `Krissvia__Token`, `Krissvia__PluginId`) sont posés par le cœur : `CoreClient` les lit
seul.

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

## 5. Tests

```bash
python -m unittest discover -s sdk/python
```

Contre un faux cœur WebSocket sur la boucle locale : poignée de main et resynchronisation, ordres réussis et
en échec, refus suivi d'une attente, cœur muet suivi d'une reconnexion, messages sans champs vides, lecture des
réglages.
