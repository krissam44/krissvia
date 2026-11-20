"""Plugin Matter : les appareils Matter (prises, interrupteurs, capteurs) vus par un serveur Matter.

Krissvia ne parle pas Matter lui-même : le protocole (chiffrement, certificats, réseau maillé) est l'affaire d'un
contrôleur dédié, le **Matter Server** de l'Open Home Foundation (https://github.com/matter-js/matterjs-server, le
successeur de python-matter-server, archivé en juin 2026). Ce plugin dialogue avec lui par son API WebSocket, comme
le plugin Zigbee2MQTT dialogue avec Zigbee2MQTT. Voir docs/MATTER.md.

Un appareil Krissvia par nœud Matter, avec ce que ses points d'accès (« endpoints ») exposent :

- marche/arrêt (cluster OnOff) : un ``switch``, ou une ``light`` si l'appareil se déclare lumière ;
- mesure d'énergie : puissance (W), tension (V), courant (A), énergie cumulée (kWh) ;
- capteurs : température, humidité, luminosité, occupation, contact (ouverture), batterie.

Plus un appareil « Matter », dont l'entité ``matter:controleur`` dit si le serveur est joignable, liste les nœuds,
et porte la commande ``commission`` qui appaire un nouvel appareil à partir de son code (carte « Matter »).
"""

import asyncio
import itertools
import json
import logging
import math
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import krissvia

VERSION = "0.1.0"
URL_PAR_DEFAUT = "ws://localhost:5580/ws"
CONTROLEUR = "matter"
ENTITE_CONTROLEUR = "matter:controleur"

log = logging.getLogger("matter")

# ── Clusters et attributs (vérifiés dans le modèle Matter de matter.js) ─────────────────────────────────────────

ON_OFF, LEVEL = 6, 8
DESCRIPTOR = 29
BASIC, BRIDGED_BASIC = 40, 57
POWER_SOURCE = 47
BOOLEAN_STATE = 69
POWER, ENERGY = 144, 145
ILLUMINANCE, TEMPERATURE, HUMIDITY, OCCUPANCY = 1024, 1026, 1029, 1030

# Types d'appareil (Descriptor.DeviceTypeList) qui comptent pour le choix de l'entité.
LUMIERES = {0x0100, 0x0101, 0x010C, 0x010D}       # On/Off, Dimmable, Color Temperature, Extended Color Light
CONTACT, FUITE = 0x0015, 0x0043                     # Contact Sensor, Water Leak Detector

# Grandeurs numériques : (cluster, attribut, suffixe, nom, classe, unité, conversion de la valeur Matter).
MESURES = [
    (POWER, 8, "puissance", "Puissance", "power", "W", lambda v: round(v / 1000, 1)),          # mW
    (POWER, 4, "tension", "Tension", "voltage", "V", lambda v: round(v / 1000, 1)),            # mV
    (POWER, 5, "courant", "Courant", "current", "A", lambda v: round(v / 1000, 3)),            # mA
    (TEMPERATURE, 0, "temperature", "Température", "temperature", "°C", lambda v: round(v / 100, 2)),
    (HUMIDITY, 0, "humidite", "Humidité", "humidity", "%", lambda v: round(v / 100, 1)),
    (POWER_SOURCE, 12, "batterie", "Batterie", "battery", "%", lambda v: round(v / 2)),        # demi-points
    # Échelle logarithmique de Matter : 10 000 × log10(lux) + 1.
    (ILLUMINANCE, 0, "luminosite", "Luminosité", "illuminance", "lx",
     lambda v: round(10 ** ((v - 1) / 10000)) if v > 0 else 0),
]


def chemin(endpoint: int, cluster: int, attribut: int) -> str:
    return f"{endpoint}/{cluster}/{attribut}"


