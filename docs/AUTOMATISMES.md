# Automatismes

> Livré le 19/10/2026. Module du cœur `Krissvia.Climate.Automations` (`Automation`, `AutomationRules`,
> `AutomationEngine`, `AutomationService`), boucle `AutomationLoop`, écrans `/automatismes`, API `/api/automations`.

Un automatisme se lit **Quand… Si… Alors…** : un déclencheur, des conditions (toutes vraies), des actions exécutées dans
l'ordre. C'est le moteur de règles déclaratif décidé pour DomoMud le 25/07/2026 ; l'échappatoire pour ce qu'il ne sait pas
faire reste Node-RED, par l'API REST.

## 1. Déclencheurs

| Type | Part quand | Règles |
|---|---|---|
| `button` | une entité `event` publie une action (« single », « double »…) ; action vide = toutes | Un appui de plus de 2 min (plugin qui rejoue ses états à la reconnexion) et un simple changement de disponibilité ne comptent pas. Deux « single » de suite sont deux appuis. |
| `time` | à l'heure locale choisie, sur les jours cochés (aucun = tous) | Une seule fois par minute ; **pas de rattrapage** si le serveur était arrêté à l'heure dite. |
| `state_change` | un tout-ou-rien (`binary_sensor`, `switch`, `light`) devient actif, repasse au repos, ou les deux | Sur **front** seulement : un état précédent inconnu (premier état après un redémarrage) ne déclenche pas, sinon chaque redémarrage rallumerait les lumières ; un radar qui réaffirme sa détection non plus. |

« Actif » : `true` (allumé, ouvert, détecté), ou `{"on": true}` pour une lumière.

## 2. Conditions

`home_occupied` / `home_empty` (l'absence du foyer : forçage, période programmée, détection — PRESENCE.md §5),
`time_range` (fin avant début = la nuit, comme les plannings), `thermostat_mode` (mode **effectif**, absence comprise),
`entity_active`, `above` / `below` (seuil strict sur une mesure). Chacune peut être **inversée**.

Une donnée inconnue (mesure absente, thermostat supprimé) rend la condition **non satisfaite, inversée ou non** : mieux vaut
ne rien faire que d'agir sur une supposition.

## 3. Actions

`turn_on`, `turn_off`, `toggle` (switch ou lumière), `thermostat_mode` (même chemin que le bouton de la carte : le planning
est suspendu), `notify` (ntfy, à chaque déclenchement : ce n'est pas une alerte dédoublonnée). Chacune peut attendre avant
de s'exécuter (`delay_seconds`, 24 h au plus) : « allumer, puis éteindre 5 min après ».

- Les ordres passent par le bus de commandes, signés `automatisme:{id}` : une entité inconnue ou une commande qu'elle ne
  déclare pas échoue bruyamment.
- `toggle` utilise la commande `toggle` si l'entité la déclare (l'appareil bascule lui-même), sinon le contraire de son
  état publié ; état inconnu : l'action échoue au lieu de deviner.
- Une action qui échoue (ntfy non configuré, prise injoignable) **n'arrête pas les suivantes** ; l'exécution est notée
  « en échec » avec la raison.
- Une exécution tourne à part : un délai de 10 min ne bloque ni le bus ni les autres automatismes. Une action retardée en
  attente à l'arrêt du serveur est perdue (pas de rattrapage, comme les horaires).
- Plus de 10 déclenchements en une minute pour un même automatisme : on s'arrête (« arrêté (boucle ?) »). C'est le cas
  d'un automatisme sur « la lampe s'allume » qui bascule cette même lampe.

## 4. Vérifications à l'enregistrement

Chaque référence est vérifiée au registre **avec sa famille** : un déclencheur bouton vise un `event`, un changement d'état
un tout-ou-rien, une action un `switch` ou une `light`, une condition de seuil un `sensor` ; un thermostat doit exister.
Un équipement renommé ne laisse donc pas un automatisme agir dans le vide en se croyant en règle.

## 5. Écrans et API

- `/automatismes` : une carte par automatisme (« Quand… Si… Alors… »), sa dernière exécution, l'interrupteur Actif, le
  bouton **Exécuter** (conditions évaluées comme pour un vrai déclenchement), et le journal des 20 dernières exécutions.
- Le formulaire est une **fenêtre** (« Nouveau », ou la roue d'une carte ; 23/10/2026), à trois onglets comme dans
  DomoMud : **Déclencheur**, **Conditions**, **Commandes**. Chaque onglet commence par sa phrase (« S'exécute si entre
  22:00 et 06:00 ») ; « Créer » reste grisé tant que le nom ou l'équipement déclencheur manque, et une erreur du service
  ramène sur l'onglet concerné. Pour un bouton, l'aide affiche la dernière action reçue : appuyer une fois donne le mot
  à recopier. (L'ancienne page `/automatismes/{id}` n'existe plus.)
- API : `GET /api/automations`, `GET|PUT|DELETE /api/automations/{id}`, `POST /api/automations/{id}/run`,
  `GET /api/automations/journal`. Format : celui de la base (snake_case, énumérations en texte).

```json
{
  "name": "Radar de nuit",
  "trigger": { "type": "state_change", "entity_id": "binary_sensor.radar_salon_occupancy", "transition": "activated" },
  "conditions": [ { "type": "time_range", "start": "22:00:00", "end": "06:00:00" } ],
  "actions": [
    { "type": "turn_on", "entity_id": "switch.eclairage_salon" },
    { "type": "turn_off", "entity_id": "switch.eclairage_salon", "delay_seconds": 300 }
  ]
}
```

## 6. Pas encore

Déclencheurs sur seuil (température qui passe sous 18 °C), sur présence ou sur couleur Tempo ; actions « activer/couper
une programmation » ; bibliothèque d'automatismes prêts à l'emploi. Le journal des exécutions vit en mémoire (50 dernières).
