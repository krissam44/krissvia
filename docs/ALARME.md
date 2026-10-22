# Alarme

> Livré le 22/10/2026. Module du cœur `Krissvia.Climate.Alarm` (`AlarmMachine`, `AlarmFaults`, `AlarmCodes`,
> `AlarmLockout`, `AlarmService`, `AlarmEngine`), boucle `AlarmLoop`, page `/alarme`, API `/api/alarm`, widget
> standard `alarme`.
> Portage de la centrale de DomoMud, avec les sonneries de `ARCHITECTURE.md` §9.

## 1. États

| État | Valeur de l'entité (`state`) | Ce qui se passe |
|---|---|---|
| Désarmée | `disarmed` | rien n'est surveillé |
| Sortie | `exit_delay` | décompte avant l'armement : on traverse la maison pour sortir, les détections sont normales |
| Armée | `armed` | un capteur **temporisé** ouvre l'entrée, un capteur **immédiat** déclenche |
| Entrée | `entry_delay` | décompte pour désarmer ; tout ce qui bouge est toléré jusqu'à l'expiration |
| Déclenchée | `triggered` | sonneries, notification ; l'état reste jusqu'au désarmement, sonneries finies |

L'état est publié par le cœur dans l'entité `sensor.maison_alarme` (classe `alarm`, valeur objet :
`state`, `label` — le libellé des digicodes DomoMud, `EXIT_DELAY:12` —, `countdown`, `ringing`, `sensor`,
`triggered_at`, `faults`). Elle se lit comme les autres : tableau de bord, automatismes, API des entités. Ses deux
commandes, `arm` et `disarm`, exigent un code dans les arguments (`{"code": "1234"}`) : voir §6.

## 2. Capteurs

Tout `binary_sensor` de classe `opening` (contact, `true` = ouvert) ou `occupancy` (détecteur, `true` = détection).
Un rôle par capteur :

- **Immédiat** : déclenche aussitôt.
- **Temporisé** : ouvre la temporisation d'entrée. La porte par laquelle on rentre — **jamais une fenêtre**, ce serait
  un cadeau au visiteur.
- **Contrôle seul** : vérifié à l'armement, ne déclenche jamais.

Règles de DomoMud, conservées :

- **Seuls les fronts comptent** (repos → actif, les deux valeurs connues). Un détecteur qui réaffirme sa détection ne
  redéclenche pas ; le premier message après un redémarrage, ou après une entité restaurée encore indisponible, n'est
  pas un front — sinon chaque redémarrage déclencherait.
- **Double contrôle des défauts** : à la demande d'armement ET à la fin du décompte de sortie. On sort par la porte
  surveillée : ne contrôler qu'au départ laisserait armer porte ouverte, abandonner dès qu'elle bouge empêcherait de
  sortir. Nouveauté : un armement abandonné en fin de décompte est **notifié** — on est parti en croyant la maison
  protégée.
- **Un capteur muet est un défaut** (12 h par défaut, réglable, 0 désactive) : un contact Zigbee ne publie qu'au
  changement, et « on ne sait pas » n'est pas « fermé ». Un capteur sans valeur (jamais entendu, indisponible) aussi.
- **Un détecteur actif n'est pas un défaut** : il vous voit taper votre code. Son silence, si.

## 3. Sirènes et sonneries

Les sirènes sont des entités qu'on allume et éteint (`turn_on` / `turn_off`, priorité `safety`). Au déclenchement :
une sonnerie de 10 à 30 s, une pause de 5 à 120 s, et ainsi de suite jusqu'au nombre de sonneries (1 à 10) ; puis
silence, l'état restant « déclenchée ». Pas de « 0 = illimité ».

- **Le désarmement coupe les sirènes sans condition**, même si la centrale les croit silencieuses : l'état interne
  n'est qu'une croyance, comme pour la coupure garantie des thermostats.
- Les ordres partent hors du verrou de la centrale, dans l'ordre : une sirène lente ne fige pas le décompte, et un
  arrêt ne double jamais une mise en marche. Un ordre refusé ou injoignable va au journal.

**Limite connue** : un serveur qui meurt pendant une sonnerie laisse la sirène dans l'état où elle était ; au
redémarrage, la centrale reprend « déclenchée, sonneries finies ». Préférer une sirène qui borne elle-même sa durée
(la plupart des sirènes Zigbee le font).

## 4. Redémarrage

