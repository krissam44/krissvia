"""Tests du plugin Matter, sans appareil ni vrai serveur : traduction des nœuds, puis une session complète contre un
faux serveur Matter WebSocket qui parle comme matterjs-server (formats relevés sur matter-server 1.6.0).

    python -m unittest discover -s plugins/Krissvia.Plugin.Matter
(le SDK doit être importable : PYTHONPATH=sdk/python, ce que fait le test lancé depuis la racine du dépôt.)
"""

import asyncio
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))

from websockets.asyncio.server import serve  # noqa: E402

import matter  # noqa: E402

# Le nœud d'une prise à mesure d'énergie, au format du serveur (attributs « ep/cluster/attr », structures par numéro
# de champ). Les grands identifiants de nœud dépassent 2^53 : ceux des nœuds de test de matterjs-server.
PRISE = {
    "node_id": 18446744065119617025,
    "available": True,
    "attributes": {
        "0/40/1": "SONOFF", "0/40/3": "Orb Socket", "0/40/5": "Prise salon",
        "0/29/0": [{"0": 22, "1": 1}],
        "1/29/0": [{"0": 266, "1": 1}, {"0": 1296, "1": 1}],   # On/Off Plug-in Unit, Electrical Sensor
        "1/6/0": False,
        "1/144/4": 230500, "1/144/5": 4350, "1/144/8": 1001234,
        "1/145/1": {"0": 12345678, "1": 800000000},
    },
}


def capteur(**attributs):
    return {"node_id": 7, "available": True, "attributes": {"0/40/3": "Capteur", **attributs}}


class TraductionTests(unittest.TestCase):
    def test_la_prise_donne_un_interrupteur_et_ses_mesures_converties(self):
        n = matter.traduire(PRISE)
        uid = f"{PRISE['node_id']}:1"

        self.assertEqual("Prise salon", n.nom)
        self.assertEqual(("SONOFF", "Orb Socket"), (n.fabricant, n.modele))
        genres = {e.unique_id: e.descripteur["kind"] for e in n.entites}
        self.assertEqual({f"{uid}:onoff": "switch", f"{uid}:puissance": "sensor", f"{uid}:tension": "sensor",
                          f"{uid}:courant": "sensor", f"{uid}:energie": "sensor"}, genres)
        self.assertEqual({f"{uid}:onoff": False, f"{uid}:puissance": 1001.2, f"{uid}:tension": 230.5,
                          f"{uid}:courant": 4.35, f"{uid}:energie": 12.346}, n.valeurs())
        energie = next(e for e in n.entites if e.unique_id.endswith(":energie")).descripteur
        self.assertEqual(("energy", "kWh", "total_increasing"), (energie["device_class"], energie["unit"], energie["state_class"]))

    def test_sans_nom_donne_le_produit_puis_un_numero(self):
        self.assertEqual("Orb Socket", matter.nom_du_noeud(1, {"0/40/3": "Orb Socket", "0/40/5": " "}))
        self.assertEqual("Matter 9", matter.nom_du_noeud(9, {}))

    def test_un_contact_est_inverse_une_fuite_ne_l_est_pas(self):
        contact = matter.traduire(capteur(**{"1/29/0": [{"0": 0x0015}], "1/69/0": True}))
        fuite = matter.traduire(capteur(**{"1/29/0": [{"0": 0x0043}], "1/69/0": True}))

        self.assertEqual({"7:1:ouverture": False}, contact.valeurs())   # contact établi = fermé
        self.assertEqual({"7:1:fuite": True}, fuite.valeurs())

    def test_capteurs_et_valeurs_nulles(self):
        n = matter.traduire(capteur(**{"1/1026/0": 2154, "1/1029/0": None, "1/1030/0": 1, "1/47/12": 171,
                                       "1/1024/0": 30001}))
        self.assertEqual({"7:1:temperature": 21.54, "7:1:occupation": True, "7:1:batterie": 86,
                          "7:1:luminosite": 1000}, n.valeurs())                      # humidité nulle : rien de publié
        self.assertIn("7:1:humidite", [e.unique_id for e in n.entites])           # mais l'entité existe

    def test_une_lumiere_a_variateur_porte_sa_luminosite(self):
        n = matter.traduire(capteur(**{"1/29/0": [{"0": 0x0101}], "1/6/0": True, "1/8/0": 127}))
        lumiere = n.entites[0]

        self.assertEqual(("light", ["turn_on", "turn_off", "toggle", "set_brightness"]),
                         (lumiere.descripteur["kind"], lumiere.descripteur["commands"]))
        self.assertEqual({"7:1:lumiere": {"on": True, "brightness": 50}}, n.valeurs())
        self.assertEqual((8, "MoveToLevelWithOnOff", {"level": 254, "transitionTime": 0, "optionsMask": 0, "optionsOverride": 0}),
                         matter.ordre(lumiere, "set_brightness", {"brightness": 100}))

    def test_plusieurs_voies_sont_distinguees(self):
        n = matter.traduire(capteur(**{"1/6/0": True, "2/6/0": False}))
        self.assertEqual(["1", "2"], [e.descripteur.get("name") for e in n.entites])   # « Capteur 1 », « Capteur 2 »

    def test_ordres_marche_arret_et_refus(self):
        prise = matter.traduire(PRISE).entites[0]
        self.assertEqual((6, "On", {}), matter.ordre(prise, "turn_on", None))
        self.assertEqual((6, "Toggle", {}), matter.ordre(prise, "toggle", None))
        with self.assertRaises(ValueError):
            matter.ordre(prise, "set_brightness", {"brightness": 10})


