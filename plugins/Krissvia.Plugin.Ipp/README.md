# Plugin Imprimantes IPP

Plugin C# : l'état et les consommables (toner, tambour, rouleau de transfert, four…) des imprimantes réseau
compatibles IPP — presque toutes depuis une dizaine d'années, et toutes celles qui affichent « AirPrint » ou « IPP
Everywhere ». Sur le modèle du plugin IPPPrinter de Gladys.

**Lecture seule.** La seule requête que le plugin sait écrire est `Get-Printer-Attributes`. Il ne peut pas imprimer,
annuler un travail ou changer un réglage.

## Pourquoi en C#, et sans bibliothèque IPP

- C#, comme Proxmox et Livebox : une imprimante se lit en HTTP, rien que le SDK C# ne fasse déjà (connexion au cœur,
  relance, `Krissvia:ParentPid`). Python n'apporterait qu'un deuxième environnement à installer sur la VM et le Pi.
- IPP est un protocole **binaire** posé sur HTTP (RFC 8010) : un en-tête de 8 octets, puis des attributs
  « étiquette, longueur, nom, longueur, valeur ». Écrire une requête et lire une réponse tient en une centaine de lignes
  (`Protocole/IppCodec.cs`). Les paquets NuGet existants (SharpIpp…) savent tout faire, impression comprise : une
  dépendance de plus à suivre, pour ne se servir que d'une opération — et précisément pas de celle qu'on ne veut jamais
  envoyer.
- La découverte mDNS est maison aussi (`Decouverte/Mdns.cs`) : une question DNS et la lecture de quatre types
  d'enregistrements, pour la même raison.

## Trouver les imprimantes

- **Découverte** (activée par défaut) : une question mDNS `_ipp._tcp.local` et `_ipps._tcp.local` au démarrage, puis
  toutes les 15 min. Elle part en mDNS ordinaire, depuis le port 5353 partagé avec le répondeur du système, réponses
  écoutées sur le groupe 224.0.0.251 ; et en « monodiffusion à l'ancienne » (RFC 6762 §6.7), en secours. La première
  version n'avait que la seconde : la Samsung M2020 de l'utilisateur n'y répond pas (essai du 24/10/2026 sur son
  réseau). Le journal donne l'adresse de chaque imprimante trouvée. Une imprimante qui s'annonce en `_ipp` et `_ipps` reste **une** imprimante : elles
  partagent leur UUID.
- **Adresses saisies** (page Services) : pour une imprimante que la découverte ne voit pas (autre sous-réseau, mDNS
  coupé). Formes acceptées : `http://192.168.1.63:631/ipp/print`, `ipp://192.168.1.63/ipp/print`, `ipps://…`, ou
  l'adresse IP seule (complétée en `ipp://IP:631/ipp/print`, le chemin d'IPP Everywhere). Sans port écrit, c'est 631,
  même derrière `http://`. Une imprimante qui répond « introuvable » (0x0406) a un autre chemin : `/ipp`,
  `/printers/<nom>` (CUPS)…
- **Mémoire** : le plugin retient les imprimantes déjà lues (`imprimantes.json`, dans son dossier de données). Une
  imprimante de maison passe sa vie éteinte : sans cette mémoire, elle ne serait pas déclarée au lancement suivant, et
  ses cartes diraient « Entité inconnue » jusqu'à ce qu'on la rallume. Une imprimante dont on retire l'adresse des
  réglages (ou découverte, quand on coupe la découverte) n'est plus déclarée : ses entités passent « disparues ».

## Entités

Par imprimante, un appareil dont la clé est l'UUID de l'imprimante (`printer-uuid`) — il survit à un changement
d'adresse IP ; à défaut d'UUID (imprimante ancienne), son adresse.

