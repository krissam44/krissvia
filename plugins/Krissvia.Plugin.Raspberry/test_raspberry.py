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
    "proc/meminfo": "MemTotal:        3884000 kB\nMemFree:          200000 kB\nMemAvailable:    2913000 kB\n"
                    "SwapTotal:        102396 kB\nSwapFree:          76797 kB\n",
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

        self.assertEqual({"sante", "disque", "disque_plein"}, set(e))

    def test_les_valeurs_publiees_disent_le_probleme_en_cours(self):
        machine = FausseMachine()
        plugin = raspberry.Raspberry("pi", machine.racine, ".", 30)
        valeurs = {v["entity_unique_id"]: v["value"] for v in plugin.valeurs()}

        self.assertIs(True, valeurs["sous_tension"])
        self.assertIs(True, valeurs["bridage"])
        self.assertEqual(52.6, valeurs["temperature"])
        self.assertEqual("pi", valeurs["sante"]["hostname"])


# La sortie de « systemctl show » pour trois services : Zigbee2MQTT n'est pas installé sur cette machine.
SHOW = """Id=krissvia.service
LoadState=loaded
ActiveState=active

Id=mosquitto.service
LoadState=loaded
ActiveState=failed

Id=zigbee2mqtt.service
LoadState=not-found
ActiveState=inactive
"""


def cgroup(machine, service, anon_octets):
    machine.ecrire(f"sys/fs/cgroup/system.slice/{service}.service/memory.stat", f"anon {anon_octets}\nfile 999999999\nkernel 4096\n")


class ComplementsTests(unittest.TestCase):
    def test_swap_et_charges_sur_5_et_15_minutes(self):
        m = FausseMachine()
        r, _ = raspberry.relever(m.racine, m.racine, None)

        self.assertEqual(25.0, r.swap)                # (102 396 − 76 797) / 102 396
        self.assertEqual((0.42, 0.30, 0.25), (r.charge, r.charge_5, r.charge_15))

    def test_sans_swap_rien_n_est_declare(self):
        m = FausseMachine()
        m.ecrire("proc/meminfo", "MemTotal: 3884000 kB\nMemAvailable: 2913000 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")
        e = {x["unique_id"] for x in raspberry.Raspberry("pi", m.racine, m.racine, 30).entites()}

        self.assertNotIn("swap", e)

    def test_un_service_inconnu_de_systemd_est_ignore(self):
        etats = raspberry.etats_services(["krissvia", "mosquitto", "zigbee2mqtt"], lambda noms: SHOW)

        self.assertEqual({"krissvia": "active", "mosquitto": "failed"}, etats)

    def test_sans_systemd_aucun_service(self):
        self.assertEqual({}, raspberry.etats_services(["krissvia"], lambda noms: None))

    def test_la_memoire_d_un_service_est_son_tas_pas_son_cache(self):
        m = FausseMachine()
        cgroup(m, "krissvia", 157286400)              # 150 Mo anonymes ; le cache (« file ») ne compte pas

        self.assertEqual(150.0, raspberry.memoire_service(m.racine, "krissvia"))
        self.assertIsNone(raspberry.memoire_service(m.racine, "mosquitto"))

    def test_sans_controleur_memoire_on_additionne_le_tas_des_processus(self):
        # Raspberry Pi OS : « memory » absent de cgroup.controllers, donc pas de memory.stat — seulement la liste.
        m = FausseMachine()
        m.ecrire("sys/fs/cgroup/system.slice/krissvia.service/cgroup.procs", "812\n1033\n4242\n")   # 4242 : mort
        m.ecrire("proc/812/status", "Name:\tdotnet\nVmRSS:\t  204800 kB\nRssAnon:\t  143360 kB\nRssFile:\t   61440 kB\n")
        m.ecrire("proc/1033/status", "Name:\tpython3\nRssAnon:\t   10240 kB\n")
        m.ecrire("sys/fs/cgroup/system.slice/mosquitto.service/cgroup.procs", "")                # arrêté

        self.assertEqual(150.0, raspberry.memoire_service(m.racine, "krissvia"))   # 140 + 10 Mo, sans RssFile
        self.assertIsNone(raspberry.memoire_service(m.racine, "mosquitto"))

    def test_services_entites_et_valeurs(self):
        m = FausseMachine()
        cgroup(m, "krissvia", 157286400)              # mosquitto est en échec : plus de groupe, plus de mémoire
        plugin = raspberry.Raspberry("pi", m.racine, m.racine, 30, services=["krissvia", "mosquitto", "zigbee2mqtt"],
                                     show=lambda noms: SHOW)
        e = {x["unique_id"]: x for x in plugin.entites()}
        valeurs = {v["entity_unique_id"]: v["value"] for v in plugin.valeurs()}

        self.assertEqual("problem", e["service_mosquitto"]["device_class"])
        self.assertIn("memoire_mosquitto", e)         # déclarée quand même : elle reviendra avec le service
        self.assertNotIn("service_zigbee2mqtt", e)
        self.assertIs(False, valeurs["service_krissvia"])
        self.assertIs(True, valeurs["service_mosquitto"])
        self.assertEqual(150.0, valeurs["memoire_krissvia"])
        self.assertNotIn("memoire_mosquitto", valeurs)   # pas de 0 inventé : la courbe s'interrompt
        self.assertEqual([{"name": "krissvia", "state": "active", "memory_mb": 150.0},
                          {"name": "mosquitto", "state": "failed", "memory_mb": None}], valeurs["sante"]["services"])

    def test_la_taille_de_la_base_compte_le_journal(self):
        m = FausseMachine()
        m.ecrire("krissvia.db", "x" * (1024 * 1024))
        m.ecrire("krissvia.db-wal", "x" * (512 * 1024))

        self.assertEqual(1.5, raspberry.taille_base(os.path.join(m.racine, "krissvia.db")))
        self.assertIsNone(raspberry.taille_base(os.path.join(m.racine, "absente.db")))

    def test_un_seuil_ne_retombe_que_sous_sa_marge(self):
        s = raspberry.Seuil(90, 5)

        self.assertEqual([False, True, True, True, False, False, True],
                         [s.evaluer(v) for v in (89.9, 90, 86, 85.1, 84.9, None, 91)])

    def test_les_depassements_sont_publies(self):
        m = FausseMachine()                           # 52,6 °C, mémoire 25 %
        plugin = raspberry.Raspberry("pi", m.racine, m.racine, 30, seuils={"temperature": 50, "memoire": 90})
        e = {x["unique_id"]: x for x in plugin.entites()}
        valeurs = {v["entity_unique_id"]: v["value"] for v in plugin.valeurs()}

        self.assertEqual("problem", e["surchauffe"]["device_class"])
        self.assertIs(True, valeurs["surchauffe"])
        self.assertIs(False, valeurs["memoire_pleine"])
        self.assertEqual({"temperature": 50, "memoire": 90, "disque": 85}, valeurs["sante"]["thresholds"])

    def test_liste_des_services(self):
        self.assertEqual(["krissvia", "mosquitto", "zigbee2mqtt"], raspberry.liste(" krissvia, mosquitto.service,,zigbee2mqtt "))


if __name__ == "__main__":
    unittest.main()