L'état est enregistré (collection `alarme`, document `etat`) : un redémarrage ne désarme pas en silence. Les
décomptes ne sont pas repris : une sortie interrompue redevient **désarmée** (l'armement n'avait pas abouti), une
entrée en cours redevient **armée** (elle l'était). Le déclenchement survit : c'est le souvenir de l'intrusion.

## 5. Codes

- Pour l'API et les claviers logiciels. Enregistrés en **PBKDF2** (sel, 100 000 itérations), jamais en clair ; deux
  personnes ne peuvent pas avoir le même code (le journal ne saurait plus qui a désarmé). 4 à 12 chiffres.
- **Trois codes faux bloquent 60 s**, compteur global (avant vérification on ne sait pas qui tape), remis à zéro au
  blocage. C'est lui la vraie protection : dix mille codes à quatre chiffres, aucun hachage ne les rend impraticables.
- La page `/alarme` arme et désarme **sans code** pour un compte connecté : c'est l'échappatoire d'un clavier bloqué.

## 6. API

| Méthode | Route | Corps | Réponse |
|---|---|---|---|
| GET | `/api/alarm` | | `enabled`, `state` (la valeur de l'entité), `faults_now`, `locked_seconds` |
| POST | `/api/alarm/arm` | `{"code": "1234"}` | **200** `{user, state}` ; **409** armement refusé, avec `faults` |
| POST | `/api/alarm/disarm` | `{"code": "1234"}` | **200** `{user, state}` |
| GET | `/api/alarm/journal` | | les 200 dernières lignes, la plus récente d'abord |
| GET / PUT | `/api/alarm/settings` | réglages | les codes ne se donnent que dans la page |

Code faux : **403** (et non 401 : la clé d'API est bonne). Clavier bloqué : **429** avec `Retry-After`. Alarme
désactivée : **409**. La clé d'API ne suffit pas à désarmer : un script qui la connaît ne doit pas devenir la porte
dérobée du digicode. Le code voyage dans le corps, jamais dans l'URL.

**Par l'entité** (`POST /api/entities/sensor.maison_alarme/commands`, `{"command": "arm", "args": {"code": "1234"}}`) :
c'est le chemin de la carte du tableau de bord. Mêmes règles — un code faux compte pour le même blocage — et la raison
d'un refus revient dans `detail` (« code incorrect », « trop de codes faux : clavier bloqué 42 s », « armement
refusé : Porte d'entrée »). Le bus de commandes n'écrit les arguments nulle part (journal du serveur, route
`/debug/commands`) : c'est ce qui permet d'y faire passer un code, et cela doit le rester.

**La carte `alarme`** : badge d'état, décompte de sortie ou d'entrée en grand, capteur en cause et heure d'un
déclenchement, dernier refus d'armement ; dessous un clavier de 0 à 9 et le bouton **Armer** ou **Désarmer** selon
l'état. Le code n'est jamais affiché (des points), s'efface à l'envoi et après 30 s d'inactivité. Les touches
réagissent à l'appui (`pointerdown`) et ne redessinent que les points : la carte se redessine à chaque seconde d'un
décompte, et un clic à cheval sur un redessin serait perdu — précisément au moment où l'on tape son code.

Réglages (`PUT /api/alarm/settings`) :

```json
{ "enabled": true, "exit_delay_seconds": 30, "entry_delay_seconds": 20,
  "ring_seconds": 30, "ring_count": 3, "pause_seconds": 30, "silence_minutes": 720,
  "sensors": [ { "entity_id": "binary_sensor.porte_d_entree", "role": "delayed" },
               { "entity_id": "binary_sensor.fenetre_chambre", "role": "immediate" } ],
  "sirens": [ "switch.sirene_interieure" ], "notify_arming": false, "notify_trigger": true }
```

Rôles : `immediate`, `delayed`, `fault_only`. Activer l'alarme sans capteur est refusé.

## 7. Ce qui reste

- **Digicode ESPHome** (l'écran de l'appartement sous DomoMud, qui parle `domomud/alarme/*`) : pas repris. Le
  contrat 0.3 a fourni ce qui manquait (suivre l'entité, commander avec un code), et c'est la **page 4 des platines
  openHASP** qui fait digicode (`plugins/Krissvia.Plugin.OpenHasp/README.md`). Le code y passe comme dans la carte du
  tableau de bord : dans les arguments de `arm` / `disarm`, donc mêmes codes, même blocage, même journal.
