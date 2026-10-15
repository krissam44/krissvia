# Contrat plugin ↔ cœur — proposition v0.2 (instable)

> Complète `CONTRACT-V0.md` (v0.1). Rien ici n'est figé : tant que la version commence par `0.`,
> le contrat peut changer sans préavis. Chaque point cite la raison qui l'a fait apparaître.

## 1. Session

| Changement | Raison |
|---|---|
| Nouveau message `welcome` (cœur → plugin) : version du contrat retenue, ou `rejected` avec un motif (version incompatible, jeton invalide, plugin déjà connecté) | `hello` n'avait pas de réponse : la négociation de version n'était pas réelle |
| Nouveau message `configure_result` (plugin → cœur) : `ok` ou erreur lisible (IP injoignable, broker refusé) | un plugin ne pouvait pas dire que sa configuration, valide au schéma, était inutilisable |
| À chaque (re)connexion, le plugin renvoie `declare` **puis tous ses états** | `state` n'envoie que les changements ; après un redémarrage du cœur, rien ne resynchronisait |
| Le cœur ignore (et journalise) un message dont le `type` est inconnu | un contrat 0.3 ajoutera des messages, un ancien destinataire ne doit pas échouer |

## 2. Commandes

- **Tout `command` reçoit un `command_result`**, pas seulement `safety`.
- Un ordre périmé (`ttl_seconds` dépassé) répond `failed`, `detail: "ttl"`. Le TTL court à partir de
  l'**émission** par le cœur.
- `accepted` : le plugin a transmis l'ordre et l'appareil l'a acquitté, ou le protocole n'a pas
  d'acquittement. `unreachable` : l'appareil n'a pas pu être joint.
- **Le résultat ne prouve pas l'effet.** Le plugin publie l'état résultant de l'entité (`state`) après
  une commande ; une coupure de sécurité est confirmée par cet état, ou par la chute de puissance.
- Commandes standard ajoutées (propositions, à valider avec des équipements réels) :
  `set_color_temp`, `set_color`, `activate_scene` (remplacent les payloads « bruts » que
  `PayloadCommande` laissait passer).
- Ordres de sirène : `priority: "safety"`, mêmes règles que le délestage.

## 3. Entités

| Changement | Raison |
|---|---|
| `expected_interval_seconds` (facultatif) sur l'entité | le cœur juge la fraîcheur d'une mesure sans seuil réglé à la main (Linky : 2) |
| `availability` peut viser une entité (`entity_unique_id`) | la convention « entité sans valeur récente ⇒ `availability` à `false` » n'avait pas de message pour une seule entité |
| Classe `apparent_power`, unité `VA` | le délestage et l'abonnement sont en VA (trame Linky `SINSTS`) |
| Un `sensor` peut porter une **chaîne** quand `device_class` vaut `enum` (liste `options` déclarée) | libellé et numéro de tarif Linky |
| États structurés (prévision météo) | le thermostat consomme la prévision horaire. **Option recommandée** : classe `forecast`, valeur = tableau `[{time, temperature}]` ; à valider avec le plugin météo |
| La batterie est publiée **en pourcentage** | la conversion des crans RFXCom (0-9) se fait dans le plugin, jamais d'après la valeur reçue (incident du 2 % affiché 22 %) |
| Deux entités sont unifiées par leur `unique_id` : un changement de `kind` sur un `unique_id` existant est refusé par le cœur | l'`entity_id` commence par la famille et ne change jamais |

## 4. Validation

- Publication d'un JSON Schema par message pour les SDK non C# (point ouvert de la v0.1).
- Kit de conformité exécutable par un plugin contre un cœur simulé : hello/welcome, resynchronisation,
  commande avec TTL, message inconnu, `declare` incomplet.

## 5. Points ouverts

- ~~Transport retenu et délais du heartbeat~~ : **tranché le 15/10/2026**, WebSocket + JSON, ping 15 s,
  mort à 45 s — voir `TRANSPORT.md`. Implémentés au passage (contrat `0.2`) : `welcome` / `rejected`,
  `configure_result`, `availability` par entité, messages de type inconnu ignorés. Écart avec §1 : un
  second `hello` du même plugin **remplace** la session au lieu d'être refusé (raison dans `TRANSPORT.md`).
- Plugin à deux sens pour openHASP : entités d'un côté, écran piloté de l'autre. Entité d'affichage
  ou service sortant ?
- Auto-extinction côté appareil (`lease`) pour les radiateurs et chauffe-eau, via `args`.