def champ(valeur: Any, numero: int, nom: str) -> Any:
    """Un champ de structure : par numéro (ce qu'envoie le serveur, « tag-based ») ou par nom (anciens serveurs)."""
    if isinstance(valeur, dict):
        return valeur.get(str(numero), valeur.get(nom))
    return None


def kwh(valeur: Any) -> float | None:
    """CumulativeEnergyImported : une structure dont le champ 0 (Energy) est en mWh."""
    energie = champ(valeur, 0, "energy")
    return round(energie / 1_000_000, 3) if isinstance(energie, (int, float)) else None


def occupe(valeur: Any) -> bool | None:
    """OccupancyBitmap : bit 0 « occupied », en nombre ou, selon le serveur, en objet."""
    if isinstance(valeur, bool):
        return valeur
    if isinstance(valeur, int):
        return bool(valeur & 1)
    if isinstance(valeur, dict):
        return bool(champ(valeur, 0, "occupied"))
    return None


def types_appareil(attributs: dict, endpoint: int) -> set[int]:
    """Les types d'appareil d'un endpoint (Descriptor.DeviceTypeList : [{"0": type, "1": révision}])."""
    liste = attributs.get(chemin(endpoint, DESCRIPTOR, 0)) or []
    types = set()
    for element in liste:
        t = champ(element, 0, "deviceType")
        if isinstance(t, int):
            types.add(t)
    return types


def endpoints(attributs: dict) -> list[int]:
    return sorted({int(p.split("/")[0]) for p in attributs})


# ── Traduction d'un nœud (fonctions pures, testées) ──────────────────────────────────────────────────────────

@dataclass
class Entite:
    """Une entité Krissvia et la façon de calculer sa valeur à partir d'un attribut Matter."""
    unique_id: str
    descripteur: dict
    source: str                   # chemin de l'attribut Matter « ep/cluster/attr »
    convertir: Any                # valeur Matter → valeur Krissvia (None : rien à publier)
    endpoint: int = 0
    genre: str = "mesure"         # « onoff », « lumiere » ou « mesure »


@dataclass
class Noeud:
    node_id: int
    nom: str
    fabricant: str | None
    modele: str | None
    disponible: bool
    attributs: dict
    entites: list[Entite] = field(default_factory=list)

    @property
    def appareil(self) -> str:
        return f"noeud-{self.node_id}"

    def descripteur(self) -> dict:
        return krissvia.device(self.appareil, self.nom, [e.descripteur for e in self.entites],
                               manufacturer=self.fabricant, model=self.modele)

    def valeurs(self) -> dict[str, Any]:
        """Les valeurs connues de toutes les entités (une valeur Matter nulle n'est pas publiée)."""
        resultat = {}
        for e in self.entites:
            if (brut := self.attributs.get(e.source)) is not None and (v := e.convertir(brut)) is not None:
                resultat[e.unique_id] = v
        return resultat


def nom_du_noeud(node_id: int, attributs: dict) -> str:
    """Le nom donné à l'appareil (NodeLabel), sinon son produit, sinon « Matter n° … »."""
    for cle in (chemin(0, BASIC, 5), chemin(0, BASIC, 3)):
        if isinstance(v := attributs.get(cle), str) and v.strip():
            return v.strip()
    return f"Matter {node_id}"


