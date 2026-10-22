"""Plugin Raspberry : la santé de la machine qui fait tourner Krissvia.

Lu dans ``/proc`` et ``/sys``, sans dépendance : température du processeur, occupation du processeur, charge,
mémoire, disque, durée de fonctionnement, et — sur un Raspberry Pi — la sous-tension et le bridage signalés par le
microprogramme. Fonctionne sur tout Linux ; ce qu'une machine ne sait pas dire n'est simplement pas déclaré.

Un appareil (le nom de la machine), avec :

- une entité « santé » (``device_class: system_health``) dont la valeur est un objet : c'est ce que dessine la carte
  livrée par le plugin (``widgets/raspberry``) ;
- des entités numériques (température du processeur en ``device_temperature`` — surtout pas ``temperature``, ce
  n'est pas une pièce —, processeur, mémoire et disque en %) ;
- deux ``binary_sensor`` de classe ``problem`` sur un Raspberry : sous-tension et bridage EN CE MOMENT. La
  supervision du cœur alerte dès que l'un passe à vrai : une alimentation trop faible est la cause classique d'une
  carte SD ou d'une base corrompue.
"""

from __future__ import annotations

import asyncio
import logging
import os
import shutil
import socket
import subprocess
from dataclasses import dataclass

import krissvia

VERSION = "1.0.0"
log = logging.getLogger("raspberry")

# Bits de get_throttled (microprogramme du Raspberry Pi) : « en ce moment » et « depuis le démarrage ».
SOUS_TENSION, FREQUENCE_LIMITEE, BRIDE, LIMITE_TEMPERATURE = 0x1, 0x2, 0x4, 0x8
DEPUIS_DEMARRAGE = 16

# Où le noyau d'un Raspberry Pi expose get_throttled (noyaux récents) ; sinon, ``vcgencmd``.
GET_THROTTLED = "/sys/devices/platform/soc/soc:firmware/get_throttled"


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


def charge(racine: str) -> float | None:
    brut = lire_texte(racine, "/proc/loadavg")
    try:
        return float(brut.split()[0]) if brut else None
    except (ValueError, IndexError):
        return None


def memoire(racine: str) -> float | None:
    """Mémoire occupée en %, d'après MemAvailable (ce que le noyau peut rendre sans échanger)."""
    brut = lire_texte(racine, "/proc/meminfo")
    if not brut:
        return None
    champs = {}
    for ligne in brut.splitlines():
        nom, _, reste = ligne.partition(":")
        if reste.strip():
            try:
                champs[nom] = int(reste.split()[0])
            except ValueError:
                pass
    total, libre = champs.get("MemTotal"), champs.get("MemAvailable")
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

    def sante(self, nom: str) -> dict:
        a = self.alimentation
        return {
            "hostname": nom, "model": self.modele, "cpu_temperature": self.temperature, "cpu_percent": self.processeur,
            "load_1": self.charge, "memory_percent": self.memoire, "disk_percent": self.disque,
            "disk_free_gb": self.disque_libre_go, "uptime_s": self.duree,
            "undervoltage_now": bool(a & SOUS_TENSION) if a is not None else None,
            "throttled_now": bool(a & (BRIDE | FREQUENCE_LIMITEE)) if a is not None else None,
            "undervoltage_since_boot": bool(a & (SOUS_TENSION << DEPUIS_DEMARRAGE)) if a is not None else None,
            "throttled_since_boot": bool(a & ((BRIDE | FREQUENCE_LIMITEE) << DEPUIS_DEMARRAGE)) if a is not None else None,
        }


def relever(racine: str, chemin_disque: str, precedent: tuple[int, int] | None) -> tuple[Releve, tuple[int, int] | None]:
    compteurs = compteurs_processeur(racine)
    occupe, libre = disque(chemin_disque) or (None, None)
    return Releve(modele(racine), temperature_processeur(racine), occupation(precedent, compteurs), charge(racine),
                  memoire(racine), occupe, libre, duree_fonctionnement(racine), etat_alimentation(racine)), compteurs


# Entités numériques : (identifiant, nom, classe, unité, champ du relevé).
NUMERIQUES = [
    ("temperature", "Température processeur", "device_temperature", "°C", "temperature"),
    ("processeur", "Processeur", None, "%", "processeur"),
    ("memoire", "Mémoire", None, "%", "memoire"),
    ("disque", "Disque", None, "%", "disque"),
]
PROBLEMES = [("sous_tension", "Sous-tension", SOUS_TENSION), ("bridage", "Bridage", BRIDE | FREQUENCE_LIMITEE)]


class Raspberry(krissvia.Plugin):
    def __init__(self, nom: str, racine: str, chemin_disque: str, intervalle: int):
        self.nom, self.racine, self.chemin_disque, self.intervalle = nom, racine, chemin_disque, intervalle
        self.releve, self._compteurs = relever(racine, chemin_disque, None)

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

    def valeurs(self):
        r = self.releve
        valeurs = [krissvia.value("sante", r.sante(self.nom))]
        valeurs += [krissvia.value(uid, getattr(r, champ)) for uid, _, _, _, champ in NUMERIQUES if getattr(r, champ) is not None]
        if r.alimentation is not None:
            valeurs += [krissvia.value(uid, bool(r.alimentation & masque)) for uid, _, masque in PROBLEMES]
        return valeurs

    def snapshot(self):
        return [krissvia.declare(krissvia.device("machine", self.nom, list(self.entites()), model=self.releve.modele)),
                krissvia.state(*self.valeurs())]

    async def surveiller(self, client: krissvia.CoreClient):
        log.info("%s (%s) : relevé toutes les %d s", self.nom, self.releve.modele or "machine Linux", self.intervalle)
        signale = None
        while True:
            await asyncio.sleep(self.intervalle)
            self.releve, self._compteurs = relever(self.racine, self.chemin_disque, self._compteurs)
            await client.send(krissvia.state(*self.valeurs()))
            probleme = self.releve.alimentation is not None and bool(self.releve.alimentation & (SOUS_TENSION | BRIDE))
            if probleme != signale:
                (log.warning if probleme else log.info)("Alimentation : %s (0x%x)", "PROBLÈME" if probleme else "correcte",
                                                        self.releve.alimentation or 0)
                signale = probleme


if __name__ == "__main__":
    nom = krissvia.setting("Raspberry:Nom") or socket.gethostname()
    intervalle = max(5, krissvia.setting("Raspberry:IntervalSeconds", 30, int))
    # Racine des fichiers système : « / » ; un autre dossier sert aux essais hors d'un Raspberry.
    racine = krissvia.setting("Raspberry:Racine", "/")
    chemin_disque = krissvia.setting("Raspberry:Disque", "/" if os.name != "nt" else os.getcwd())
    krissvia.run(Raspberry(nom, racine, chemin_disque, intervalle), Raspberry.surveiller, plugin_version=VERSION)
