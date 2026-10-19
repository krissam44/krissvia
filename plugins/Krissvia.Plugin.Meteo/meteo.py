"""Plugin Météo : conditions et prévisions de plusieurs lieux, relevées sur Open-Meteo.

Exemple de plugin Krissvia écrit en Python (docs/PLUGINS-PYTHON.md). Un appareil par lieu, avec :

- une entité « météo » (``device_class: weather``) dont la valeur est un objet : conditions du moment,
  lever et coucher du soleil, prévision sur 7 jours. C'est ce que dessine le widget Météo ;
- des entités numériques (température, humidité, pression, vent), celles dont le cœur se sert : la
  température d'un lieu peut servir de température extérieure à un thermostat.

Quand Open-Meteo ne répond pas pour un lieu, son appareil devient indisponible : le cœur ne garde pas
la dernière valeur comme si elle était fraîche.

Open-Meteo est gratuit et sans clé pour un usage non commercial ; les données sont sous licence
CC BY 4.0 (https://open-meteo.com).
"""

import asyncio
import json
import logging
import re
import unicodedata
import urllib.parse
import urllib.request
from dataclasses import dataclass, field

import krissvia

VERSION = "0.2.0"
API = "https://api.open-meteo.com/v1/forecast"

log = logging.getLogger("meteo")

# Codes temps WMO (weather_code) : les mêmes libellés que DomoMud.
CONDITIONS = {
    0: "Ciel dégagé", 1: "Plutôt dégagé", 2: "Partiellement nuageux", 3: "Couvert",
    45: "Brouillard", 48: "Brouillard",
    51: "Bruine", 53: "Bruine", 55: "Bruine", 56: "Bruine verglaçante", 57: "Bruine verglaçante",
    61: "Pluie", 63: "Pluie", 65: "Pluie", 66: "Pluie verglaçante", 67: "Pluie verglaçante",
    71: "Neige", 73: "Neige", 75: "Neige", 77: "Grains de neige",
    80: "Averses", 81: "Averses", 82: "Averses", 85: "Averses de neige", 86: "Averses de neige",
    95: "Orage", 96: "Orage avec grêle", 99: "Orage avec grêle",
}

# Grandeurs publiées en entités numériques : (suffixe, champ « current » d'Open-Meteo, nom, classe, unité).
GRANDEURS = [
    ("temperature", "temperature_2m", "Température", "temperature", "°C"),
    ("humidite", "relative_humidity_2m", "Humidité", "humidity", "%"),
    ("pression", "surface_pressure", "Pression", "pressure", "hPa"),
    ("vent", "wind_speed_10m", "Vent", "wind_speed", "km/h"),
]

COURANT = ["temperature_2m", "relative_humidity_2m", "apparent_temperature", "is_day", "weather_code",
           "wind_speed_10m", "precipitation", "surface_pressure"]
QUOTIDIEN = ["weather_code", "temperature_2m_max", "temperature_2m_min", "precipitation_probability_max",
             "sunrise", "sunset"]


def identifiant(nom: str) -> str:
    """« La Trinité-sur-Mer » → « la_trinite_sur_mer » : l'identifiant d'un lieu qui n'en donne pas."""
    sans_accents = unicodedata.normalize("NFKD", nom).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "_", sans_accents.lower()).strip("_") or "lieu"


def condition(code) -> str:
    return CONDITIONS.get(int(code), "—") if code is not None else "—"


@dataclass
class Lieu:
    nom: str
    latitude: float
    longitude: float
    appareil: str
    valeurs: dict = field(default_factory=dict)  # unique_id → dernière valeur
    joignable: bool = False

    def uid(self, suffixe: str) -> str:
        return f"{self.appareil}:{suffixe}"

    def entites(self):
        yield krissvia.entity(self.uid("meteo"), "sensor", "Météo", device_class="weather")
        for suffixe, _, nom, classe, unite in GRANDEURS:
            yield krissvia.entity(self.uid(suffixe), "sensor", nom, device_class=classe, unit=unite,
                                  state_class="measurement")


def lire_lieux() -> list[Lieu]:
    """Les lieux de la page Services. Sans liste, l'ancien réglage d'un seul lieu (appareil « meteo »)."""
    lieux = []
    for i, brut in enumerate(krissvia.settings_list("Lieux")):
        try:
            nom = brut.get("nom") or f"Lieu {i + 1}"
            lieux.append(Lieu(nom=nom, latitude=float(brut["latitude"]), longitude=float(brut["longitude"]),
                              appareil=brut.get("identifiant") or identifiant(nom)))
        except (KeyError, ValueError):
            raise ValueError(f"lieu n° {i + 1} : latitude et longitude à renseigner, en degrés décimaux")

    if not lieux and krissvia.setting("Meteo:Latitude") is not None:
        lieux.append(Lieu(nom=krissvia.setting("Meteo:Name", "Météo"),
                          latitude=krissvia.setting("Meteo:Latitude", convert=float),
                          longitude=krissvia.setting("Meteo:Longitude", convert=float),
                          appareil=krissvia.setting("Meteo:DeviceId", "meteo")))

    doublons = {l.appareil for l in lieux if [m.appareil for m in lieux].count(l.appareil) > 1}
    if doublons:
        raise ValueError(f"deux lieux portent le même identifiant : {', '.join(sorted(doublons))}")
    return lieux


