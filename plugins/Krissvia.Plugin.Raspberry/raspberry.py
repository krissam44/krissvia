"""Plugin Raspberry : la santé de la machine qui fait tourner Krissvia.

Lu dans ``/proc`` et ``/sys``, sans dépendance : température du processeur, occupation du processeur, charge,
mémoire, swap, disque, taille de la base, durée de fonctionnement, l'état et la mémoire des services systemd
surveillés, et — sur un Raspberry Pi — la sous-tension et le bridage signalés par le microprogramme. Fonctionne sur
tout Linux ; ce qu'une machine ne sait pas dire n'est simplement pas déclaré.

Un appareil (le nom de la machine), avec :

- une entité « santé » (``device_class: system_health``) dont la valeur est un objet : c'est ce que dessine la carte
  livrée par le plugin (``widgets/raspberry``) ;
- des entités numériques (température du processeur en ``device_temperature`` — surtout pas ``temperature``, ce
  n'est pas une pièce —, processeur, mémoire, swap et disque en %, charge, taille de la base et mémoire de chaque
  service en Mo). Le cœur historise toute entité numérique : c'est l'historique qui montre une fuite de mémoire, une
  marche d'escalier par jour qu'aucun relevé isolé ne trahit ;
- des ``binary_sensor`` de classe ``problem`` : sous-tension et bridage EN CE MOMENT (Raspberry Pi), service arrêté,
  surchauffe, mémoire pleine, disque plein. La supervision du cœur alerte dès que l'un passe à vrai, et dit quand il
  repasse à faux.
"""

from __future__ import annotations

import asyncio
import logging
import os
import shutil
import socket
import subprocess
from dataclasses import dataclass, field

import krissvia

VERSION = "1.1.0"
log = logging.getLogger("raspberry")

# Bits de get_throttled (microprogramme du Raspberry Pi) : « en ce moment » et « depuis le démarrage ».
SOUS_TENSION, FREQUENCE_LIMITEE, BRIDE, LIMITE_TEMPERATURE = 0x1, 0x2, 0x4, 0x8
DEPUIS_DEMARRAGE = 16

# Où le noyau d'un Raspberry Pi expose get_throttled (noyaux récents) ; sinon, ``vcgencmd``.
GET_THROTTLED = "/sys/devices/platform/soc/soc:firmware/get_throttled"

# Le groupe de contrôle (cgroup v2) de chaque service système : sa mémoire, tous ses processus compris.
CGROUP_SERVICES = "/sys/fs/cgroup/system.slice"

MO = 1024 ** 2


