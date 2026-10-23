"""Tests du SDK Python, contre un faux cœur WebSocket sur la boucle locale.

    python -m unittest discover -s sdk/python
"""

import asyncio
import json
import os
import subprocess
import sys
import threading
import unittest
from unittest import mock

from websockets.asyncio.server import serve

import krissvia


class FauxCoeur:
    """Un cœur réduit à ce que le contrat exige : il note tout ce qu'il reçoit."""

    def __init__(self, reponse=None, heartbeat=15, timeout=45, apres_welcome=None):
        self.recus: list[dict] = []
        self.sessions = 0
        self.reponse = reponse or {"type": "welcome", "contract_version": "0.2"}
        self.heartbeat, self.timeout = heartbeat, timeout
        self.apres_welcome = apres_welcome  # coroutine (ws) jouée après le welcome
        self.message = asyncio.Event()
        self.url = ""
        self._serveur = None

    async def __aenter__(self):
        self._serveur = await serve(self._session, "127.0.0.1", 0)
        port = self._serveur.sockets[0].getsockname()[1]
        self.url = f"ws://127.0.0.1:{port}/plugins"
        return self

    async def __aexit__(self, *_):
        self._serveur.close()
        await self._serveur.wait_closed()

    async def _session(self, ws):
        self.sessions += 1
        try:
            hello = json.loads(await ws.recv())
            self._noter(hello)
            reponse = dict(self.reponse)
            if reponse["type"] == "welcome":
                reponse.update(heartbeat_seconds=self.heartbeat, timeout_seconds=self.timeout)
            await ws.send(json.dumps(reponse))
            if reponse["type"] != "welcome":
                await ws.close()
                return
            if self.apres_welcome:
                asyncio.create_task(self.apres_welcome(ws))
            async for trame in ws:
                self._noter(json.loads(trame))
        except Exception:  # noqa: BLE001 - la connexion fermée par le client finit la session
            pass

    def _noter(self, message):
        self.recus.append(message)
        self.message.set()

    async def attendre(self, condition, delai=3.0):
        async def boucle():
            while not condition():
                self.message.clear()
                await self.message.wait()
        await asyncio.wait_for(boucle(), delai)

    def de_type(self, genre):
        return [m for m in self.recus if m.get("type") == genre]


class Cave(krissvia.Plugin):
    def __init__(self):
        self.ordres = []

    def snapshot(self):
        return [
            krissvia.declare(krissvia.device("cave", "Cave", [
                krissvia.entity("cave:chauffe_eau", "switch", "Chauffe-eau", commands=["turn_on", "turn_off"]),
            ])),
            krissvia.state(krissvia.value("cave:chauffe_eau", False)),
            krissvia.availability(True, device_unique_id="cave"),
        ]

    async def execute(self, command):
        self.ordres.append(command)
        if command["command"] == "casser":
            raise RuntimeError("relais grillé")
        return krissvia.accepted()


class SessionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.taches = []

    async def asyncTearDown(self):
        for t in self.taches:
            t.cancel()
        await asyncio.gather(*self.taches, return_exceptions=True)

    def lancer(self, client):
        self.taches.append(asyncio.create_task(client.run()))

    async def test_hello_puis_resynchronisation_complete_dans_l_ordre(self):
        async with FauxCoeur() as coeur:
            client = krissvia.CoreClient(Cave(), plugin_version="1.2.3", url=coeur.url, plugin_id="cave", token="secret")
            self.lancer(client)
            await coeur.attendre(lambda: len(coeur.recus) >= 4)

            hello = coeur.recus[0]
            self.assertEqual(
                {"type": "hello", "plugin_id": "cave", "plugin_version": "1.2.3", "contract_version": krissvia.CONTRACT_VERSION, "token": "secret"},
                hello)
            self.assertEqual(["hello", "declare", "state", "availability"], [m["type"] for m in coeur.recus[:4]])
            self.assertTrue(client.connected)

    async def test_un_ordre_recoit_toujours_son_resultat_meme_en_echec(self):
        async def ordonner(ws):
            await ws.send(json.dumps({"type": "command", "id": "a1", "entity_unique_id": "cave:chauffe_eau", "command": "turn_on"}))
            await ws.send(json.dumps({"type": "command", "id": "a2", "entity_unique_id": "cave:chauffe_eau", "command": "casser"}))
            await ws.send(json.dumps({"type": "configure", "config": {}}))

        plugin = Cave()
        async with FauxCoeur(apres_welcome=ordonner) as coeur:
            self.lancer(krissvia.CoreClient(plugin, url=coeur.url, plugin_id="cave"))
            await coeur.attendre(lambda: len(coeur.de_type("command_result")) == 2 and coeur.de_type("configure_result"))

            resultats = {r["id"]: r for r in coeur.de_type("command_result")}
            self.assertEqual("accepted", resultats["a1"]["outcome"])
            self.assertEqual({"type": "command_result", "id": "a2", "outcome": "failed", "detail": "relais grillé"}, resultats["a2"])
            self.assertFalse(coeur.de_type("configure_result")[0]["ok"])
            self.assertEqual(["turn_on", "casser"], [o["command"] for o in plugin.ordres])

    async def test_un_refus_attend_avant_de_retenter(self):
        async with FauxCoeur(reponse={"type": "rejected", "reason": "invalid_token"}) as coeur:
            client = krissvia.CoreClient(Cave(), url=coeur.url, plugin_id="cave", rejected_delay=0.3, reconnect_delays=(0.01,))
            self.lancer(client)
            await asyncio.sleep(0.15)
            self.assertEqual(1, coeur.sessions)  # pas de martèlement après un refus
            await coeur.attendre(lambda: coeur.sessions >= 2)
            self.assertFalse(client.connected)
            self.assertEqual([], coeur.de_type("declare"))  # rien n'est déclaré sans welcome

    async def test_un_coeur_muet_est_quitte_et_le_client_se_reconnecte(self):
        # Le faux cœur n'envoie jamais rien après le welcome : silence au-delà du délai → reconnexion.
        async with FauxCoeur(heartbeat=0.05, timeout=0.2) as coeur:
            self.lancer(krissvia.CoreClient(Cave(), url=coeur.url, plugin_id="cave", reconnect_delays=(0.01,)))
            # Resynchronisé à chaque session : un second declare, sur une seconde connexion.
            await coeur.attendre(lambda: len(coeur.de_type("declare")) >= 2)
            self.assertGreaterEqual(coeur.sessions, 2)
            self.assertTrue(coeur.de_type("ping"))  # il battait pendant la première session

    async def test_rien_ne_part_hors_session(self):
        client = krissvia.CoreClient(Cave(), url="ws://127.0.0.1:1/plugins", plugin_id="cave")
        self.assertFalse(await client.send(krissvia.state(krissvia.value("cave:chauffe_eau", True))))


class ParentTests(unittest.IsolatedAsyncioTestCase):
    """Un plugin lancé par le cœur ne lui survit pas : il surveille le processus Krissvia__ParentPid."""

    async def test_le_client_rend_la_main_quand_le_coeur_parent_disparait(self):
        verifications = []

        def vivant(pid):
            verifications.append(pid)
            return len(verifications) < 4

        async with FauxCoeur() as coeur:
            client = krissvia.CoreClient(Cave(), url=coeur.url, plugin_id="cave", parent_pid=4242,
                                         parent_check=0.05, parent_alive=vivant)
            await asyncio.wait_for(client.run(), 3)

            self.assertEqual([4242] * 4, verifications)
            self.assertEqual(1, coeur.sessions)  # connecté pendant ce temps, comme d'habitude
            self.assertFalse(client.connected)

    async def test_un_plugin_lance_a_la_main_ne_surveille_rien(self):
        interroge = []
        with mock.patch.dict(os.environ, {}, clear=True):
            client = krissvia.CoreClient(Cave(), url="ws://127.0.0.1:1/plugins", plugin_id="cave",
                                         reconnect_delays=(0.01,), parent_check=0.01,
                                         parent_alive=lambda pid: interroge.append(pid) or False)
        with self.assertRaises(asyncio.TimeoutError):
            await asyncio.wait_for(client.run(), 0.3)
        self.assertEqual([], interroge)

    def test_le_pid_du_parent_vient_de_l_environnement(self):
        with mock.patch.dict(os.environ, {"Krissvia__ParentPid": "1234"}):
            self.assertEqual(1234, krissvia.CoreClient(Cave()).parent_pid)
        with mock.patch.dict(os.environ, {"Krissvia__ParentPid": "cœur"}):
            self.assertIsNone(krissvia.CoreClient(Cave()).parent_pid)

    def test_un_processus_vivant_est_reconnu_et_un_processus_sorti_ne_l_est_plus(self):
        self.assertTrue(krissvia.process_alive(os.getpid()))
        bref = subprocess.Popen([sys.executable, "-c", "pass"])
        bref.wait()
        self.assertFalse(krissvia.process_alive(bref.pid))