def mesures(lieu: Lieu, donnees: dict) -> dict:
    """Une réponse d'Open-Meteo → les valeurs des entités du lieu (fonction pure, testée)."""
    actuel, jours = donnees["current"], donnees.get("daily", {})
    valeurs = {lieu.uid(s): float(actuel[champ]) for s, champ, *_ in GRANDEURS if actuel.get(champ) is not None}

    def jour(i, cle):
        serie = jours.get(cle) or []
        return serie[i] if i < len(serie) else None

    prevision = [{
        "date": date,
        "code": jour(i, "weather_code"),
        "condition": condition(jour(i, "weather_code")),
        "max": jour(i, "temperature_2m_max"),
        "min": jour(i, "temperature_2m_min"),
        "precipitation_probability": jour(i, "precipitation_probability_max"),
    } for i, date in enumerate(jours.get("time") or [])]

    valeurs[lieu.uid("meteo")] = {
        "name": lieu.nom,
        "temperature": actuel.get("temperature_2m"),
        "apparent_temperature": actuel.get("apparent_temperature"),
        "humidity": actuel.get("relative_humidity_2m"),
        "wind_speed": actuel.get("wind_speed_10m"),
        "precipitation": actuel.get("precipitation"),
        "pressure": actuel.get("surface_pressure"),
        "code": actuel.get("weather_code"),
        "condition": condition(actuel.get("weather_code")),
        "is_day": bool(actuel.get("is_day", 1)),
        # Heures locales du lieu (timezone=auto), telles qu'Open-Meteo les donne : « 2026-10-19T08:36 ».
        "sunrise": jour(0, "sunrise"),
        "sunset": jour(0, "sunset"),
        "forecast": prevision,
    }
    return valeurs


class Meteo(krissvia.Plugin):
    def __init__(self, lieux: list[Lieu], intervalle_minutes: int):
        self.lieux = lieux
        self.intervalle = intervalle_minutes * 60

    def snapshot(self):
        messages = [krissvia.declare(*(krissvia.device(l.appareil, l.nom, list(l.entites()), manufacturer="Open-Meteo")
                                       for l in self.lieux))]
        # Après une reconnexion, le cœur reçoit tout ce qu'on sait : il a pu redémarrer entre-temps.
        connues = [krissvia.value(u, v) for l in self.lieux for u, v in l.valeurs.items()]
        if connues:
            messages.append(krissvia.state(*connues))
        messages += [krissvia.availability(l.joignable, device_unique_id=l.appareil) for l in self.lieux]
        return messages

    @staticmethod
    def lire(lieu: Lieu) -> dict:
        """Un relevé (appel bloquant : il tourne dans un fil à part)."""
        parametres = urllib.parse.urlencode({
            "latitude": lieu.latitude, "longitude": lieu.longitude, "timezone": "auto", "forecast_days": 7,
            "current": ",".join(COURANT), "daily": ",".join(QUOTIDIEN),
        })
        requete = urllib.request.Request(f"{API}?{parametres}", headers={"User-Agent": f"Krissvia-meteo/{VERSION}"})
        with urllib.request.urlopen(requete, timeout=15) as reponse:
            return json.load(reponse)

    async def relever(self, client: krissvia.CoreClient):
        """Tous les lieux à chaque intervalle ; un lieu en échec n'empêche pas les autres."""
        log.info("%d lieu(x) : %s", len(self.lieux), ", ".join(l.nom for l in self.lieux))
        while True:
            for lieu in self.lieux:
                try:
                    lieu.valeurs = mesures(lieu, await asyncio.to_thread(self.lire, lieu))
                    await client.send(krissvia.state(*(krissvia.value(u, v) for u, v in lieu.valeurs.items())))
                    if not lieu.joignable:
                        lieu.joignable = True
                        await client.send(krissvia.availability(True, device_unique_id=lieu.appareil))
                    meteo = lieu.valeurs[lieu.uid("meteo")]
                    log.info("%s : %s °C, %s", lieu.nom, meteo["temperature"], meteo["condition"])
                except (OSError, ValueError, KeyError, TypeError) as ex:
                    log.warning("%s : Open-Meteo injoignable ou réponse inattendue (%s)", lieu.nom, ex)
                    if lieu.joignable:
                        lieu.joignable = False
                        await client.send(krissvia.availability(False, device_unique_id=lieu.appareil))
            await asyncio.sleep(self.intervalle)


def main():
    try:
        lieux = lire_lieux()
        intervalle = krissvia.setting("Meteo:IntervalMinutes", 10, int)
    except ValueError as ex:
        raise SystemExit(f"Réglages invalides : {ex}")
    if not lieux:
        raise SystemExit("Aucun lieu : en ajouter un dans les réglages du plugin (page Services).")

    krissvia.run(Meteo(lieux, intervalle), Meteo.relever, plugin_version=VERSION)


if __name__ == "__main__":
    main()
