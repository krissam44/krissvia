"""Plugin Météo : la température, l'humidité et la pression d'un lieu, relevées sur Open-Meteo.

Exemple de plugin Krissvia écrit en Python (docs/PLUGINS-PYTHON.md). Il ne fait que trois choses :
décrire son appareil (snapshot), relever et publier les mesures (relever), et dire quand le service
est injoignable — le cœur rend alors les entités indisponibles, au lieu de garder la dernière valeur.

Open-Meteo est gratuit et sans clé pour un usage non commercial ; les données sont sous licence
CC BY 4.0 (https://open-meteo.com).
"""

import asyncio
import json
import logging
import urllib.parse
import urllib.request

import krissvia

VERSION = "0.1.0"
API = "https://api.open-meteo.com/v1/forecast"

log = logging.getLogger("meteo")


class Meteo(krissvia.Plugin):
    def __init__(self, latitude: float, longitude: float, nom: str, appareil: str, intervalle_minutes: int):
        self.latitude, self.longitude = latitude, longitude
        self.nom, self.appareil = nom, appareil
        self.intervalle = intervalle_minutes * 60
        self.mesures: dict[str, float] = {}   # unique_id → dernière valeur relevée
        self.joignable = False

    # Ce que le plugin publie : (suffixe, champ d'Open-Meteo, nom, classe, unité).
    GRANDEURS = [
        ("temperature", "temperature_2m", "Température", "temperature", "°C"),
        ("humidite", "relative_humidity_2m", "Humidité", "humidity", "%"),
        ("pression", "surface_pressure", "Pression", "pressure", "hPa"),
    ]

    def uid(self, suffixe: str) -> str:
        return f"{self.appareil}:{suffixe}"

    def snapshot(self):
        messages = [krissvia.declare(krissvia.device(self.appareil, self.nom, [
            krissvia.entity(self.uid(s), "sensor", nom, device_class=classe, unit=unite, state_class="measurement")
            for s, _, nom, classe, unite in self.GRANDEURS
        ], manufacturer="Open-Meteo"))]
        # Après une reconnexion, le cœur reçoit tout ce qu'on sait : il a pu redémarrer entre-temps.
        if self.mesures:
            messages.append(krissvia.state(*(krissvia.value(u, v) for u, v in self.mesures.items())))
        messages.append(krissvia.availability(self.joignable, device_unique_id=self.appareil))
        return messages

    def lire(self) -> dict[str, float]:
        """Un relevé (appel bloquant : il tourne dans un fil à part)."""
        parametres = urllib.parse.urlencode({
            "latitude": self.latitude, "longitude": self.longitude,
            "current": ",".join(champ for _, champ, *_ in self.GRANDEURS),
        })
        requete = urllib.request.Request(f"{API}?{parametres}", headers={"User-Agent": f"Krissvia-meteo/{VERSION}"})
        with urllib.request.urlopen(requete, timeout=15) as reponse:
            actuel = json.load(reponse)["current"]
        return {self.uid(s): float(actuel[champ]) for s, champ, *_ in self.GRANDEURS if actuel.get(champ) is not None}

    async def relever(self, client: krissvia.CoreClient):
        """Un relevé par intervalle, publié aussitôt ; un échec rend l'appareil indisponible."""
        while True:
            try:
                self.mesures = await asyncio.to_thread(self.lire)
                await client.send(krissvia.state(*(krissvia.value(u, v) for u, v in self.mesures.items())))
                if not self.joignable:
                    self.joignable = True
                    await client.send(krissvia.availability(True, device_unique_id=self.appareil))
                log.info("Relevé : %s", ", ".join(f"{u.split(':')[-1]} {v}" for u, v in self.mesures.items()))
            except (OSError, ValueError, KeyError) as ex:
                log.warning("Open-Meteo injoignable : %s", ex)
                if self.joignable:
                    self.joignable = False
                    await client.send(krissvia.availability(False, device_unique_id=self.appareil))
            await asyncio.sleep(self.intervalle)


def main():
    try:
        meteo = Meteo(
            latitude=krissvia.setting("Meteo:Latitude", convert=float),
            longitude=krissvia.setting("Meteo:Longitude", convert=float),
            nom=krissvia.setting("Meteo:Name", "Météo"),
            appareil=krissvia.setting("Meteo:DeviceId", "meteo"),
            intervalle_minutes=krissvia.setting("Meteo:IntervalMinutes", 10, int),
        )
    except ValueError as ex:
        raise SystemExit(str(ex))
    if meteo.latitude is None or meteo.longitude is None:
        raise SystemExit("Latitude et longitude à renseigner dans les réglages du plugin (page Services).")

    krissvia.run(meteo, Meteo.relever, plugin_version=VERSION)


if __name__ == "__main__":
    main()