class RunTests(unittest.TestCase):
    def test_la_disparition_du_coeur_arrete_aussi_les_taches_du_plugin(self):
        annulee = []

        async def relever(plugin, client):
            try:
                await asyncio.sleep(3600)
            except asyncio.CancelledError:
                annulee.append(True)
                raise

        # Dans un fil à part : si le plugin ne s'arrête pas, le test échoue au lieu de bloquer la suite.
        fil = threading.Thread(target=krissvia.run, daemon=True, args=(Cave(), relever), kwargs=dict(
            url="ws://127.0.0.1:1/plugins", plugin_id="cave", reconnect_delays=(0.01,),
            parent_pid=4242, parent_check=0.01, parent_alive=lambda pid: False))
        fil.start()
        fil.join(5)

        self.assertFalse(fil.is_alive(), "le plugin a survécu à la disparition du cœur")
        self.assertEqual([True], annulee)


class Contrat03Tests(unittest.IsolatedAsyncioTestCase):
    """Contrat 0.3 : suivre et commander d'autres entités (docs/CONTRACT-V0.3.md)."""

    async def asyncSetUp(self):
        self.taches = []

    async def asyncTearDown(self):
        for t in self.taches:
            t.cancel()
        await asyncio.gather(*self.taches, return_exceptions=True)

    async def test_l_abonnement_repart_a_chaque_session_et_les_etats_arrivent_au_plugin(self):
        recus = []

        class Platine(Cave):
            def on_entity_state(self, message):
                recus.append(message)

        async def pousser(ws):
            await ws.send(json.dumps({"type": "entity_state", "entity_id": "climate.salon", "name": "Salon",
                                      "kind": "climate", "available": True, "value": {"mode": "eco"}}))

        async with FauxCoeur(apres_welcome=pousser) as coeur:
            client = krissvia.CoreClient(Platine(), url=coeur.url, plugin_id="openhasp", reconnect_delays=(0.01,))
            await client.subscribe(["climate.salon", "climate.salon", "switch.lampe"])   # avant toute session
            self.taches.append(asyncio.create_task(client.run()))

            await coeur.attendre(lambda: coeur.de_type("subscribe") and recus)
            self.assertEqual([{"type": "subscribe", "entity_ids": ["climate.salon", "switch.lampe"]}], coeur.de_type("subscribe"))
            self.assertEqual("availability", coeur.recus[coeur.recus.index(coeur.de_type("subscribe")[0]) - 1]["type"])  # après la resynchro
            self.assertEqual({"mode": "eco"}, recus[0]["value"])

    async def test_l_etat_du_lien_part_a_chaque_session_et_garde_son_motif(self):
        async with FauxCoeur() as coeur:
            client = krissvia.CoreClient(Cave(), url=coeur.url, plugin_id="openhasp", reconnect_delays=(0.01,))
            self.assertFalse(await client.report_link("mqtt", False, "broker injoignable (adresse ou port)"))  # hors session
            await client.report_link("mqtt", False)   # sans motif : on garde celui qu'on connaît
            self.taches.append(asyncio.create_task(client.run()))

            await coeur.attendre(lambda: coeur.de_type("link_state"))
            self.assertEqual([{"type": "link_state", "link": "mqtt", "connected": False,
                               "detail": "broker injoignable (adresse ou port)"}], coeur.de_type("link_state"))

    async def test_un_ordre_rend_son_resultat_et_hors_session_jamais_un_faux_succes(self):
        hors_session = await krissvia.CoreClient(Cave(), url="ws://127.0.0.1:1/x", plugin_id="openhasp").command("switch.lampe", "turn_on")
        self.assertEqual("unreachable", hors_session["outcome"])

        socket = {}

        async def garder(ws):
            socket["ws"] = ws

        async with FauxCoeur(apres_welcome=garder) as coeur:
            client = krissvia.CoreClient(Cave(), url=coeur.url, plugin_id="openhasp")
            self.taches.append(asyncio.create_task(client.run()))
            await coeur.attendre(lambda: client.connected and "ws" in socket)

            ordre = asyncio.create_task(client.command("switch.lampe", "turn_off"))
            await coeur.attendre(lambda: coeur.de_type("entity_command"))
            demande = coeur.de_type("entity_command")[0]
            self.assertEqual({"type": "entity_command", "id": demande["id"], "entity_id": "switch.lampe", "command": "turn_off"}, demande)
            await socket["ws"].send(json.dumps({"type": "entity_command_result", "id": "un-autre", "outcome": "failed"}))
            await socket["ws"].send(json.dumps({"type": "entity_command_result", "id": demande["id"], "outcome": "accepted",
                                                "verification": "confirmed"}))

            resultat = await asyncio.wait_for(ordre, 3)
            self.assertEqual("accepted", resultat["outcome"])
            self.assertEqual("confirmed", resultat["verification"])


