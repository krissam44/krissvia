# Notifications et alertes

> Livré le 19/10/2026. Code : `Krissvia.Core.Notifications` (envoi, alertes), `Krissvia.Climate.Alerts` (alerte gel),
> `AlertLoop` (analyse toutes les 15 min), page **Notifications** (`/parametres/notifications`).

## 1. Notifications : ntfy

Comme DomoMud : un message part sur le téléphone par [ntfy](https://ntfy.sh), serveur public ou serveur à soi. Le
téléphone a l'application ntfy, abonnée au même **sujet**.

- Réglages : serveur, sujet, jeton facultatif (`Authorization: Bearer`), activé ou non. Collection `notifications`,
  document `ntfy` ; le jeton y est en clair, comme les réglages des plugins (base en mode 600).
- Sur le serveur public, quiconque connaît le sujet le lit et peut y écrire : un sujet difficile à deviner, ou un
  serveur à soi protégé par un jeton.
- Envoi en **JSON** (`POST` à la racine du serveur : `topic`, `title`, `message`, `priority`, `tags`), avec une longueur
  connue (`Content-Length`). Écart voulu avec DomoMud, qui passait le titre en en-tête HTTP et devait en retirer les
  accents.
- « Envoyer un essai » éprouve le formulaire **tel qu'il est**, avant de l'enregistrer et même désactivé.
- Un envoi ne lève jamais d'exception : le résultat dit `Sent`, `Disabled`, `NotConfigured` ou `Failed` (avec la cause,
  dans la langue de la machine). Délai : 10 s.

## 2. Alertes

`AlertService` reprend l'aiguillage de DomoMud :

| Règle | Raison |
|---|---|
| Une alerte par incident (clé) : tant qu'il dure, rien n'est renvoyé | pas de rafale à chaque analyse |
| La fin d'un incident ne se dit que s'il avait été signalé | pas de « tout va bien » surprise |
| Quarantaine après un retour à la normale | une valeur à la limite du seuil noyait le téléphone (quatre allers-retours en 90 min dans DomoMud) |
| Un envoi **échoué** ne compte pas comme signalé : la prochaine analyse réessaie | écart voulu avec DomoMud, qui perdait l'alerte si ntfy était injoignable à ce moment-là |
| Notifications désactivées : l'incident est noté (la page le montre) sans être renvoyé ensuite | activer les notifications ne déclenche pas une rafale d'anciens incidents |
| État en mémoire : après un redémarrage, une situation toujours dégradée est signalée une fois | voulu |

Réglages : collection `alertes`, document `reglages`. La page liste les alertes en cours.

## 3. Alerte gel

Pour **chaque lieu** du plugin Météo (entité `weather`) : le minimum prévu de l'heure en cours à l'horizon (24 h par
défaut, 1 à 48 h) ; au seuil (1 °C par défaut) ou en dessous, « Risque de gel à Vertou », priorité 4, ❄️ ; quand le
minimum remonte, « Risque de gel écarté à Vertou ». Une alerte par lieu : l'appartement inoccupé et la maison ne
gèlent pas les mêmes nuits. Quarantaine d'un horizon complet.

Une prévision indisponible ou plus vieille que 3 h ne conclut **rien** : ni alerte sur une donnée morte, ni « gel
écarté » faute de données.

## 4. Pour la suite

Sonde muette, pile faible, plugin arrêté, présence inconnue, connexion Internet coupée et espace disque : livrés le
22/10/2026, voir `SUPERVISION.md` (contrôle toutes les minutes, `SupervisionLoop`). Reste le délestage.