def traduire(donnees: dict) -> Noeud:
    """Un nœud du serveur Matter (``get_nodes`` / ``node_added``) → l'appareil Krissvia et ses entités."""
    node_id = int(donnees["node_id"])
    attributs = dict(donnees.get("attributes") or {})
    noeud = Noeud(node_id=node_id, nom=nom_du_noeud(node_id, attributs),
                  fabricant=attributs.get(chemin(0, BASIC, 1)), modele=attributs.get(chemin(0, BASIC, 3)),
                  disponible=bool(donnees.get("available", False)), attributs=attributs)

    eps = [ep for ep in endpoints(attributs) if ep != 0] or [0]
    plusieurs = len([ep for ep in eps if chemin(ep, ON_OFF, 0) in attributs]) > 1

    def uid(ep: int, suffixe: str) -> str:
        return f"{node_id}:{ep}:{suffixe}"

    def nom(base: str, ep: int) -> str | None:
        # Un appareil à plusieurs voies distingue ses entités ; sinon le registre nomme « appareil + entité ».
        sous_nom = attributs.get(chemin(ep, BRIDGED_BASIC, 5))
        if isinstance(sous_nom, str) and sous_nom.strip():
            return f"{sous_nom.strip()} {base}".strip() if base else sous_nom.strip()
        return f"{base} {ep}".strip() if plusieurs else (base or None)

    for ep in eps:
        types = types_appareil(attributs, ep)
        if chemin(ep, ON_OFF, 0) in attributs:
            if types & LUMIERES:
                variateur = chemin(ep, LEVEL, 0) in attributs
                commandes = ["turn_on", "turn_off", "toggle"] + (["set_brightness"] if variateur else [])
                noeud.entites.append(Entite(
                    uid(ep, "lumiere"), krissvia.entity(uid(ep, "lumiere"), "light", nom("", ep), commands=commandes),
                    chemin(ep, ON_OFF, 0), lambda v, ep=ep: lumiere(attributs, ep, v), ep, "lumiere"))
            else:
                noeud.entites.append(Entite(
                    uid(ep, "onoff"), krissvia.entity(uid(ep, "onoff"), "switch", nom("", ep),
                                                      commands=["turn_on", "turn_off", "toggle"]),
                    chemin(ep, ON_OFF, 0), lambda v: bool(v) if isinstance(v, bool) else None, ep, "onoff"))

        for cluster, attribut, suffixe, libelle, classe, unite, convertir in MESURES:
            if chemin(ep, cluster, attribut) in attributs:
                noeud.entites.append(Entite(
                    uid(ep, suffixe), krissvia.entity(uid(ep, suffixe), "sensor", nom(libelle, ep), device_class=classe,
                                                      unit=unite, state_class="measurement"),
                    chemin(ep, cluster, attribut), nombre(convertir), ep))

        if chemin(ep, ENERGY, 1) in attributs:
            noeud.entites.append(Entite(
                uid(ep, "energie"), krissvia.entity(uid(ep, "energie"), "sensor", nom("Énergie", ep), device_class="energy",
                                                    unit="kWh", state_class="total_increasing"),
                chemin(ep, ENERGY, 1), kwh, ep))

        if chemin(ep, OCCUPANCY, 0) in attributs:
            noeud.entites.append(Entite(
                uid(ep, "occupation"), krissvia.entity(uid(ep, "occupation"), "binary_sensor", nom("Occupation", ep),
                                                       device_class="occupancy"),
                chemin(ep, OCCUPANCY, 0), occupe, ep))

        if chemin(ep, BOOLEAN_STATE, 0) in attributs:
            if FUITE in types:
                noeud.entites.append(Entite(
                    uid(ep, "fuite"), krissvia.entity(uid(ep, "fuite"), "binary_sensor", nom("Fuite", ep),
                                                      device_class="moisture"),
                    chemin(ep, BOOLEAN_STATE, 0), lambda v: bool(v) if isinstance(v, bool) else None, ep))
            else:
                # Contact Sensor : StateValue vrai = contact établi, donc FERMÉ ; le contrat dit vrai = ouvert.
                noeud.entites.append(Entite(
                    uid(ep, "ouverture"), krissvia.entity(uid(ep, "ouverture"), "binary_sensor", nom("Ouverture", ep),
                                                          device_class="opening"),
                    chemin(ep, BOOLEAN_STATE, 0), lambda v: (not v) if isinstance(v, bool) else None, ep))
    return noeud


def nombre(convertir):
    """Applique une conversion à un nombre ; tout le reste (null, structure inattendue) ne publie rien."""
    return lambda v: convertir(v) if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) else None


