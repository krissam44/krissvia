"""Tests du SDK Python, contre un faux cœur WebSocket sur la boucle locale.

    python -m unittest discover -s sdk/python
"""

import asyncio
import json
import os
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
                {"type": "hello", "plugin_id": "cave", "plugin_version": "1.2.3", "contract_version": "0.2", "token": "secret"},
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
