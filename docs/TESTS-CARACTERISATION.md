# Tests de caractérisation — ce que DomoMud sait déjà

> Chaque ligne est un comportement observé dans le code de DomoMud, à figer par un test **avant** de le
> porter dans Krissvia. Colonne « Décision » : **conserver** (le test fige le comportement),
> **corriger** (le test décrit le comportement voulu et échoue tant que Krissvia ne le respecte pas),
> **à décider**.
>
> Lecture faite du code, pas d'exécution : les cas marqués ✱ sont des constats à vérifier en les
> écrivant. Installations de référence : 6 kVA (alerte 80 % = 4 800 VA, restauration 70 % = 4 200 VA)
> et 12 kVA (alerte 80 % = 9 600 VA, restauration 70 % = 8 400 VA ; hypothèse : mêmes pourcentages que le 6 kVA).

## Délestage

| # | Comportement | Décision |
|---|---|---|
| D1 | Au-dessus du seuil d'alerte : délestage par priorité croissante (1 en premier) | conserver |
| D2 | Sous le seuil de restauration : restauration par priorité décroissante, après le délai propre à chaque équipement | conserver |
| D3 | Entre les deux seuils : rien ne bouge (hystérésis) | conserver |
| D4 | ✱ L'excédent est calculé avant que la mesure ait baissé : deux ticks successifs coupent deux équipements pour un seul besoin | corriger (décidé) : attendre une mesure postérieure à l'effet. Test `D4_…` |
| D5 | ✱ Plusieurs équipements dont le délai est écoulé reviennent dans le même tick, sans vérifier la marge restante | corriger (décidé) : un seul rétablissement à la fois, avec marge sous le seuil d'alerte. Tests `D5_…` |
| D6 | ✱ Une mesure absente est lue comme 0 W, donc restauration de tout | corriger (décidé) : ne rien décider, signaler `SourceUnavailable`. Test `D6_…` |
| D7 | ✱ Une coupure qui échoue est comptée comme réussie (équipement marqué délesté, économie comptée) | corriger |
| D8 | ✱ Un équipement retiré ou désactivé pendant un délestage n'est pas rétabli : son thermostat reste inhibé | corriger |
| D9 | Délesteur désactivé : tous les équipements délestés sont rétablis | conserver |
| D10 | Notification seulement sur les bascules 0→N et N→0, jamais à chaque tick | conserver |
| D11 | Équipement lié à un thermostat : coupure immédiate par inhibition (voir ARCHITECTURE §8), sans toucher à la consigne | corriger (DomoMud passait par le tick de 10 s) |
| D12 | Source « somme des équipements » : mesure réelle si publiée, sinon puissance nominale, seulement pour les types qui en ont une | conserver ; écart voulu : aucun état connu ⇒ source indisponible, pas 0 W (cohérent avec D6). Tests `D12_…` |

## Climate (thermostats)

| # | Comportement | Décision |
|---|---|---|
| C1 | Un thermostat sans état dans le moteur lors d'un délestage : erreur journalisée, aucune coupure | corriger : inhibition prise sur l'objet, pas sur une copie |
| C2 | Failsafe sonde : sonde sans donnée depuis `failsafe_minutes` ⇒ coupure inconditionnelle | conserver |
| C3 | Fenêtre ouverte : coupure après `delai_ouverture`, reprise après `delai_fermeture` | conserver |
| C4 | ✱ Mode Off et coasting ne coupent que si le moteur croit que le radiateur chauffe | corriger : couper sans condition |
| C5 | ✱ Les drapeaux « coupure envoyée » sont posés même si l'ordre échoue | corriger : seulement après `accepted`, retenter sinon |
| C6 | ✱ Dictionnaire d'états modifié par la configuration pendant que d'autres services le lisent | corriger : structure concurrente |
| C7 | Après reprise d'un délestage, l'intégrale du TPI n'est pas remise à zéro | conserver |
| C8 | Une période d'absence programmée s'efface après le retour ; une présence détectée l'annule seulement si le départ a été constaté | conserver |
| C9 | Hiérarchie : forçage manuel > période programmée > détection | conserver |
| C10 | Auto-TPI : pas de correction avant 6 cycles, cycles saturés ignorés, bornes Ki ∈ [0 ; 0,10], Kp ∈ [0,2 ; 1,5] | conserver |
| C11 | Les créneaux de planning qui passent minuit (fin < début) sont actifs de début à minuit puis de minuit à fin | conserver |
| C12 | Chevauchement de créneaux : le plus matinal gagne, avertissement à la saisie | conserver |

## Alarme

| # | Comportement | Décision |
|---|---|---|
| A1 | Armement refusé si un capteur actif est ouvert ou muet (jamais entendu = défaut) | conserver |
| A2 | Contrôle des défauts au début **et** à la fin du décompte de sortie | conserver |
| A3 | Un détecteur de mouvement ne bloque pas l'armement, mais son silence reste un défaut | conserver |
| A4 | Capteur temporisé : décompte d'entrée ; capteur immédiat : déclenchement direct | conserver |
| A5 | Pendant les décomptes de sortie et d'entrée, les détections sont ignorées | conserver |
| A6 | Désarmement : sirène coupée sans condition | conserver |
| A7 | ✱ Durée de sirène 0 : la sirène ne s'arrête qu'au désarmement | corriger : durée bornée de 10 à 30 s, `nombre_sonneries`, `pause_entre_sonneries` (voir ARCHITECTURE §9) |
| A8 | L'état `Declenchee` persiste après l'arrêt de la sirène, jusqu'au désarmement | conserver |
| A9 | Code : 4 chiffres minimum ; trois échecs ⇒ verrou 60 s, compteur global ; un succès efface tout ; comparaison en temps constant | conserver |
| A10 | Identifiants de capteurs et de codes : casse canonique (incident du 08/10/2026) | conserver : identifiants en minuscules |
| A11 | Une écriture de journal qui échoue ne bloque jamais la sirène | conserver |

## Alertes, présence, énergie

| # | Comportement | Décision |
|---|---|---|
| L1 | Une alerte par incident ; retour à la normale seulement si l'alerte était active | conserver |
| L2 | Quarantaine après un retour à la normale (un capteur à la limite ne clignote pas) | conserver |
| L3 | Capteur silencieux : seulement pour les types qui émettent régulièrement | conserver, remplacé par `expected_interval_seconds` |
| L4 | Répit de 10 min au démarrage avant la première analyse | conserver |
| L5 | Alerte batterie : hystérésis de 5 points ; aucune alerte pour un équipement sur secteur | conserver |
| L6 | Batterie : conversion décidée par la technologie, jamais d'après la valeur | conserver (dans le plugin) |
| L7 | Détection de présence hors service après 3 relevés en échec ⇒ alerte, maison considérée occupée | conserver |
| L8 | Couleur Tempo inconnue ⇒ condition « jour rouge » non satisfaite | conserver |
| L9 | Premier message d'un équipement ⇒ jamais de front (un redémarrage ne rallume pas les lumières) | conserver |
| L10 | Groupe de sondes : moyenne des sondes fraîches (< 60 min) ; aucune fraîche ⇒ valeur indisponible, **entité non supprimée** | corriger |
| L11 | Un état restauré au démarrage garde son horodatage d'origine | conserver |

## Utilitaires purs à porter tels quels

`CouleurOpenHasp` (contraste), `LuminositeOpenHasp`, `EchelleGraphe`, `RaspberryProc` (analyse de
`/proc`), `PlanningValidation`, `ProchaineBascule`, `ControleDefauts`, `VerrouAlarme`, `CodeAlarme`,
`MachineAlarme` (avec la modification de la sirène).