class MessagesTests(unittest.TestCase):
    def test_les_champs_absents_ne_sont_pas_ecrits(self):
        e = krissvia.entity("m:t", "sensor", "Température", device_class="temperature", unit="°C")
        self.assertEqual({"unique_id": "m:t", "kind": "sensor", "name": "Température", "device_class": "temperature",
                          "unit": "°C", "commands": []}, e)
        self.assertEqual({"type": "availability", "online": False, "device_unique_id": "m"},
                         krissvia.availability(False, device_unique_id="m"))
        self.assertEqual({"entity_unique_id": "m:t", "value": 12.5}, krissvia.value("m:t", 12.5))

    def test_une_liste_de_reglages_est_relue_dans_l_ordre_quelle_que_soit_la_casse(self):
        env = {"Lieux__1__Nom": "Vertou", "LIEUX__0__NOM": "La Trinité", "Lieux__0__Latitude": "47.58",
               "Lieux__10__Nom": "Paris", "Lieux__2__Vide": "", "Autre__0__Nom": "x"}
        with mock.patch.dict(os.environ, env, clear=True):
            lieux = krissvia.settings_list("Lieux")
        self.assertEqual([{"nom": "La Trinité", "latitude": "47.58"}, {"nom": "Vertou"}, {"nom": "Paris"}], lieux)

    def test_les_reglages_suivent_la_convention_dotnet(self):
        with mock.patch.dict(os.environ, {"Meteo__Latitude": "47.65", "Meteo__Actif": "true", "Meteo__Vide": ""}):
            self.assertEqual(47.65, krissvia.setting("Meteo:Latitude", convert=float))
            self.assertTrue(krissvia.setting("Meteo:Actif", convert=bool))
            self.assertEqual(10, krissvia.setting("Meteo:Vide", 10, int))
            self.assertEqual(10, krissvia.setting("Meteo:Absent", 10, int))
        with mock.patch.dict(os.environ, {"Meteo__Latitude": "nord"}):
            with self.assertRaisesRegex(ValueError, "Meteo:Latitude"):
                krissvia.setting("Meteo:Latitude", convert=float)


if __name__ == "__main__":
    unittest.main()