def lire_texte(racine: str, chemin: str) -> str | None:
    try:
        with open(os.path.join(racine, chemin.lstrip("/")), encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return None


def temperature_processeur(racine: str) -> float | None:
    """En °C, depuis la première zone thermique (millidegrés)."""
    brut = lire_texte(racine, "/sys/class/thermal/thermal_zone0/temp")
    try:
        return round(int(brut.strip()) / 1000, 1) if brut else None
    except ValueError:
        return None


def compteurs_processeur(racine: str) -> tuple[int, int] | None:
    """(total, inactif) cumulés depuis le démarrage, première ligne de /proc/stat."""
    brut = lire_texte(racine, "/proc/stat")
    if not brut or not brut.startswith("cpu "):
        return None
    try:
        valeurs = [int(v) for v in brut.splitlines()[0].split()[1:]]
    except ValueError:
        return None
    inactif = valeurs[3] + (valeurs[4] if len(valeurs) > 4 else 0)   # idle + iowait
    return sum(valeurs), inactif


def occupation(avant: tuple[int, int] | None, apres: tuple[int, int] | None) -> float | None:
    """Part du temps processeur occupé entre deux relevés, en %."""
    if not avant or not apres or apres[0] <= avant[0]:
        return None
    total, inactif = apres[0] - avant[0], apres[1] - avant[1]
    return round(100 * (total - inactif) / total, 1)


def charges(racine: str) -> tuple[float, float, float] | None:
    """Charge moyenne sur 1, 5 et 15 minutes."""
    brut = lire_texte(racine, "/proc/loadavg")
    try:
        un, cinq, quinze = (float(v) for v in brut.split()[:3]) if brut else (None, None, None)
    except (ValueError, TypeError):
        return None
    return (un, cinq, quinze) if un is not None else None


def champs_meminfo(racine: str) -> dict[str, int]:
    """/proc/meminfo en kio, par nom de ligne."""
    brut = lire_texte(racine, "/proc/meminfo") or ""
    champs = {}
    for ligne in brut.splitlines():
        nom, _, reste = ligne.partition(":")
        if reste.strip():
            try:
                champs[nom] = int(reste.split()[0])
            except ValueError:
                pass
    return champs


def memoire(champs: dict[str, int]) -> float | None:
    """Mémoire occupée en %, d'après MemAvailable (ce que le noyau peut rendre sans échanger)."""
    total, libre = champs.get("MemTotal"), champs.get("MemAvailable")
    return round(100 * (total - libre) / total, 1) if total and libre is not None else None


def swap(champs: dict[str, int]) -> float | None:
    """Swap occupée en %. None sans swap : rien à surveiller, l'entité n'est pas déclarée."""
    total, libre = champs.get("SwapTotal"), champs.get("SwapFree")
    return round(100 * (total - libre) / total, 1) if total and libre is not None else None


def duree_fonctionnement(racine: str) -> int | None:
    brut = lire_texte(racine, "/proc/uptime")
    try:
        return int(float(brut.split()[0])) if brut else None
    except (ValueError, IndexError):
        return None


def modele(racine: str) -> str | None:
    """« Raspberry Pi 4 Model B Rev 1.4 » ; le fichier se termine par un octet nul."""
    brut = lire_texte(racine, "/proc/device-tree/model")
    return brut.strip("\x00\n ") or None if brut else None


def etat_alimentation(racine: str) -> int | None:
    """Les bits get_throttled : le fichier du noyau récent, sinon ``vcgencmd``. None hors Raspberry Pi."""
    brut = lire_texte(racine, GET_THROTTLED)
    if brut is None and racine in ("", "/"):
        try:
            sortie = subprocess.run(["vcgencmd", "get_throttled"], capture_output=True, text=True, timeout=5, check=False)
            brut = sortie.stdout.partition("=")[2] if sortie.returncode == 0 else None
        except (OSError, subprocess.SubprocessError):
            brut = None
    try:
        return int(brut.strip(), 16) if brut and brut.strip() else None
    except ValueError:
        return None


def disque(chemin: str) -> tuple[float, float] | None:
    """(% occupé, Go libres) du système de fichiers qui contient ``chemin``."""
    try:
        u = shutil.disk_usage(chemin)
    except OSError:
        return None
    return round(100 * u.used / u.total, 1), round(u.free / 1024 ** 3, 1)


def taille_base(chemin: str | None) -> float | None:
    """La base SQLite et ses fichiers ``-wal`` / ``-shm``, en Mo. None si la base n'est pas là."""
    if not chemin or not os.path.isfile(chemin):
        return None
    return round(sum(os.path.getsize(f) for f in (chemin, chemin + "-wal", chemin + "-shm") if os.path.isfile(f)) / MO, 1)


def memoire_service(racine: str, nom: str) -> float | None:
    """
    Mémoire ANONYME du service en Mo (ligne ``anon`` de ``memory.stat``) : le tas de tous ses processus — les plugins
    pour Krissvia, ``node`` pour Zigbee2MQTT. Pas ``memory.current``, qui compte aussi le cache disque : il monte
    pendant des jours sans être une fuite, et se rend dès que la mémoire manque.
    """
    brut = lire_texte(racine, f"{CGROUP_SERVICES}/{nom}.service/memory.stat")
    for ligne in (brut or "").splitlines():
        cle, _, valeur = ligne.partition(" ")
        if cle == "anon":
            try:
                return round(int(valeur) / MO, 1)
            except ValueError:
                return None
    return None


def systemctl_show(noms: list[str]) -> str | None:
    """La sortie de ``systemctl show`` pour ces services ; None sans systemd (ou pour les essais)."""
    try:
        sortie = subprocess.run(["systemctl", "show", "--property=Id,LoadState,ActiveState", "--", *(n + ".service" for n in noms)],
                                capture_output=True, text=True, timeout=10, check=False)
    except (OSError, subprocess.SubprocessError):
        return None
    return sortie.stdout if sortie.returncode == 0 else None


def etats_services(noms: list[str], show=None) -> dict[str, str]:
    """
    L'état de chaque service (``active``, ``inactive``, ``failed``, ``activating``…), tel que systemd le dit. Un
    service que systemd ne connaît pas (``LoadState=not-found``, faute de frappe dans les réglages, Zigbee2MQTT pas
    installé sur cette machine) est absent du résultat : le déclarer « arrêté » alerterait pour rien, à vie.
    """
    if not noms:
        return {}
    brut = (show or systemctl_show)(noms)
    etats = {}
    for bloc in (brut or "").strip().split("\n\n"):
        champs = dict(l.partition("=")[::2] for l in bloc.splitlines() if "=" in l)
        nom = champs.get("Id", "").removesuffix(".service")
        if nom in noms and champs.get("LoadState") == "loaded" and champs.get("ActiveState"):
            etats[nom] = champs["ActiveState"]
    return etats


@dataclass
class Seuil:
    """
    Un dépassement avec hystérésis : vrai à partir de ``seuil``, et ne redevient faux que sous ``seuil - marge``. Une
    mémoire qui oscille autour de 90 % ne doit pas envoyer une alerte et sa résolution toutes les trente secondes.
    """
    seuil: float
    marge: float
    actif: bool = False

    def evaluer(self, valeur: float | None) -> bool:
        if valeur is not None:
            self.actif = valeur >= self.seuil if not self.actif else valeur >= self.seuil - self.marge
        return self.actif


@dataclass
class Releve:
    """Un relevé complet. Un champ à None : la machine ne sait pas le dire, l'entité n'est pas déclarée."""
    modele: str | None
    temperature: float | None
    processeur: float | None
    charge: float | None
    memoire: float | None
    disque: float | None
    disque_libre_go: float | None
    duree: int | None
    alimentation: int | None
    charge_5: float | None = None
    charge_15: float | None = None
    swap: float | None = None
    base: float | None = None
    services: dict[str, str] = field(default_factory=dict)            # nom → état systemd
    memoire_services: dict[str, float | None] = field(default_factory=dict)   # nom → Mo

    def sante(self, nom: str, alertes: dict[str, bool] | None = None, seuils: dict[str, float] | None = None) -> dict:
        a = self.alimentation
        return {
            "hostname": nom, "model": self.modele, "cpu_temperature": self.temperature, "cpu_percent": self.processeur,
            "load_1": self.charge, "load_5": self.charge_5, "load_15": self.charge_15,
            "memory_percent": self.memoire, "swap_percent": self.swap, "disk_percent": self.disque,
            "disk_free_gb": self.disque_libre_go, "database_mb": self.base, "uptime_s": self.duree,
            "undervoltage_now": bool(a & SOUS_TENSION) if a is not None else None,
            "throttled_now": bool(a & (BRIDE | FREQUENCE_LIMITEE)) if a is not None else None,
            "undervoltage_since_boot": bool(a & (SOUS_TENSION << DEPUIS_DEMARRAGE)) if a is not None else None,
            "throttled_since_boot": bool(a & ((BRIDE | FREQUENCE_LIMITEE) << DEPUIS_DEMARRAGE)) if a is not None else None,
            "services": [{"name": n, "state": e, "memory_mb": self.memoire_services.get(n)} for n, e in self.services.items()],
            "alerts": alertes or {},
            "thresholds": seuils or {},
        }


def relever(racine: str, chemin_disque: str, precedent: tuple[int, int] | None, *, services: list[str] = (),
            chemin_base: str | None = None, show=None) -> tuple[Releve, tuple[int, int] | None]:
    compteurs = compteurs_processeur(racine)
    occupe, libre = disque(chemin_disque) or (None, None)
    un, cinq, quinze = charges(racine) or (None, None, None)
    meminfo = champs_meminfo(racine)
    etats = etats_services(list(services), show)
    return Releve(modele(racine), temperature_processeur(racine), occupation(precedent, compteurs), un,
                  memoire(meminfo), occupe, libre, duree_fonctionnement(racine), etat_alimentation(racine),
                  charge_5=cinq, charge_15=quinze, swap=swap(meminfo), base=taille_base(chemin_base), services=etats,
                  memoire_services={n: memoire_service(racine, n) for n in etats}), compteurs


# Entités numériques : (identifiant, nom, classe, unité, champ du relevé).
NUMERIQUES = [
    ("temperature", "Température processeur", "device_temperature", "°C", "temperature"),
    ("processeur", "Processeur", None, "%", "processeur"),
    ("charge", "Charge", None, None, "charge"),
    ("memoire", "Mémoire", None, "%", "memoire"),
    ("swap", "Swap", None, "%", "swap"),
    ("disque", "Disque", None, "%", "disque"),
    ("base", "Taille de la base", None, "Mo", "base"),
]
PROBLEMES = [("sous_tension", "Sous-tension", SOUS_TENSION), ("bridage", "Bridage", BRIDE | FREQUENCE_LIMITEE)]

# Dépassements : (identifiant, nom, champ du relevé, marge de retour).
DEPASSEMENTS = [
    ("surchauffe", "Surchauffe", "temperature", 5),
    ("memoire_pleine", "Mémoire pleine", "memoire", 5),
    ("disque_plein", "Disque plein", "disque", 2),   # un disque ne se vide pas tout seul : une petite marge suffit
]


class Raspberry(krissvia.Plugin):
    def __init__(self, nom: str, racine: str, chemin_disque: str, intervalle: int, *, services: list[str] = (),
                 chemin_base: str | None = None, seuils: dict[str, float] | None = None, show=None):
        self.nom, self.racine, self.chemin_disque, self.intervalle = nom, racine, chemin_disque, intervalle
        self.services, self.chemin_base, self._show = list(services), chemin_base, show
        seuils = {"temperature": 75, "memoire": 90, "disque": 85, **(seuils or {})}
        self.seuils = {uid: Seuil(seuils[champ], marge) for uid, _, champ, marge in DEPASSEMENTS}
        self.releve, self._compteurs = self._relever(None)
        self._evaluer()

    def _relever(self, precedent):
        return relever(self.racine, self.chemin_disque, precedent, services=self.services, chemin_base=self.chemin_base,
                       show=self._show)

    def _evaluer(self) -> dict[str, bool]:
        return {uid: self.seuils[uid].evaluer(getattr(self.releve, champ)) for uid, _, champ, _ in DEPASSEMENTS}

    def _alertes(self) -> dict[str, bool]:
        return {uid: self.seuils[uid].actif for uid, _, champ, _ in DEPASSEMENTS if getattr(self.releve, champ) is not None}

    def entites(self):
        r = self.releve
        yield krissvia.entity("sante", "sensor", "Santé", device_class="system_health")
        for uid, nom, classe, unite, champ in NUMERIQUES:
            # L'occupation du processeur se calcule entre deux relevés : déclarée dès que /proc/stat se lit.
            if getattr(r, champ) is not None or (champ == "processeur" and self._compteurs is not None):
                yield krissvia.entity(uid, "sensor", nom, device_class=classe, unit=unite, state_class="measurement")
        if r.alimentation is not None:
            for uid, nom, _ in PROBLEMES:
                yield krissvia.entity(uid, "binary_sensor", nom, device_class="problem")
        for uid, nom, champ, _ in DEPASSEMENTS:
            if getattr(r, champ) is not None:
                yield krissvia.entity(uid, "binary_sensor", nom, device_class="problem")
        # Un service connu de systemd l'est pour de bon : sa mémoire est déclarée même s'il est arrêté au démarrage du
        # plugin (elle reviendra avec lui), sinon elle n'apparaîtrait qu'au prochain redémarrage du plugin.
        for service in r.services:
            yield krissvia.entity(f"service_{service}", "binary_sensor", f"Service {service} arrêté", device_class="problem")
            yield krissvia.entity(f"memoire_{service}", "sensor", f"Mémoire {service}", unit="Mo", state_class="measurement")

    def valeurs(self):
        r = self.releve
        alertes = self._alertes()
        seuils = {champ: self.seuils[uid].seuil for uid, _, champ, _ in DEPASSEMENTS}
        valeurs = [krissvia.value("sante", r.sante(self.nom, alertes, seuils))]
        valeurs += [krissvia.value(uid, getattr(r, champ)) for uid, _, _, _, champ in NUMERIQUES if getattr(r, champ) is not None]
        if r.alimentation is not None:
            valeurs += [krissvia.value(uid, bool(r.alimentation & masque)) for uid, _, masque in PROBLEMES]
        valeurs += [krissvia.value(uid, actif) for uid, actif in alertes.items()]
        valeurs += [krissvia.value(f"service_{n}", etat != "active") for n, etat in r.services.items()]
        # Un service arrêté n'a plus de mémoire : on se tait au lieu d'écrire 0, qui ferait une fausse chute dans la courbe.
        valeurs += [krissvia.value(f"memoire_{n}", mo) for n, mo in r.memoire_services.items() if mo is not None]
        return valeurs

    def snapshot(self):
        return [krissvia.declare(krissvia.device("machine", self.nom, list(self.entites()), model=self.releve.modele)),
                krissvia.state(*self.valeurs())]

    async def surveiller(self, client: krissvia.CoreClient):
        log.info("%s (%s) : relevé toutes les %d s ; services : %s", self.nom, self.releve.modele or "machine Linux",
                 self.intervalle, ", ".join(self.releve.services) or "aucun")
        signale, arretes = None, set()
        while True:
            await asyncio.sleep(self.intervalle)
            self.releve, self._compteurs = self._relever(self._compteurs)
            self._evaluer()
            await client.send(krissvia.state(*self.valeurs()))
            probleme = self.releve.alimentation is not None and bool(self.releve.alimentation & (SOUS_TENSION | BRIDE))
            if probleme != signale:
                (log.warning if probleme else log.info)("Alimentation : %s (0x%x)", "PROBLÈME" if probleme else "correcte",
                                                        self.releve.alimentation or 0)
                signale = probleme
            maintenant = {n for n, e in self.releve.services.items() if e != "active"}
            for n in maintenant - arretes:
                log.warning("Service %s : %s", n, self.releve.services[n])
            for n in arretes - maintenant:
                log.info("Service %s : de nouveau actif", n)
            arretes = maintenant


def liste(texte: str | None) -> list[str]:
    """« krissvia, mosquitto,zigbee2mqtt » → trois noms ; « .service » final toléré."""
    return [n.strip().removesuffix(".service") for n in (texte or "").split(",") if n.strip()]


if __name__ == "__main__":
    nom = krissvia.setting("Raspberry:Nom") or socket.gethostname()
    intervalle = max(5, krissvia.setting("Raspberry:IntervalSeconds", 30, int))
    # Racine des fichiers système : « / » ; un autre dossier sert aux essais hors d'un Raspberry.
    racine = krissvia.setting("Raspberry:Racine", "/")
    chemin_disque = krissvia.setting("Raspberry:Disque", "/" if os.name != "nt" else os.getcwd())
    services = liste(krissvia.setting("Raspberry:Services", "krissvia,mosquitto,zigbee2mqtt"))
    # La base : réglée, sinon celle du cœur (Database__Path, hérité du service), sinon l'emplacement du kit.
    chemin_base = (krissvia.setting("Raspberry:Base") or os.environ.get("Database__Path")
                   or ("/var/lib/krissvia/krissvia.db" if os.name != "nt" else None))
    seuils = {"temperature": krissvia.setting("Raspberry:SeuilTemperature", 75, float),
              "memoire": krissvia.setting("Raspberry:SeuilMemoire", 90, float),
              "disque": krissvia.setting("Raspberry:SeuilDisque", 85, float)}
    krissvia.run(Raspberry(nom, racine, chemin_disque, intervalle, services=services, chemin_base=chemin_base, seuils=seuils),
                 Raspberry.surveiller, plugin_version=VERSION)