def lumiere(attributs: dict, ep: int, allumee: Any) -> dict | None:
    """Une lumière : ``{"on": …}``, plus ``brightness`` (0-100) si elle a un variateur (LevelControl, 0-254)."""
    if not isinstance(allumee, bool):
        return None
    valeur = {"on": allumee}
    niveau = attributs.get(chemin(ep, LEVEL, 0))
    if isinstance(niveau, int):
        valeur["brightness"] = round(niveau * 100 / 254)
    return valeur


def ordre(entite: Entite, commande: str, args: dict | None) -> tuple[int, str, dict]:
    """Une commande Krissvia → (cluster, commande Matter, charge utile). ValueError si elle ne s'applique pas."""
    if commande == "turn_on":
        return ON_OFF, "On", {}
    if commande == "turn_off":
        return ON_OFF, "Off", {}
    if commande == "toggle":
        return ON_OFF, "Toggle", {}
    if commande == "set_brightness" and entite.genre == "lumiere":
        pourcent = (args or {}).get("brightness")
        if not isinstance(pourcent, (int, float)) or not 0 <= pourcent <= 100:
            raise ValueError("brightness attendu entre 0 et 100")
        return LEVEL, "MoveToLevelWithOnOff", {"level": round(pourcent * 254 / 100), "transitionTime": 0,
                                                "optionsMask": 0, "optionsOverride": 0}
    raise ValueError(f"commande « {commande} » non prise en charge par {entite.unique_id}")


# ── Dialogue avec le serveur Matter ──────────────────────────────────────────────────────────────────────────

class ErreurServeur(Exception):
    """Une réponse ``{error_code, details}`` du serveur Matter."""

    def __init__(self, code: int, details: str):
        super().__init__(f"{details} (code {code})")
        self.code = code


class Serveur:
    """Une session WebSocket avec le serveur Matter : requêtes numérotées, réponses, événements."""

    def __init__(self, ws):
        self.ws = ws
        self._numeros = itertools.count(1)
        self._attentes: dict[str, asyncio.Future] = {}

    async def demander(self, commande: str, args: dict | None = None, delai: float = 30) -> Any:
        numero = str(next(self._numeros))
        attente = asyncio.get_running_loop().create_future()
        self._attentes[numero] = attente
        message = {"message_id": numero, "command": commande}
        if args is not None:
            message["args"] = args
        try:
            await self.ws.send(json.dumps(message))
            return await asyncio.wait_for(attente, delai)
        finally:
            self._attentes.pop(numero, None)

    def recevoir(self, message: dict) -> dict | None:
        """Range une réponse avec sa requête ; rend l'événement s'il en est un."""
        if "event" in message:
            return message
        attente = self._attentes.get(str(message.get("message_id")))
        if attente is not None and not attente.done():
            if "error_code" in message:
                attente.set_exception(ErreurServeur(message["error_code"], message.get("details", "")))
            else:
                attente.set_result(message.get("result"))
        return None

    def abandonner(self):
        for attente in self._attentes.values():
            if not attente.done():
                attente.set_exception(ConnectionError("connexion au serveur Matter perdue"))


# ── Le plugin ────────────────────────────────────────────────────────────────────────────────────────────────