class FauxServeurMatter:
    """Un serveur Matter réduit : accueil ServerInfo, start_listening, device_command, commission_with_code."""

    def __init__(self, noeuds):
        self.noeuds = noeuds
        self.recus: list[dict] = []
        self.ws = None
        self.pret = asyncio.Event()
        self.url = ""

    async def __aenter__(self):
        self._serveur = await serve(self._session, "127.0.0.1", 0)
        self.url = f"ws://127.0.0.1:{self._serveur.sockets[0].getsockname()[1]}/ws"
        return self

    async def __aexit__(self, *_):
        self._serveur.close()
        await self._serveur.wait_closed()

    async def _session(self, ws):
        self.ws = ws
        await ws.send(json.dumps({"fabric_id": 1, "sdk_version": "matter-server/1.6.0", "bluetooth_enabled": False,
                                  "wifi_credentials_set": False, "schema_version": 15}))
        async for brut in ws:
            m = json.loads(brut)
            self.recus.append(m)
            commande = m["command"]
            if commande == "start_listening":
                await ws.send(json.dumps({"message_id": m["message_id"], "result": self.noeuds}))
                self.pret.set()
            elif commande == "set_wifi_credentials":
                await ws.send(json.dumps({"message_id": m["message_id"], "result": None}))
            elif commande == "device_command":
                if m["args"]["node_id"] == 404:
                    await ws.send(json.dumps({"message_id": m["message_id"], "error_code": 5, "details": "Node 404 does not exist"}))
                else:
                    await ws.send(json.dumps({"message_id": m["message_id"], "result": None}))
            elif commande == "commission_with_code":
                if m["args"]["code"] == "00000000000":
                    await ws.send(json.dumps({"message_id": m["message_id"], "error_code": 1, "details": "passcode-mismatch"}))
                else:
                    await ws.send(json.dumps({"message_id": m["message_id"], "result": capteur(**{"0/40/5": "Capteur entrée", "1/1026/0": 1850})}))
            else:
                await ws.send(json.dumps({"message_id": m["message_id"], "error_code": 9, "details": f"Unknown command: {commande}"}))

    async def evenement(self, nom, donnees):
        await self.ws.send(json.dumps({"event": nom, "data": donnees}))


class FauxClient:
    """Ce que le plugin envoie au cœur."""

    def __init__(self):
        self.messages: list[dict] = []
        self.recu = asyncio.Event()

    async def send(self, message):
        self.messages.append(message)
        self.recu.set()
        return True

    def valeurs(self) -> dict:
        return {v["entity_unique_id"]: v["value"] for m in self.messages if m["type"] == "state" for v in m["states"]}

    def declarations(self) -> list[dict]:
        return [m for m in self.messages if m["type"] == "declare"]


async def attendre(condition, delai=3.0):
    async def boucle():
        while not condition():
            await asyncio.sleep(0.01)
    await asyncio.wait_for(boucle(), delai)


class SessionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.taches = []

    async def asyncTearDown(self):
        for t in self.taches:
            t.cancel()
        await asyncio.gather(*self.taches, return_exceptions=True)

    async def relier(self, serveur, plugin=None):
        plugin = plugin or matter.Matter(serveur.url)
        client = FauxClient()
        self.taches.append(asyncio.create_task(plugin.relier(client)))
        await asyncio.wait_for(serveur.pret.wait(), 3)
        await attendre(lambda: plugin.serveur is not None and client.declarations())
        return plugin, client

    async def test_les_noeuds_sont_declares_et_leurs_mesures_publiees(self):
        async with FauxServeurMatter([PRISE]) as serveur:
            plugin, client = await self.relier(serveur)
            uid = f"{PRISE['node_id']}:1"
            await attendre(lambda: f"{uid}:puissance" in client.valeurs())

            appareils = client.declarations()[-1]["devices"]
            self.assertEqual(["matter", f"noeud-{PRISE['node_id']}"], [a["unique_id"] for a in appareils])
            self.assertEqual(1001.2, client.valeurs()[f"{uid}:puissance"])
            self.assertTrue(client.valeurs()["matter:controleur"]["connected"])

            # La prise s'allume : l'attribut arrive, la valeur suit.
            await serveur.evenement("attribute_updated", [PRISE["node_id"], "1/6/0", True])
            await attendre(lambda: client.valeurs()[f"{uid}:onoff"] is True)

    async def test_un_ordre_part_en_device_command_et_l_erreur_du_serveur_est_rendue(self):
        async with FauxServeurMatter([PRISE, {**PRISE, "node_id": 404, "available": False}]) as serveur:
            plugin, _ = await self.relier(serveur)

            r = await plugin.execute({"entity_unique_id": f"{PRISE['node_id']}:1:onoff", "command": "turn_off"})
            self.assertEqual("accepted", r["outcome"])
            commande = next(m for m in serveur.recus if m["command"] == "device_command")
            self.assertEqual({"node_id": PRISE["node_id"], "endpoint_id": 1, "cluster_id": 6, "command_name": "Off",
                              "payload": {}}, commande["args"])

            injoignable = await plugin.execute({"entity_unique_id": "404:1:onoff", "command": "turn_on"})
            self.assertEqual("unreachable", injoignable["outcome"])   # nœud indisponible : l'ordre n'est pas passé
            inconnue = await plugin.execute({"entity_unique_id": "1:1:onoff", "command": "turn_on"})
            self.assertEqual("failed", inconnue["outcome"])

    async def test_l_appairage_ajoute_le_noeud_et_un_echec_est_montre(self):
        async with FauxServeurMatter([]) as serveur:
            plugin, client = await self.relier(serveur)

            sans_code = await plugin.execute({"entity_unique_id": "matter:controleur", "command": "commission", "args": {}})
            self.assertEqual("failed", sans_code["outcome"])
            r = await plugin.execute({"entity_unique_id": "matter:controleur", "command": "commission",
                                      "args": {"code": "3497-011-2332"}})
            self.assertEqual("accepted", r["outcome"])
            await attendre(lambda: client.valeurs().get("matter:controleur", {}).get("commissioning", {}).get("state") == "done")

            demande = next(m for m in serveur.recus if m["command"] == "commission_with_code")
            self.assertEqual({"code": "34970112332", "network_only": True}, demande["args"])
            self.assertEqual(18.5, client.valeurs()["7:1:temperature"])
            self.assertIn("noeud-7", [a["unique_id"] for a in client.declarations()[-1]["devices"]])

            await plugin.execute({"entity_unique_id": "matter:controleur", "command": "commission",
                                  "args": {"code": "00000000000"}})
            await attendre(lambda: client.valeurs()["matter:controleur"]["commissioning"]["state"] == "failed")
            self.assertIn("passcode-mismatch", client.valeurs()["matter:controleur"]["commissioning"]["detail"])

    async def test_un_second_appairage_aussitot_est_refuse(self):
        async with FauxServeurMatter([]) as serveur:
            plugin, _ = await self.relier(serveur)
            commande = {"entity_unique_id": "matter:controleur", "command": "commission", "args": {"code": "34970112332"}}

            premier, second = await plugin.execute(commande), await plugin.execute(commande)

            self.assertEqual(("accepted", "failed"), (premier["outcome"], second["outcome"]))

    async def test_le_bluetooth_est_refuse_sans_adaptateur_ni_wifi(self):
        async with FauxServeurMatter([]) as serveur:
            plugin, _ = await self.relier(serveur)
            r = await plugin.execute({"entity_unique_id": "matter:controleur", "command": "commission",
                                      "args": {"code": "34970112332", "bluetooth": True}})
            self.assertEqual("failed", r["outcome"])
            self.assertNotIn("commission_with_code", [m["command"] for m in serveur.recus])

    async def test_un_noeud_retire_disparait_de_la_declaration(self):
        async with FauxServeurMatter([PRISE]) as serveur:
            plugin, client = await self.relier(serveur)
            await serveur.evenement("node_removed", PRISE["node_id"])
            await attendre(lambda: [a["unique_id"] for a in client.declarations()[-1]["devices"]] == ["matter"])

    async def test_le_wifi_regle_est_transmis_au_serveur(self):
        async with FauxServeurMatter([]) as serveur:
            plugin = matter.Matter(serveur.url, "MaBox", "secret")
            client = FauxClient()
            self.taches.append(asyncio.create_task(plugin.relier(client)))
            await attendre(lambda: any(m["command"] == "set_wifi_credentials" for m in serveur.recus))
            demande = next(m for m in serveur.recus if m["command"] == "set_wifi_credentials")
            self.assertEqual({"ssid": "MaBox", "credentials": "secret"}, demande["args"])
            await attendre(lambda: client.declarations() and client.valeurs()["matter:controleur"]["wifi_credentials_set"])

    async def test_serveur_injoignable_le_controleur_le_dit(self):
        plugin = matter.Matter("ws://127.0.0.1:1/ws")
        client = FauxClient()
        self.taches.append(asyncio.create_task(plugin.relier(client)))
        await attendre(lambda: "matter:controleur" in client.valeurs(), 15)
        self.assertFalse(client.valeurs()["matter:controleur"]["connected"])
        r = await plugin.execute({"entity_unique_id": "matter:controleur", "command": "commission", "args": {"code": "1"}})
        self.assertEqual("unreachable", r["outcome"])


if __name__ == "__main__":
    unittest.main()
