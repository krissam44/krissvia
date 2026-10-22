# Auto-TPI

> Livré le 23/10/2026. `ThermalModel` (identification et réglage, fonctions pures), `ThermalIntervals` (découpage de
> l'historique), `AutoTpiService` (calcul, application, retour arrière), boucle `AutoTpiLoop`, panneau « Auto-TPI »
> dans l'onglet Régulation d'un thermostat. S'appuie sur l'historique (HISTORIQUE.md).

## 1. Le principe

Au lieu de corriger Kp et Ki à tâtons, on **identifie la pièce** sur son historique, puis le réglage en découle.

Le modèle : `dT/dt = g·u − k·(T − Text) + c`

| Terme | Sens | Exemple |
|---|---|---|
| `g` | gain de chauffe : °C gagnés par heure, radiateur à fond | 2 °C/h |
| `u` | part du temps où le radiateur chauffe (0 à 1) | |
| `k` | déperditions, par heure ; constante de temps `τ = 1/k` | 0,05/h, τ = 20 h |
| `c` | apports gratuits (soleil, occupants, voisins), °C/h | 0,15 |

Le modèle est linéaire en (g, k, c) : une régression aux moindres carrés sur des intervalles de 30 min suffit. Puis,
le TPI calculant `Kp·(cible − T) + Ki·(cible − Text) + Kint·∫erreur` :

- **Ki = k/g**, exactement : à l'équilibre, la chauffe doit compenser les pertes, `g·u = k·(cible − Text)`. Avec ce
  Ki, il ne reste plus de biais permanent pour l'intégrale.
- **Kp = 1/(g·λ)** (réglage IMC), λ étant le temps de réponse demandé : **1 h** en électrique, **2 h** pour des
  radiateurs à eau, **4 h** pour un plancher chauffant. Le type de chauffage se règle par thermostat.
- **Kint = Kp·k** (temps d'intégration = constante de temps de la pièce) : modéré, puisque Ki fait l'essentiel.
- Bornes (celles de DomoMud, C10) : Kp de 0,2 à 1,5, Ki de 0 à 0,10, Kint de 0 à 0,10. Un réglage ramené dans les
  bornes le dit (« le radiateur semble sous-dimensionné » quand Ki dépasserait 0,10).

**Pourquoi pas l'auto-TPI de DomoMud** (C10 : Kp et Ki poussés par petits pas selon l'erreur moyenne) : le TPI de
Krissvia a une intégrale, qui corrige déjà ce même biais. Deux correcteurs du même biais se battent.

## 2. Ce qu'il faut pour proposer un réglage

- Une **sonde extérieure** sur le thermostat : sans elle, les déperditions ne se mesurent pas.
- **24 h d'historique** au moins (48 intervalles exploitables) ; le calcul regarde les 14 derniers jours. Un intervalle
  où la sonde, l'extérieur ou la chauffe sont inconnus plus de 10 % du temps est écarté.
- Une chauffe qui a **varié** (sinon g et les apports ne se séparent pas), un écart intérieur − extérieur qui a varié
  d'au moins 2 °C (sinon k et les apports ne se séparent pas).
- Des valeurs **physiques** : g et k positifs. Un gain négatif fait penser à un radiateur suivi qui n'est pas celui de
  la pièce ; des déperditions négatives, à une sonde extérieure au soleil.
- Un **R² d'au moins 0,5** : en dessous, sonde bruitée, fenêtres souvent ouvertes, ou autre chauffage dans la pièce.

Chaque refus est dit en clair dans le panneau.

## 3. Proposé, jamais appliqué seul

Choix de l'utilisateur (22/10/2026) : **opt-in par thermostat, et rien ne change sans « Appliquer »**.

- Activer l'auto-TPI (onglet Régulation) lance un premier calcul ; ensuite, un calcul par jour environ (`AutoTpiLoop`,
  toutes les heures, pour les thermostats dont le dernier calcul a plus de 20 h). « Calculer maintenant » le refait.
- Le panneau montre la pièce identifiée (gain, demi-vie de l'écart avec l'extérieur, apports, R²) et le réglage
  proposé à côté de celui en place.
- **Appliquer** met en place Kp, Ki et Kint proposés ; le reste des réglages ne bouge pas. Le réglage remplacé est
  gardé : **Revenir à…** le remet. Appliquer plusieurs jours de suite garde toujours le réglage **d'origine** comme
  point de retour.
- Les comptes rendus vivent dans leur propre collection (`thermostat_autotpi`), comme les pentes du préchauffage : un
  calcul n'écrit jamais dans les réglages.

## 4. Pas encore

- Le préchauffage apprend encore sa pente à part (`Preheat`) : le modèle identifié donnerait la même chose, corrigée
  de l'extérieur, et pourrait la remplacer.
- Une estimation des kWh par radiateur (g et la puissance déclarée du radiateur).
