"""SDK Python des plugins Krissvia : la connexion au cœur, en un fichier.

Fait la même chose que ``CoreClient`` du SDK C# (docs/TRANSPORT.md en est la spécification) :
hello → welcome, resynchronisation complète à chaque (re)connexion, battement de cœur dans les deux
sens, exécution des ordres, et reconnexion sans fin. Le plugin, lui, ne fait que décrire ses
appareils, publier ses mesures et exécuter les ordres.

Un plugin lancé par le cœur (page Services) trouve ce module tout seul : le cœur ajoute son dossier
au PYTHONPATH. Seule dépendance : le paquet ``websockets`` (13 ou plus).

Exemple minimal ::

    import asyncio, krissvia

    class Cave(krissvia.Plugin):
        def snapshot(self):
            return [krissvia.declare(krissvia.device("cave", "Cave", [
                krissvia.entity("cave:temperature", "sensor", "Température",
                                device_class="temperature", unit="°C", state_class="measurement")]))]

    asyncio.run(krissvia.CoreClient(Cave(), plugin_version="1.0.0").run())
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import sys
from typing import Any, Iterable

try:
    from websockets.asyncio.client import connect
    from websockets.exceptions import ConnectionClosed, InvalidHandshake, InvalidURI
except ImportError:  # pragma: no cover - dépend de l'installation
    sys.exit("Le SDK Krissvia demande le paquet « websockets » (13 ou plus) : pip install websockets")

CONTRACT_VERSION = "0.2"
"""Version du contrat parlée par ce SDK : le cœur exige la même mineure tant qu'on est en 0.x."""

MAX_FRAME = 1 << 20
"""Une trame de plus de 1 Mio ferme la connexion côté cœur (docs/TRANSPORT.md §3)."""

log = logging.getLogger("krissvia")

Message = dict[str, Any]


# ── Réglages ─────────────────────────────────────────────────────────────────


def setting(name: str, default: Any = None, convert=str) -> Any:
    """Un réglage de la page Services, transmis par le cœur en variable d'environnement.

    ``setting("Meteo:Latitude", convert=float)`` lit ``Meteo__Latitude``, la même convention que la
    configuration .NET. Absent ou vide : ``default``. Une valeur que ``convert`` refuse lève
    ``ValueError`` avec le nom du réglage, pour un journal lisible.
    """
    brut = os.environ.get(name.replace(":", "__"))
    if brut is None or brut == "":
        return default
    if convert is bool:
        return brut.lower() in ("true", "1", "oui", "yes")
    try:
        return convert(brut)
    except (TypeError, ValueError) as ex:
        raise ValueError(f"réglage {name} invalide : « {brut} »") from ex


# ── Messages ─────────────────────────────────────────────────────────────────
# Les champs absents ne sont pas écrits (contrat : jamais de null) ; ces fonctions s'en chargent.


def _sans_vides(d: dict[str, Any]) -> dict[str, Any]:
    return {k: v for k, v in d.items() if v is not None}


def entity(unique_id: str, kind: str, name: str | None = None, *, device_class: str | None = None,
           unit: str | None = None, state_class: str | None = None, commands: Iterable[str] = (),
           extra: dict[str, Any] | None = None) -> dict[str, Any]:
    """Une entité. ``unique_id`` est immuable et unique dans le plugin : jamais un nom affiché.

    ``kind`` : ``sensor``, ``binary_sensor``, ``switch``, ``light``, ``cover``, ``event``.
    Unités normalisées : °C, %, W, VA, kWh, hPa… (docs/CONTRACT-V0.md, Conventions).
    """
    return _sans_vides({"unique_id": unique_id, "kind": kind, "name": name, "device_class": device_class,
                        "unit": unit, "state_class": state_class, "commands": list(commands), "extra": extra})


def device(unique_id: str, name: str | None = None, entities: Iterable[dict[str, Any]] = (), *,
           manufacturer: str | None = None, model: str | None = None) -> dict[str, Any]:
    """Un appareil et ses entités."""
    return _sans_vides({"unique_id": unique_id, "name": name, "manufacturer": manufacturer, "model": model,
                        "entities": list(entities)})


def declare(*devices: dict[str, Any]) -> Message:
    """L'instantané COMPLET des appareils du plugin : ce qui n'y est plus devient « manquant » au registre."""
    return {"type": "declare", "devices": list(devices)}


def value(entity_unique_id: str, val: Any, timestamp: str | None = None) -> dict[str, Any]:
    """Une mesure : nombre (sensor), booléen (binary_sensor, switch), objet (light, cover), chaîne (event)."""
    return _sans_vides({"entity_unique_id": entity_unique_id, "value": val, "timestamp": timestamp})


