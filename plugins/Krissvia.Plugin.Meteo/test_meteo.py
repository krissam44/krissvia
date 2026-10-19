"""Tests du plugin Météo, sans réseau : lecture des réglages et traduction d'une réponse d'Open-Meteo.

    python -m unittest discover -s plugins/Krissvia.Plugin.Meteo
(le SDK doit être importable : PYTHONPATH=sdk/python, ce que fait le test lancé depuis la racine du dépôt.)
"""

import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))

import meteo  # noqa: E402

REPONSE = {
    "utc_offset_seconds": 7200,
    "hourly": {"time": ["2026-10-19T14:00", "2026-10-19T15:00", "2026-10-19T16:00"],
               "temperature_2m": [12.0, None, 9.5]},
    "current": {"temperature_2m": 6.8, "relative_humidity_2m": 93, "apparent_temperature": 4.7, "is_day": 1,
                "weather_code": 0, "wind_speed_10m": 7.2, "precipitation": 0.0, "surface_pressure": 1019.8},
    "daily": {"time": ["2026-10-19", "2026-10-20"], "weather_code": [3, 63], "temperature_2m_max": [15.2, 15.0],
              "temperature_2m_min": [6.1, 8.0], "precipitation_probability_max": [3, 50],
              "sunrise": ["2026-10-19T08:36", "2026-10-20T08:37"], "sunset": ["2026-10-19T19:16", "2026-10-20T19:14"]},
}


class ReglagesTests(unittest.TestCase):
    def test_plusieurs_lieux_avec_identifiant_tire_du_nom(self):
        env = {"Lieux__0__Nom": "La Trinité-sur-Mer", "Lieux__0__Latitude": "47.58", "Lieux__0__Longitude": "-3.03",
               "Lieux__0__Identifiant": "meteo",
               "Lieux__1__Nom": "Vertou", "Lieux__1__Latitude": "47.17", "Lieux__1__Longitude": "-1.47"}
        with mock.patch.dict(os.environ, env, clear=True):
            lieux = meteo.lire_lieux()
        self.assertEqual([("La Trinité-sur-Mer", "meteo", -3.03), ("Vertou", "vertou", -1.47)],
                         [(l.nom, l.appareil, l.longitude) for l in lieux])

    def test_l_ancien_reglage_d_un_seul_lieu_garde_ses_entites(self):
        env = {"Meteo__Latitude": "47.58", "Meteo__Longitude": "-3.03", "Meteo__Name": "La Trinité"}
        with mock.patch.dict(os.environ, env, clear=True):
            (lieu,) = meteo.lire_lieux()
        self.assertEqual(("La Trinité", "meteo:temperature"), (lieu.nom, lieu.uid("temperature")))

    def test_un_lieu_sans_coordonnees_ou_deux_identifiants_egaux_sont_refuses(self):
        with mock.patch.dict(os.environ, {"Lieux__0__Nom": "Vertou"}, clear=True):
            with self.assertRaisesRegex(ValueError, "lieu n° 1"):
                meteo.lire_lieux()
        double = {"Lieux__0__Nom": "Paris", "Lieux__0__Latitude": "48.85", "Lieux__0__Longitude": "2.35",
                  "Lieux__1__Nom": "Paris", "Lieux__1__Latitude": "48.86", "Lieux__1__Longitude": "2.34"}
        with mock.patch.dict(os.environ, double, clear=True):
            with self.assertRaisesRegex(ValueError, "paris"):
                meteo.lire_lieux()

    def test_identifiant(self):
        self.assertEqual("la_trinite_sur_mer", meteo.identifiant("La Trinité-sur-Mer"))
        self.assertEqual("lieu", meteo.identifiant("—"))


class MesuresTests(unittest.TestCase):
    def setUp(self):
        self.lieu = meteo.Lieu("La Trinité-sur-Mer", 47.58, -3.03, "trinite")
        self.valeurs = meteo.mesures(self.lieu, REPONSE)

    def test_les_grandeurs_numeriques_sont_publiees_a_part(self):
        self.assertEqual(6.8, self.valeurs["trinite:temperature"])
        self.assertEqual(93.0, self.valeurs["trinite:humidite"])
        self.assertEqual(1019.8, self.valeurs["trinite:pression"])
        self.assertEqual(7.2, self.valeurs["trinite:vent"])

    def test_l_entite_meteo_porte_conditions_soleil_et_prevision(self):
        m = self.valeurs["trinite:meteo"]
        self.assertEqual(("Ciel dégagé", 4.7, True), (m["condition"], m["apparent_temperature"], m["is_day"]))
        self.assertEqual(("2026-10-19T08:36", "2026-10-19T19:16"), (m["sunrise"], m["sunset"]))
        self.assertEqual({"date": "2026-10-20", "code": 63, "condition": "Pluie", "max": 15.0, "min": 8.0,
                          "precipitation_probability": 50}, m["forecast"][1])

    def test_la_prevision_horaire_est_publiee_en_utc_sans_trous(self):
        # Heure d'été à Paris : 14:00 locale = 12:00 UTC ; le point sans valeur est omis.
        self.assertEqual([{"time": "2026-10-19T12:00:00Z", "temperature": 12.0},
                          {"time": "2026-10-19T14:00:00Z", "temperature": 9.5}],
                         self.valeurs["trinite:meteo"]["hourly"])

    def test_une_serie_quotidienne_absente_ne_casse_rien(self):
        reponse = {"current": REPONSE["current"], "daily": {"time": ["2026-10-19"]}}
        m = meteo.mesures(self.lieu, reponse)["trinite:meteo"]
        self.assertEqual([None, "—"], [m["forecast"][0]["max"], m["forecast"][0]["condition"]])


class InstantaneTests(unittest.TestCase):
    def test_declare_tous_les_lieux_puis_leurs_valeurs_et_leur_joignabilite(self):
        a, b = meteo.Lieu("A", 1, 1, "a"), meteo.Lieu("B", 2, 2, "b")
        a.valeurs, a.joignable = meteo.mesures(a, REPONSE), True
        messages = meteo.Meteo([a, b], 10).snapshot()

        self.assertEqual(["declare", "state", "availability", "availability"], [m["type"] for m in messages])
        self.assertEqual(["a", "b"], [d["unique_id"] for d in messages[0]["devices"]])
        self.assertIn("a:meteo", [e["unique_id"] for e in messages[0]["devices"][0]["entities"]])
        self.assertEqual([True, False], [m["online"] for m in messages[2:]])


if __name__ == "__main__":
    unittest.main()