| Entité | Famille, classe | Valeur |
|---|---|---|
| Imprimante | `sensor`, `printer` | objet : `name`, `model`, `url`, `state` (`idle`, `processing`, `stopped`), `state_label`, `message`, `reasons`, `alerts` (en français), `problem`, `markers` (`id`, `name`, `level`, `some_remaining`, `low`, `low_threshold`, `type`, `color`), `lowest`, `last_read`. Commande `refresh`. |
| État | `sensor`, `enum` | « Prête », « En impression », « Arrêtée » |
| Problème | `binary_sensor`, `problem` | vrai : la supervision envoie l'alerte |
| un par consommable (« Black Toner », « Fuser Life »…) | `sensor`, % | niveau, pour l'historique ; pas d'état quand l'imprimante ne le connaît pas |

- **Nom d'un consommable** : sans le numéro de série que certaines imprimantes y collent (Samsung :
  `Black Toner_S/N_:CRUM-18020727263`). Le garder aurait changé l'entité à chaque cartouche neuve.
- **Niveaux spéciaux** de la RFC 3805 : -3 « il en reste » (la jauge dit « OK »), -2 et -1 inconnu (« ? »). Jamais un
  faux 0 %.
- **Seuil « bas »** : celui de l'imprimante (`marker-low-levels`) quand elle le donne, sinon le réglage (10 %).
- **Échelle** : un niveau se lit sur son maximum (`marker-high-levels`, souvent 100 mais pas toujours).
- **`printer-supply`** (PWG 5100.13) : lu quand l'imprimante n'envoie aucun `marker-*` (certaines Epson). Repris de
  l'intégration Gladys ([guim31/gladys-ipp](https://github.com/guim31/gladys-ipp)).
- **Ce que l'IPP ne dit pas** : la Samsung M2020 n'y publie que son **toner**, même en IPP/2.0. Les rouleaux et le
  four sont lus en **SNMP** (voir ci-dessous).
- **Nom de l'appareil** : `printer-info`, sauf quand il ne fait que répéter le modèle suivi du nom réseau (Samsung :
  « Samsung M2020 Series (SEC30CDA7201287) ») — le modèle seul, alors.

## Pièces en SNMP

Beaucoup de lasers ne publient en IPP que leurs cartouches ; les **pièces** (tambour, four, rouleaux, courroie,
récupérateur) sont dans la table `prtMarkerSupplies` de la Printer MIB (RFC 3805), en SNMP. Le plugin la lit en
complément, comme l'intégration Gladys, avec la même prudence :

- **Seulement les pièces** : une cartouche SNMP n'est jamais ajoutée, l'IPP les couvre. Une pièce se reconnaît à son
  type (`opc`, `fuser`, `transfer-unit`…) ou, pour le type « autre », à son nom (« Roller », « Drum », « Fuser »,
  « Waste »…) : la Samsung range ses rouleaux de transfert et d'entraînement en « autre ». Une ligne inconnue est
  écartée, et une pièce de même type ou de même identifiant qu'un consommable IPP aussi (pas de doublon).
- **Aucun consommable IPP** : la table remplace tout.
- **Niveaux ramenés à leur capacité** : la Samsung compte en pages (25 743 sur 30 000 = 86 %).
- **Écrit à la main** (`Snmp/SnmpCodec.cs`) : la seule requête est un `GetNextRequest`, une lecture ; quatre colonnes à
  la fois, cinq échanges pour une laser à quatre consommables. Vers l'adresse de l'imprimante seulement, jamais en
  balayage, et seulement après une réponse IPP.
- **v2c d'abord, v1 en secours**, version retenue par imprimante : la Samsung ne répond qu'en v2c, quand Gladys ne
  parle que v1.
- **Chaque question est répétée** jusqu'à trois fois (2 s chacune) : la Samsung perd les premières questions qui la
  tirent de sa veille, puis répond en 15 ms. Sans répétition, elle passait pour « sans SNMP » une fois sur deux.
- **Une table lue à moitié n'est pas une mesure** : le parcours entier échoue, et la dernière table complète reste
  publiée. Sinon, une jauge disparaîtrait au premier datagramme perdu, puis réapparaîtrait.
- **Rythme** : relue à chaque relevé quand elle apporte des pièces ; toutes les 6 h quand elle n'apporte rien (une jet
  d'encre dont l'IPP dit tout) ; 30 min de répit après un silence. Le journal ne parle que quand le bilan change.