def state(*values: dict[str, Any]) -> Message:
    """Un lot de mesures."""
    return {"type": "state", "states": list(values)}


def availability(online: bool, *, entity_unique_id: str | None = None,
                 device_unique_id: str | None = None) -> Message:
    """Joignabilité d'une entité, d'un appareil, ou du plugin entier (ni l'un ni l'autre)."""
    return _sans_vides({"type": "availability", "online": online, "entity_unique_id": entity_unique_id,
                        "device_unique_id": device_unique_id})


def accepted(detail: str | None = None) -> dict[str, Any]:
    """Résultat d'un ordre transmis (et acquitté si le protocole le permet). Il ne prouve pas l'effet :
    publier ensuite l'état résultant de l'entité."""
    return _sans_vides({"outcome": "accepted", "detail": detail})


def failed(detail: str) -> dict[str, Any]:
    return {"outcome": "failed", "detail": detail}


def unreachable(detail: str | None = None) -> dict[str, Any]:
    """L'appareil n'a pas pu être joint : le cœur ne supposera pas que l'ordre est passé."""
    return _sans_vides({"outcome": "unreachable", "detail": detail})


# ── Plugin ───────────────────────────────────────────────────────────────────


class Plugin:
    """Ce qu'un plugin fournit au ``CoreClient``."""

    def snapshot(self) -> list[Message]:
        """Tout ce que le cœur doit savoir à l'ouverture d'une session : ``declare``, puis TOUS les
        états connus, puis les joignabilités. Le cœur a pu redémarrer : rien d'autre ne le
        resynchronise (contrat 0.2 §1)."""
        raise NotImplementedError

    async def execute(self, command: Message) -> dict[str, Any]:
        """Exécute un ordre (``command["entity_unique_id"]``, ``command["command"]``,
        ``command.get("args")``) et renvoie ``accepted()``, ``failed(...)`` ou ``unreachable()``.
        Une exception devient un ``failed``."""
        return failed(f"commande « {command.get('command')} » non prise en charge")


# ── Connexion au cœur ────────────────────────────────────────────────────────


