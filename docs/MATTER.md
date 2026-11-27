# Matter

> Livré le 20/11/2026. Plugin `plugins/Krissvia.Plugin.Matter` (Python), carte « Matter » livrée par le plugin,
> classe `matter_controller`. **Vérifié le 27/11/2026 avec une vraie prise** SONOFF Orb Socket WS01TPE-E (Matter sur
> Wi-Fi, mesure d'énergie), sur le Raspberry Pi de l'appartement (§ 5).

## 1. Le principe

Matter est un protocole domotique standard. Ses appareils parlent soit **Wi-Fi** (aucun matériel à ajouter), soit
**Thread** (il faut alors un routeur de bordure Thread : dongle, Apple TV, HomePod, Nest). La fiche d'un appareil le
dit toujours : « Matter over Wi-Fi » ou « Matter over Thread ».

Krissvia ne parle pas Matter lui-même. Le protocole (chiffrement, certificats, appairage) est l'affaire d'un
**contrôleur** dédié, le **Matter Server** de l'Open Home Foundation — celui de Home Assistant :

```
Appareils Matter (Wi-Fi)  ⇄  Matter Server (matterjs-server)  ⇄ WebSocket ⇄  plugin Matter  ⇄  cœur Krissvia
```

C'est le même schéma que Zigbee2MQTT pour le Zigbee. `python-matter-server` a été **archivé le 23/06/2026** (8.1.2,
dernière version) ; son successeur est **matterjs-server**, qui garde la même API WebSocket.

## 2. Installer le Matter Server (sur le Pi)

Node.js **22.13 ou plus** (24 recommandé). Deux façons :

**npm, en service systemd** (le plus léger, comme les autres services du Pi) :

```bash
sudo mkdir -p /opt/matter-server /var/lib/matter-server && sudo chown christian: /opt/matter-server /var/lib/matter-server
cd /opt/matter-server && npm install matter-server
/opt/matter-server/node_modules/.bin/matter-server --storage-path /var/lib/matter-server --listen-address 127.0.0.1
```

puis l'unité systemd (celle installée sur le Pi le 27/11/2026, où le Node.js 22.23 déjà présent a suffi) :

```bash
sudo tee /etc/systemd/system/matter-server.service > /dev/null <<'EOF'
[Unit]
Description=Matter Server
After=network-online.target
Wants=network-online.target

[Service]
User=christian
ExecStart=/opt/matter-server/node_modules/.bin/matter-server --storage-path /var/lib/matter-server --listen-address 127.0.0.1
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload && sudo systemctl enable --now matter-server
journalctl -u matter-server -n 20 --no-pager      # « Webserver listening on http://127.0.0.1:5580 »
```

Le paquet `nodejs` de Debian/Raspberry Pi OS est trop ancien : il faut Node.js 22.13 ou plus (`node --version`),
sinon celui de NodeSource.

**Docker** : `docker run -d --name matterjs-server --restart=unless-stopped --network=host -v $(pwd)/data:/data
ghcr.io/matter-js/matterjs-server:stable` (UID 1000 dans le conteneur : `chown -R 1000:1000 data`).

- Le serveur écoute `ws://localhost:5580/ws` (`--port`), et sert un tableau de bord sur `http://localhost:5580/`.
  `--listen-address 127.0.0.1` le réserve au Pi : le plugin tourne sur la même machine, et rien d'autre du réseau n'a
  à lui parler.