Une pièce sous le seuil (10 % par défaut) lève le **Problème**, comme une cartouche.

## Problème

Vrai quand :

1. l'imprimante est **arrêtée** (`printer-state` = 5 : « intervention nécessaire », RFC 8011) ;
2. elle envoie un motif en `-error`, ou une panne franche même sans suffixe (bourrage, plus de papier, capot ouvert,
   toner vide, bac absent…) ;
3. un consommable est **bas** : d'après elle (`toner-low`, tout motif en `-low`, `-almost-empty`, `-near-eol`,
   `-almost-full`, hors `-report`), ou d'après son niveau sous le seuil.

Pas d'alerte pour un motif sans suffixe hors de la liste des pannes (`paused`, `sleep`, mot-clé du fabricant) : la RFC
voudrait qu'on les tienne pour des erreurs, ce qui ferait d'une mise en veille une alerte. Une vraie panne absente de la
liste se voit quand même, parce que l'imprimante passe « arrêtée ». Les motifs en `-report` ne sont jamais montrés.

**Une imprimante éteinte n'est pas un problème** : après deux relevés ratés, l'appareil devient indisponible, sans
alerte. La carte garde le dernier relevé, avec l'état « Éteinte ou injoignable ».

## Carte « Imprimante »

Livrée par le plugin (`widgets/imprimante`, WIDGETS.md §13), choisie d'office pour l'entité Imprimante : une jauge en
arc par consommable (vert, orange sous 25 %, rouge sous le seuil), l'état, l'heure du dernier relevé, les alertes, et **Vérifier** qui relève tout de suite (commande `refresh`). La carte n'affiche rien de neuf
parce qu'elle a cliqué : le relevé arrive par l'état de l'entité.

## Réglages (page Services)

Découverte (oui), relevé toutes les 300 s (30 à 3600), seuil bas par défaut 10 %, adresses saisies, pièces en SNMP
(oui) et sa communauté (`public`). Pour un lancement
à la main : `appsettings.Local.json` (voir `appsettings.Local.example.json`).

## Tests

`tests/Krissvia.Plugin.Ipp.Tests`, vérifiés par mutation :

- **Trames** (`Trames/*.txt`, un octet par paire, commentaire après `#`) : écrites d'après les RFC par un script
  indépendant du codec, aux valeurs de la Samsung M2020 telles que Gladys les affiche — une imprimante prête (avec
  collection imbriquée, résolution, plage, date, booléen, groupe « unsupported ») et une imprimante en bourrage ; et
  la réponse Bonjour de la même imprimante, compression de noms comprise.
- **Captures réelles** (`Trames/capture-*`), sur la Samsung M2020 de l'utilisateur, avec son accord, en lecture seule
  (24/10/2026) : la réponse à la requête du plugin, la réponse complète (`all`, 94 attributs) et l'annonce mDNS.
  Elles ont appris ce que les trames écrites ne disaient pas : un seul consommable en IPP, un `printer-info` qui
  répète le modèle, une imprimante muette à la monodiffusion mDNS. Puis le SNMP (`capture-snmp-reponse-*`) : les
  cinq réponses au parcours du plugin, et une question écrite à part, par un script indépendant du codec.
- SNMP : codec, parcours contre un faux agent UDP qui rejoue les vraies réponses (v2c, repli v1, imprimante qui sort
  de veille, muette, coupée en plein parcours), règle de complément sur les vraies données, rythme des lectures.
- Codec : requête octet par octet, lecture complète, trames incohérentes refusées sans lecture hors des bornes.
- Relevé, diagnostic, découverte, puis le plugin contre de fausses imprimantes (éteinte, mémoire, doublons
  `_ipp`/`_ipps`, découverte coupée, Vérifier).
- De bout en bout : une fausse imprimante HTTP sur la boucle locale → client IPP → plugin → WebSocket → registre et
  états du cœur, et Vérifier envoyé par le bus de commandes ; elle note chaque opération reçue, et le test vérifie qu'il
  n'y a jamais eu que des lectures.
