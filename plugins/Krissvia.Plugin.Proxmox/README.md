# Plugin Proxmox

Plugin C# : les machines virtuelles (`qemu`) et les conteneurs (`lxc`) d'un serveur Proxmox VE, par son API REST et un
jeton d'API. Portage du `ProxmoxService` de DomoMud.

## Jeton

Datacenter → Permissions → **API Tokens** → Add : utilisateur `root@pam` (ou un compte dédié), nom `krissvia`. Le
secret ne s'affiche qu'une fois.

- **Décocher « Privilege Separation »**, ou donner au jeton lui-même les rôles **PVEAuditor** (lire) et **PVEVMUser**
  (démarrer, arrêter) sur `/` (Datacenter → Permissions → Add → API Token Permission). Sans droits, Proxmox répond une
  liste VIDE, pas une erreur : le plugin le dit au journal, avec ce remède.
- En-tête envoyé : `Authorization: PVEAPIToken=root@pam!krissvia=<secret>` — sans espace après `PVEAPIToken`.
- Certificat auto-signé accepté par défaut (« Vérifier le certificat » décoché) : c'est celui de tout nœud Proxmox.

## Entités

| Appareil | Entité | Classe |
|---|---|---|
| chaque machine | Machine (objet : état, processeur, mémoire, disque, durée, surveillance, relance) — commandes `start`, `shutdown` (arrêt propre), `reboot`, `set_tracked`, `set_auto_restart` | `virtual_machine` |
| | Arrêt inattendu | `problem` (binary_sensor) |
| chaque nœud | Nœud (objet : en ligne, processeur et cœurs, mémoire et disque système utilisé / total, durée, machines en marche) | `hypervisor_node` |
| | Hors ligne | `problem` |
| | Processeur, Mémoire, Disque système | % (pour l'historique) |

Les modèles (`template`) sont ignorés. Une VM sans agent invité ne remonte pas son disque utilisé : la carte montre
alors la taille allouée (« 64 Go alloués »), comme DomoMud ; un conteneur montre la barre et « utilisé / total ».
Un nœud hors ligne n'a pas de pourcentages (nuls, pas zéro).

## Arrêt inattendu et relance automatique

- Seul un passage **en marche → arrêtée** d'une machine **surveillée** est un arrêt inattendu (DomoMud). Une machine
  déjà arrêtée au premier relevé (une machine de réserve) n'en est pas un, ni un arrêt demandé depuis Krissvia.
- L'arrêt inattendu est une entité `problem` : la supervision du cœur envoie « Problème : domomud Arrêt inattendu »,
  puis « Problème résolu » quand la machine repart.
- **Relance automatique** (case sur la carte) : la machine est redémarrée aussitôt, sans alerte. Garde-fou de DomoMud :
  au-delà de **3 relances en 30 min**, c'est un vrai plantage — le plugin cesse et l'arrêt inattendu est levé. Une
  relance qui ne prend pas (toujours arrêtée au relevé suivant) ou qui ne peut pas partir est levée aussi.
- « Surveillée » et « Relance auto » se règlent depuis la carte ; le plugin les garde dans son dossier de données
  (`preferences.json`). Par défaut : surveillée, sans relance.

## Cartes

Livrées par le plugin (WIDGETS.md §13), choisies d'office :

- `widgets/proxmox-vm`, pour l'entité Machine : état, barres, disque, Démarrer / Arrêter / Redémarrer (un arrêt ou un
  redémarrage demande confirmation), et les deux cases.
- `widgets/proxmox-noeud` (23/10/2026), pour l'entité Nœud : l'en-tête de la page Proxmox de DomoMud — trois tuiles
  Processeur / Mémoire / Disque système avec pourcentage, jauge et détail, durée de fonctionnement et machines en
  marche. Pleine largeur, 1,5 ligne par défaut.

## Réglages (page Services)

Adresse, port (8006), identifiant et secret du jeton, vérification du certificat, relevé toutes les 30 s. Trois relevés
ratés : tout devient indisponible.

## Tests

`tests/Krissvia.Plugin.Proxmox.Tests` : lecture de `cluster/resources` (modèles écartés, réponse vide = pas de droits),
forme de l'en-tête et des URL, puis le plugin contre une fausse API — arrêt inattendu seulement sur transition, arrêt
demandé, machine non surveillée, relance et son garde-fou, relance sans effet ou impossible, préférences conservées,
déclaration, indisponibilité.