- **IPv6 et mDNS** sont indispensables : c'est ainsi que les appareils Matter s'annoncent et se joignent. Vérifier
  `ip -6 addr` (une adresse `fe80::` au moins sur l'interface du réseau local) ; `--primary-interface` si plusieurs.
- Le serveur garde ses clés et ses appareils dans `--storage-path` : **à sauvegarder** (sinon il faut tout réappairer).
- Ressources : un processus Node.js de plus sur le Pi (prévoir une centaine de Mo).

## 3. Le plugin

Page **Services → Matter (Python)** : adresse du serveur (par défaut `ws://localhost:5580/ws`), puis **Activé**.

Un appareil Krissvia par nœud Matter, nommé par son nom Matter (NodeLabel), sinon son produit :

| Matter (cluster/attribut) | Entité Krissvia |
|---|---|
| OnOff (6/0) | `switch` (`turn_on`, `turn_off`, `toggle`) ; `light` si l'appareil se déclare lumière, avec `set_brightness` s'il a un variateur (8/0) |
| Puissance (144/8, mW) · tension (144/4, mV) · courant (144/5, mA) | `power` W · `voltage` V · `current` A |
| Énergie cumulée importée (145/1, mWh) | `energy` kWh, `total_increasing` |
| Température (1026/0) · humidité (1029/0) · luminosité (1024/0) | °C · % · lx |
| Occupation (1030/0) | `binary_sensor` `occupancy` |
| Contact (69/0) | `binary_sensor` `opening` — **inversé** : pour Matter, vrai = contact établi = fermé ; détecteur de fuite (type 0x0043) : `moisture`, non inversé |
| Batterie (47/12, demi-points) | `battery` % |

- Un endpoint par voie : un appareil à plusieurs voies a une entité par voie (« Prise 1 », « Prise 2 »).
- Une valeur Matter nulle n'est pas publiée (pas de « 0 » inventé).
- Serveur injoignable : les appareils passent indisponibles, le plugin se reconnecte (5 s, puis jusqu'à 60 s).
- L'ordre est accepté quand le serveur l'a transmis ; l'**état** qui fait foi est celui que l'appareil renvoie ensuite.

## 4. Appairer un appareil

Poser la carte **Matter** sur le tableau de bord (entité « Matter Contrôleur ») : elle dit si le serveur est joint,
liste les appareils connus, et appaire un nouvel appareil à partir de son **code d'appairage** (11 chiffres, ou le
texte `MT:…` du QR code). L'appairage prend jusqu'à 3 minutes ; son issue s'affiche sur la carte.

**Le plus simple — depuis l'appli du fabricant (« multi-admin »)** : un appareil Matter accepte plusieurs
contrôleurs. Pour la prise SONOFF (eWeLink, novembre 2026) :

1. l'appairer d'abord dans **eWeLink**, comme prévu par le fabricant ;
2. préparer Krissvia : service `matter-server` actif, plugin Matter activé, carte Matter « CONNECTÉ » — le mode
   appairage de la prise ne dure que quelques minutes ;
3. dans eWeLink : l'appareil → **Paramètres de dispositif** → tout en bas, **« Activer le mode d'appairage »**. Ce
   n'est **pas** le bouton « Partager » (il partage la prise avec un autre utilisateur eWeLink), ni la rubrique
   « Plateformes compatibles Matter » (une simple liste). L'appli affiche un code de 11 chiffres (ou un QR code) ;
4. le taper dans la carte Matter → **Appairer** (tirets et espaces acceptés).

L'appareil est déjà sur le Wi-Fi : le Pi le trouve sur le réseau (IPv6/mDNS), aucun Bluetooth n'est nécessaire, et
l'appli d'origine continue de fonctionner à côté.

**Appareil neuf, par Bluetooth** : le Pi 4 a le Bluetooth intégré. Lancer le serveur avec `--bluetooth-adapter 0`
(BlueZ actif, adaptateur allumé : `bluetoothctl power on`), et renseigner le Wi-Fi (nom et mot de passe) dans les
réglages du plugin : le serveur le transmet à l'appareil pendant l'appairage. La case « Appareil neuf, par
Bluetooth » apparaît alors sur la carte, avec le code imprimé sur l'appareil.

## 5. Vérifié

**Avec une vraie prise, le 27/11/2026** — SONOFF Orb Socket WS01TPE-E (micrologiciel 1.0.4, puce Beken BK7238), sur
le Raspberry Pi 4 de l'appartement, `matter-server` installé par npm en service systemd (§ 2) :

- appairage par le code de « Activer le mode d'appairage » d'eWeLink, tapé dans la carte Matter : « Smart Plug
  appairé » ; eWeLink continue de fonctionner à côté ;
- nom de l'appareil : « Smart Plug », le produit (`0/40/3`) — la prise ne porte pas de nom Matter (`0/40/5`) ; le
  renommer dans l'écran Entités ;
- entités créées : l'interrupteur (`switch`) et les quatre mesures — courant, énergie, puissance, tension — qui se
  mettent à jour ; la prise se pilote depuis Krissvia (marche/arrêt), son état suit ;
- carte conseillée : **« Compteur d'énergie »** posée sur l'entité puissance (elle relie d'elle-même tension, courant
  et énergie du même appareil), et une carte « Prise » pour le bouton.

**Contre un vrai serveur, le 20/11/2026** — `matter-server` 1.6.0 lancé sur la boucle locale du PC de développement : contre un vrai `matter-server` 1.6.0 lancé sur la boucle locale du PC de développement :
message d'accueil (`ServerInfo`), format des requêtes et des réponses (`{message_id, result}` /
`{message_id, error_code, details}`), `start_listening`, événements `node_added`, `device_command` accepté, et un
nœud de test (prise à mesure) importé avec `import_test_node` : déclaré dans Krissvia avec ses mesures converties,
carte Matter comprise. Les autres commandes et événements suivent les types publiés du client officiel
(`@matter-server/ws-client`), et sont couverts par les tests contre un faux serveur.

**Pas encore vérifié** : l'appairage par Bluetooth d'un appareil neuf, et les autres familles d'appareils
(lumières, capteurs).

Sources : https://github.com/matter-js/matterjs-server , https://github.com/OpenHomeFoundation/roadmap/issues/89 ,
https://github.com/matter-js/python-matter-server