class Matter(krissvia.Plugin):
    def __init__(self, url: str, wifi_ssid: str | None = None, wifi_mot_de_passe: str | None = None):
        self.url = url
        self.wifi = (wifi_ssid, wifi_mot_de_passe) if wifi_ssid and wifi_mot_de_passe else None
        self.noeuds: dict[int, Noeud] = {}
        self.serveur: Serveur | None = None
        self.infos: dict = {}
        self.appairage: dict = {"state": "idle"}
        self.client: krissvia.CoreClient | None = None
        self._declaration: str | None = None
        self._taches: set[asyncio.Task] = set()

    # Ce que voit le cœur ────────────────────────────────────────────────────────

    def appareils(self) -> list[dict]:
        controleur = krissvia.device(CONTROLEUR, "Matter", [
            krissvia.entity(ENTITE_CONTROLEUR, "sensor", "Contrôleur", device_class="matter_controller",
                            commands=["commission"]),
        ], manufacturer="Open Home Foundation", model="Matter Server")
        return [controleur] + [n.descripteur() for n in sorted(self.noeuds.values(), key=lambda n: n.node_id)]

    def etat_controleur(self) -> dict:
        return {
            "connected": self.serveur is not None,
            "url": self.url,
            "sdk_version": self.infos.get("sdk_version"),
            "fabric_id": self.infos.get("fabric_id"),
            "bluetooth_enabled": bool(self.infos.get("bluetooth_enabled")),
            "wifi_credentials_set": bool(self.infos.get("wifi_credentials_set")),
            "nodes": [{"node_id": n.node_id, "name": n.nom, "available": n.disponible}
                      for n in sorted(self.noeuds.values(), key=lambda n: n.node_id)],
            "commissioning": self.appairage,
        }

    def snapshot(self):
        messages = [krissvia.declare(*self.appareils())]
        valeurs = [krissvia.value(ENTITE_CONTROLEUR, self.etat_controleur())]
        valeurs += [krissvia.value(u, v) for n in self.noeuds.values() for u, v in n.valeurs().items()]
        messages.append(krissvia.state(*valeurs))
        messages += [krissvia.availability(self.serveur is not None and n.disponible, device_unique_id=n.appareil)
                     for n in self.noeuds.values()]
        return messages

    async def envoyer(self, *messages):
        if self.client is not None:
            for m in messages:
                await self.client.send(m)

    async def redeclarer_si_change(self):
        """Redéclare les appareils si leur liste ou leurs entités ont changé (nœud ajouté, retiré, réinterrogé)."""
        appareils = self.appareils()
        empreinte = json.dumps(appareils, sort_keys=True)
        if empreinte != self._declaration:
            self._declaration = empreinte
            await self.envoyer(krissvia.declare(*appareils))

    async def publier_controleur(self):
        await self.envoyer(krissvia.state(krissvia.value(ENTITE_CONTROLEUR, self.etat_controleur())))

    async def publier_noeud(self, noeud: Noeud):
        valeurs = [krissvia.value(u, v) for u, v in noeud.valeurs().items()]
        if valeurs:
            await self.envoyer(krissvia.state(*valeurs))
        await self.envoyer(krissvia.availability(self.serveur is not None and noeud.disponible,
                                                 device_unique_id=noeud.appareil))

    # Événements du serveur ──────────────────────────────────────────────────────

    async def accueillir(self, donnees: dict):
        noeud = traduire(donnees)
        self.noeuds[noeud.node_id] = noeud
        await self.redeclarer_si_change()
        await self.publier_noeud(noeud)
        await self.publier_controleur()

    async def evenement(self, message: dict):
        genre, donnees = message.get("event"), message.get("data")
        if genre in ("node_added", "node_updated") and isinstance(donnees, dict):
            await self.accueillir(donnees)
        elif genre == "node_removed":
            if self.noeuds.pop(int(donnees), None) is not None:
                await self.redeclarer_si_change()
                await self.publier_controleur()
        elif genre == "attribute_updated" and isinstance(donnees, list) and len(donnees) == 3:
            await self.attribut(int(donnees[0]), str(donnees[1]), donnees[2])
        elif genre == "server_info_updated" and isinstance(donnees, dict):
            self.infos = donnees
            await self.publier_controleur()

    async def attribut(self, node_id: int, source: str, valeur: Any):
        noeud = self.noeuds.get(node_id)
        if noeud is None:
            return
        noeud.attributs[source] = valeur
        # Le nom et les types d'appareil changent les entités ; un niveau de variateur, la valeur de la lumière.
        if source.startswith("0/40/") or "/29/" in source:
            self.noeuds[node_id] = traduire({"node_id": node_id, "attributes": noeud.attributs, "available": noeud.disponible})
            await self.redeclarer_si_change()
            await self.publier_noeud(self.noeuds[node_id])
            return
        a_publier = [krissvia.value(e.unique_id, v) for e in noeud.entites
                     if (e.source == source or (e.genre == "lumiere" and source == chemin(e.endpoint, LEVEL, 0)))
                     and (brut := noeud.attributs.get(e.source)) is not None and (v := e.convertir(brut)) is not None]
        if a_publier:
            await self.envoyer(krissvia.state(*a_publier))

    # Session avec le serveur ────────────────────────────────────────────────────

    async def session(self, ws):
        """Une connexion : l'accueil (ServerInfo), le Wi-Fi si réglé, la liste des nœuds, puis les événements."""
        serveur = Serveur(ws)
        self.infos = json.loads(await asyncio.wait_for(ws.recv(), 10))
        log.info("Serveur Matter %s (fabric %s, Bluetooth %s).", self.infos.get("sdk_version"),
                 self.infos.get("fabric_id"), "actif" if self.infos.get("bluetooth_enabled") else "inactif")

        lecteur = asyncio.create_task(self.lire(serveur))
        try:
            self.serveur = serveur
            if self.wifi is not None:
                await serveur.demander("set_wifi_credentials", {"ssid": self.wifi[0], "credentials": self.wifi[1]})
                self.infos["wifi_credentials_set"] = True
            noeuds = await serveur.demander("start_listening") or []
            self.noeuds = {n.node_id: n for n in map(traduire, noeuds)}
            log.info("%d nœud(s) Matter : %s", len(self.noeuds), ", ".join(n.nom for n in self.noeuds.values()) or "aucun")
            await self.redeclarer_si_change()
            for noeud in self.noeuds.values():
                await self.publier_noeud(noeud)
            await self.publier_controleur()
            await lecteur
        finally:
            lecteur.cancel()
            serveur.abandonner()
            self.serveur = None

    async def lire(self, serveur: Serveur):
        async for brut in serveur.ws:
            try:
                message = json.loads(brut)
            except ValueError:
                log.warning("Message illisible du serveur Matter : %.200s", brut)
                continue
            if (evenement := serveur.recevoir(message)) is not None:
                try:
                    await self.evenement(evenement)
                except Exception:  # noqa: BLE001 - un événement mal formé ne doit pas couper la session
                    log.exception("Événement Matter non traité : %.300s", brut)

    async def relier(self, client: krissvia.CoreClient):
        """Se connecte au serveur Matter, et s'y reconnecte indéfiniment (5 s, puis jusqu'à 60 s)."""
        from websockets.asyncio.client import connect

        self.client = client
        attente = 5
        while True:
            try:
                async with connect(self.url, max_size=None, open_timeout=10) as ws:
                    attente = 5
                    await self.session(ws)
                log.warning("Serveur Matter : connexion fermée.")
            except asyncio.CancelledError:
                raise
            except Exception as ex:  # noqa: BLE001 - serveur absent, refus, coupure : on réessaie
                log.warning("Serveur Matter injoignable (%s) : nouvel essai dans %d s.", ex, attente)
            # Hors connexion : les appareils sont indisponibles, l'état du contrôleur le dit.
            for noeud in self.noeuds.values():
                await self.envoyer(krissvia.availability(False, device_unique_id=noeud.appareil))
            await self.publier_controleur()
            await asyncio.sleep(attente)
            attente = min(attente * 2, 60)

    # Ordres ──────────────────────────────────────────────────────────────────────

    async def execute(self, command):
        cible, commande, args = command.get("entity_unique_id"), command.get("command"), command.get("args")
        if cible == ENTITE_CONTROLEUR:
            return self.appairer(args)

        entite, noeud = next(((e, n) for n in self.noeuds.values() for e in n.entites if e.unique_id == cible), (None, None))
        if entite is None:
            return krissvia.failed(f"entité inconnue : {cible}")
        try:
            cluster, nom, charge = ordre(entite, commande, args)
        except ValueError as ex:
            return krissvia.failed(str(ex))
        if self.serveur is None:
            return krissvia.unreachable("serveur Matter injoignable")
        try:
            await self.serveur.demander("device_command", {"node_id": noeud.node_id, "endpoint_id": entite.endpoint,
                                                          "cluster_id": cluster, "command_name": nom, "payload": charge})
        except ErreurServeur as ex:
            return krissvia.unreachable(str(ex)) if not noeud.disponible else krissvia.failed(str(ex))
        except (ConnectionError, asyncio.TimeoutError) as ex:
            return krissvia.unreachable(str(ex) or "pas de réponse du serveur Matter")
        # L'état résultant arrive par attribute_updated : c'est lui qui fait foi.
        return krissvia.accepted()

    def appairer(self, args: dict | None):
        """Lance l'appairage d'un appareil à partir de son code (11 chiffres, ou la charge « MT:… » du QR code).

        Par défaut, l'appareil est supposé déjà sur le réseau : on l'a partagé depuis son appli (eWeLink, Alexa…), qui
        a donné un code d'appairage. Avec le Bluetooth actif sur le serveur et le Wi-Fi réglé, ``"bluetooth": true``
        appaire un appareil neuf directement.
        """
        code = str((args or {}).get("code") or "").replace(" ", "").replace("-", "").strip()
        if not code:
            return krissvia.failed("code d'appairage manquant")
        if self.serveur is None:
            return krissvia.unreachable("serveur Matter injoignable")
        if self.appairage.get("state") == "running":
            return krissvia.failed("un appairage est déjà en cours")
        bluetooth = bool((args or {}).get("bluetooth"))
        if bluetooth and not (self.infos.get("bluetooth_enabled") and self.infos.get("wifi_credentials_set")):
            return krissvia.failed("le Bluetooth demande un serveur lancé avec un adaptateur et le Wi-Fi réglé dans le plugin")

        # « En cours » dès maintenant, pas au démarrage de la tâche : un second clic aussitôt serait sinon accepté.
        self.appairage = {"state": "running", "started_at": _maintenant()}
        tache = asyncio.create_task(self._appairer(code, network_only=not bluetooth))
        self._taches.add(tache)
        tache.add_done_callback(self._taches.discard)
        return krissvia.accepted("appairage lancé : suivre son avancement sur la carte Matter")

    async def _appairer(self, code: str, network_only: bool):
        await self.publier_controleur()
        try:
            noeud = await self.serveur.demander("commission_with_code", {"code": code, "network_only": network_only},
                                                delai=300)
            nom = nom_du_noeud(int(noeud["node_id"]), noeud.get("attributes") or {}) if isinstance(noeud, dict) else "?"
            self.appairage = {"state": "done", "node_id": noeud.get("node_id") if isinstance(noeud, dict) else None,
                              "name": nom, "at": _maintenant()}
            log.info("Appairage réussi : %s.", nom)
            if isinstance(noeud, dict):
                await self.accueillir(noeud)
        except Exception as ex:  # noqa: BLE001 - toute erreur est montrée telle quelle sur la carte
            self.appairage = {"state": "failed", "detail": str(ex) or type(ex).__name__, "at": _maintenant()}
            log.warning("Appairage échoué : %s", ex)
        await self.publier_controleur()


def _maintenant() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def main():
    url = krissvia.setting("Matter:Url", URL_PAR_DEFAUT)
    plugin = Matter(url, krissvia.setting("Matter:WifiSsid"), krissvia.setting("Matter:WifiPassword"))
    krissvia.run(plugin, Matter.relier, plugin_version=VERSION)


if __name__ == "__main__":
    main()
