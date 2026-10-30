# Appli mobile Krissvia

MAUI Blazor Hybrid + MudBlazor, sur le modèle de l'appli de DomoMud. Écrans : **Thermostats** (température,
consigne régulée, statut, part de chauffe, modes, ±0,5 °C, planning et prochaine bascule) et **Sondes** (une carte par
pièce avec la moyenne du serveur, ses sondes, humidité, pile, sondes muettes ou hors ligne signalées).
**Radiateurs** : qui chauffe en ce moment (le `heating` du cœur, qui suit le relais, préchauffage compris), en tête de
liste, avec trois actions rapides Confort / Éco / Arrêt — seulement celles que le thermostat déclare, avec ses libellés.
Mêmes données que l'écran Thermostats (le flux partagé) : rien à relire en passant de l'un à l'autre.
**Prises** : interrupteurs et lumières par pièce (`GET /api/actuators`), basculés d'un appui sur la ligne. Ordre
vérifié, pas d'état optimiste : la ligne attend la réponse, un ordre non confirmé le dit, puis l'écran se relit. Un
radiateur de thermostat ne se commande pas d'ici, un équipement délesté se rallume après confirmation. Les thermostats se créent et
se règlent sur le web ; ici on les suit et on les commande.

| Dossier | Rôle |
|---|---|
| `src/Krissvia.Mobile.Client` | Tout ce qui ne dépend pas de MAUI : client de l'API, flux en direct, lecture des entités `climate`, adresse normalisée. Dans `Krissvia.slnx`, testé par `tests/Krissvia.Mobile.Client.Tests`. |
| `mobile/Krissvia.Mobile` | L'appli (Android, et Windows pour essayer depuis le PC). **Hors de `Krissvia.slnx`** : le workload MAUI n'existe ni sur la VM ni sur le Pi, et `publier.ps1` doit continuer à marcher. Solution à part : `mobile/Krissvia.Mobile.slnx`. |

## Ce qu'elle fait avec le serveur

- **Clé d'API** (`X-Api-Key`) : la valeur de `Api:Key` (ou `Api__Key`) du serveur, gardée dans le coffre de l'appareil.
  Sans clé configurée, le serveur de production répond 503 : l'appli le dit.
- **Liste** par `GET /api/entities` (seules les entités `climate` actives sont gardées), puis **flux**
  `/api/live?entities=<thermostats>` : rien n'est interrogé périodiquement. La liste est relue à chaque connexion, et
  au retour au premier plan (Android coupe souvent la connexion en silence).
- **Ordres** par le bus de commandes (`POST /api/entities/{id}/commands`, source `mobile`) : `set_mode`,
  `adjust_target` (relatif : deux « + » rapides font +1 °C), `set_schedule`. Aucun état optimiste : l'écran attend
  l'état publié. Un refus s'affiche avec le motif du serveur ; un succès ne dit rien.
- **Rien n'est recalculé** : statut (`status_label`), consigne régulée, prochaine bascule viennent du cœur. Les couleurs
  partent des codes (`heating`, `eco`…), jamais des libellés. Le test `L_appli_mobile_lit_l_etat_et_les_modes_publies`
  (Climate.Tests) fait lire à l'appli l'état que `ThermostatEntities` publie vraiment : un champ renommé côté cœur le
  fait échouer.

- **Sondes** : `GET /api/sensors`, relu toutes les 30 s et au retour au premier plan. Moyennes et santé des sondes
  (règle des 60 min) viennent du cœur ; contrat gardé par `L_appli_mobile_lit_les_moyennes_et_la_sante_des_sondes`
  (Core.Tests). Contre un serveur qui ne connaît pas encore la route, l'appli dit qu'il faut le mettre à jour.

## Construire et essayer

```bash
dotnet test tests/Krissvia.Mobile.Client.Tests
dotnet build mobile/Krissvia.Mobile/Krissvia.Mobile.csproj -f net10.0-windows10.0.19041.0
```

Puis lancer `mobile/Krissvia.Mobile/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Krissvia.Mobile.exe` directement :
`dotnet build -t:Run` échoue sur un chemin qui contient une espace (« Projets Domotique »). Fermer l'appli avant de
reconstruire, sinon l'exe verrouillé fait relancer l'ancienne version sans qu'on le voie.

Adresse : `http://192.168.1.10:<port>` (sans `http://`, une adresse IP ou un nom sans point prend `http` ; un `/api`
final est retiré). Android accepte le http grâce à `usesCleartextTraffic` dans le manifeste : c'est voulu pour le
réseau local, l'accès hors de la maison passera par une adresse https.

**Attention** : contre le vrai serveur, les ordres pilotent les vrais radiateurs.

## APK Android

Comme pour DomoMud (pièges détaillés dans sa mémoire) : `-p:AndroidPackageFormat=apk` (sinon un `.aab`), signer avec
`-t:SignAndroidPackage`, prendre le `…-Signed.apk` dans `bin/Release/net10.0-android/` (pas dans `publish/`), et
vérifier sa date avant de le copier. Le keystore reste hors du dépôt.
