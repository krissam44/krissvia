"""Tests du plugin Raspberry, sur une fausse arborescence /proc et /sys (aucun Raspberry nécessaire).

    python -m unittest discover -s plugins/Krissvia.Plugin.Raspberry
"""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))

import raspberry  # noqa: E402

FICHIERS = {
    "sys/class/thermal/thermal_zone0/temp": "52582\n",
    "proc/stat": "cpu  1000 0 500 8000 500 0 0 0 0 0\ncpu0 1 2 3 4\n",
    "proc/loadavg": "0.42 0.30 0.25 1/234 5678\n",
    "proc/meminfo": "MemTotal:        3884000 kB\nMemFree:          200000 kB\nMemAvailable:    2913000 kB\n",
    "proc/uptime": "93784.12 350000.00\n",
    "proc/device-tree/model": "Raspberry Pi 4 Model B Rev 1.4\x00",
    "sys/firmware/get_throttled": "50005\n",
}

# « soc:firmware » n'est pas un nom de dossier possible sous Windows : les essais lisent ailleurs.
raspberry.GET_THROTTLED = "/sys/firmware/get_throttled"


class FausseMachine:
    def __init__(self, fichiers=None):
        self.dossier = tempfile.TemporaryDirectory()
        self.racine = self.dossier.name
        for chemin, contenu in (FICHIERS if fichiers is None else fichiers).items():
            self.ecrire(chemin, contenu)

    def ecrire(self, chemin, contenu):
        complet = os.path.join(self.racine, chemin)
        os.makedirs(os.path.dirname(complet), exist_ok=True)
        with open(complet, "w", encoding="utf-8", newline="") as f:
            f.write(contenu)


class LectureTests(unittest.TestCase):
    def test_un_raspberry_dit_tout_ce_qu_il_sait(self):
        m = FausseMachine()
        r, compteurs = raspberry.relever(m.racine, m.racine, None)

        self.assertEqual("Raspberry Pi 4 Model B Rev 1.4", r.modele)
        self.assertEqual(52.6, r.temperature)
        self.assertEqual(0.42, r.charge)
        self.assertEqual(25.0, r.memoire)            # (3 884 000 − 2 913 000) / 3 884 000
        self.assertEqual(93784, r.duree)
        self.assertIsNone(r.processeur)              # il faut deux relevés
        self.assertEqual((10000, 8500), compteurs)

    def test_l_occupation_du_processeur_se_mesure_entre_deux_releves(self):
        m = FausseMachine()
        _, avant = raspberry.relever(m.racine, m.racine, None)
        m.ecrire("proc/stat", "cpu  1600 0 700 8600 600 0 0 0 0 0\n")   # +1500 dont +700 inactifs
        r, _ = raspberry.relever(m.racine, m.racine, avant)

        self.assertEqual(53.3, r.processeur)

    def test_sous_tension_et_bridage_en_ce_moment_et_depuis_le_demarrage(self):
        # 0x50005 : sous-tension et bridage maintenant, et depuis le démarrage.
        machine = FausseMachine()   # gardée : son dossier temporaire disparaît avec elle
        sante = raspberry.relever(machine.racine, ".", None)[0].sante("pi")
        self.assertTrue(sante["undervoltage_now"])
        self.assertTrue(sante["throttled_now"])
        self.assertTrue(sante["undervoltage_since_boot"])

        # 0x50000 : plus rien maintenant, mais c'est arrivé depuis le démarrage.
        m = FausseMachine()
        m.ecrire("sys/firmware/get_throttled", "50000\n")
        sante = raspberry.relever(m.racine, ".", None)[0].sante("pi")
        self.assertFalse(sante["undervoltage_now"])
        self.assertFalse(sante["throttled_now"])
        self.assertTrue(sante["undervoltage_since_boot"])
        self.assertTrue(sante["throttled_since_boot"])


class EntitesTests(unittest.TestCase):
    def _entites(self, machine):
        return {e["unique_id"]: e for e in raspberry.Raspberry("pi", machine.racine, machine.racine, 30).entites()}

    def test_la_temperature_du_processeur_n_est_pas_une_sonde_et_les_problemes_sont_signales(self):
        e = self._entites(FausseMachine())

        self.assertEqual("device_temperature", e["temperature"]["device_class"])   # jamais « temperature » : pas une pièce
        self.assertEqual("system_health", e["sante"]["device_class"])
        self.assertEqual("problem", e["sous_tension"]["device_class"])
        self.assertEqual("binary_sensor", e["bridage"]["kind"])
        self.assertIn("processeur", e)

    def test_une_machine_qui_ne_sait_rien_ne_declare_que_ce_qu_elle_sait(self):
        e = self._entites(FausseMachine({}))   # ni /proc ni /sys : un poste Windows, par exemple

        self.assertEqual({"sante", "disque"}, set(e))

    def test_les_valeurs_publiees_disent_le_probleme_en_cours(self):
        machine = FausseMachine()
        plugin = raspberry.Raspberry("pi", machine.racine, ".", 30)
        valeurs = {v["entity_unique_id"]: v["value"] for v in plugin.valeurs()}

        self.assertIs(True, valeurs["sous_tension"])
        self.assertIs(True, valeurs["bridage"])
        self.assertEqual(52.6, valeurs["temperature"])
        self.assertEqual("pi", valeurs["sante"]["hostname"])


if __name__ == "__main__":
    unittest.main()