class CoreClient:
    """Connexion d'un plugin au cœur, et reconnexion jusqu'à l'arrêt du processus.

    Adresse, jeton et identifiant viennent par défaut des variables que pose le cœur au lancement
    (``Krissvia__Url``, ``Krissvia__Token``, ``Krissvia__PluginId``) : un plugin lancé depuis la page
    Services n'a rien à configurer pour se connecter.
    """

    def __init__(self, plugin: Plugin, *, plugin_version: str = "0.0.0", url: str | None = None,
                 plugin_id: str | None = None, token: str | None = None,
                 reconnect_delays: tuple[float, ...] = (1, 2, 5, 10, 30), rejected_delay: float = 60,
                 welcome_timeout: float = 10) -> None:
        self.plugin = plugin
        self.plugin_version = plugin_version
        self.url = url or os.environ.get("Krissvia__Url")
        self.plugin_id = plugin_id or os.environ.get("Krissvia__PluginId", "")
        self.token = token if token is not None else os.environ.get("Krissvia__Token")
        self.reconnect_delays = reconnect_delays
        self.rejected_delay = rejected_delay
        self.welcome_timeout = welcome_timeout
        self._envoi = asyncio.Lock()
        self._session = None
        self._derniere_reception = 0.0
        self._ordres: set[asyncio.Task] = set()

    @property
    def connected(self) -> bool:
        """Vrai entre le welcome (resynchronisation envoyée) et la fin de la session."""
        return self._session is not None

    async def send(self, message: Message) -> bool:
        """Envoie un message si une session est ouverte. Sinon il est abandonné, et c'est voulu : la
        resynchronisation de la prochaine session portera l'état à jour."""
        async with self._envoi:
            ws = self._session
            if ws is None:
                return False
            try:
                await ws.send(json.dumps(message, ensure_ascii=False))
                return True
            except ConnectionClosed:
                return False  # la réception constatera la coupure et reconnectera

    async def run(self) -> None:
        """Se connecte, et se reconnecte, jusqu'à l'annulation de la tâche."""
        if not self.url:
            raise ValueError("Adresse du cœur non configurée (variable Krissvia__Url).")
        tentative = 0
        while True:
            fin = "echec"
            try:
                # Pas de ping WebSocket : le contrat a son propre battement, dans les deux sens.
                async with connect(self.url, max_size=MAX_FRAME, ping_interval=None, open_timeout=10) as ws:
                    fin = await self._session_async(ws)
            except (OSError, ConnectionClosed, InvalidHandshake, InvalidURI, asyncio.TimeoutError) as ex:
                log.warning("Cœur injoignable (%s) : %s.", self.url, ex)

            tentative = 0 if fin == "normale" else tentative + 1
            attente = (self.rejected_delay if fin == "refus"
                       else self.reconnect_delays[min(max(tentative - 1, 0), len(self.reconnect_delays) - 1)])
            await asyncio.sleep(attente)

    async def _session_async(self, ws) -> str:
        await ws.send(json.dumps(_sans_vides({
            "type": "hello", "plugin_id": self.plugin_id, "plugin_version": self.plugin_version,
            "contract_version": CONTRACT_VERSION, "token": self.token,
        })))

        try:
            premiere = json.loads(await asyncio.wait_for(ws.recv(), self.welcome_timeout))
        except asyncio.TimeoutError:
            log.warning("Le cœur n'a pas répondu au hello.")
            return "echec"
        except ValueError:
            log.warning("Réponse illisible du cœur au hello.")
            return "echec"

        if premiere.get("type") == "rejected":
            log.error("Le cœur refuse la connexion : %s, %s. Vérifier le jeton et la version du plugin.",
                      premiere.get("reason"), premiere.get("detail"))
            return "refus"
        if premiere.get("type") != "welcome":
            log.warning("Réponse inattendue du cœur au hello.")
            return "echec"

        # La resynchronisation part sous le verrou d'envoi : une mesure émise pendant ce temps attend, et
        # arrive donc APRÈS l'instantané au lieu d'être écrasée par lui.
        async with self._envoi:
            for message in self.plugin.snapshot():
                await ws.send(json.dumps(message, ensure_ascii=False))
            self._session = ws
        log.info("Connecté au cœur Krissvia (contrat %s).", premiere.get("contract_version"))

        boucle = asyncio.get_running_loop()
        self._derniere_reception = boucle.time()
        battement = asyncio.create_task(self._battement(ws, float(premiere.get("heartbeat_seconds", 15)),
                                                        float(premiere.get("timeout_seconds", 45))))
        try:
            async for trame in ws:
                self._derniere_reception = boucle.time()
                if not isinstance(trame, str):
                    continue
                try:
                    message = json.loads(trame)
                except ValueError:
                    log.warning("Trame illisible ignorée.")
                    continue

                genre = message.get("type")
                if genre == "command":
                    # Hors de la boucle de réception : un ordre lent ne retarde pas les suivants.
                    tache = asyncio.create_task(self._executer(message))
                    self._ordres.add(tache)
                    tache.add_done_callback(self._ordres.discard)
                elif genre == "configure":
                    await self.send({"type": "configure_result", "ok": False,
                                     "error": "configuration locale du plugin uniquement"})
                elif genre == "shutdown":
                    log.info("Le cœur ferme la session (%s).", message.get("reason"))
                    return "normale"
                # ping, et tout type inconnu : la réception suffit (contrat 0.2 §1).
        except ConnectionClosed:
            pass  # coupure : run() reconnecte
        finally:
            battement.cancel()
            async with self._envoi:
                self._session = None
            log.warning("Session avec le cœur fermée ; reconnexion.")
        return "normale"

    async def _executer(self, ordre: Message) -> None:
        try:
            resultat = await self.plugin.execute(ordre)
        except Exception as ex:  # noqa: BLE001 - toute erreur du plugin devient un « failed »
            log.exception("Ordre %s sur « %s » en échec.", ordre.get("command"), ordre.get("entity_unique_id"))
            resultat = failed(str(ex) or type(ex).__name__)
        await self.send({**resultat, "type": "command_result", "id": ordre.get("id")})

    async def _battement(self, ws, periode: float, delai_mort: float) -> None:
        boucle = asyncio.get_running_loop()
        try:
            while True:
                await asyncio.sleep(periode)
                silence = boucle.time() - self._derniere_reception
                if silence > delai_mort:
                    log.warning("Cœur muet depuis %d s : reconnexion.", silence)
                    await ws.close()
                    return
                await self.send({"type": "ping"})
        except asyncio.CancelledError:
            pass


def run(plugin: Plugin, *tasks, plugin_version: str = "0.0.0", **options) -> None:
    """Point d'entrée d'un plugin : journal sur la sortie (repris par la page Services), connexion au
    cœur, et les tâches du plugin (fonctions ``async`` qui reçoivent le client) en parallèle.

    ``krissvia.run(Meteo(), Meteo.relever, plugin_version="1.0.0")``
    """
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s : %(message)s", stream=sys.stdout)
    client = CoreClient(plugin, plugin_version=plugin_version, **options)

    async def principal() -> None:
        await asyncio.gather(client.run(), *(tache(plugin, client) for tache in tasks))

    try:
        asyncio.run(principal())
    except KeyboardInterrupt:
        pass
