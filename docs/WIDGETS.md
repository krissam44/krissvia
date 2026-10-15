# Contrat des widgets (version 1, proposition)

> Statut : **proposition à valider** avant tout code (tableau de bord, widgets standards). Rien ici n'est
> implémenté. Complète ARCHITECTURE.md §10.

Un widget affiche une ou plusieurs entités et peut leur envoyer des ordres. L'utilisateur peut en écrire, en
Web Component ou en Blazor, et choisir celui qu'utilise chaque entité ou chaque carte du tableau de bord.

## 1. Principes

1. **Un seul contrat, pour tout le monde.** Les widgets standards de Krissvia n'ont aucun accès privilégié : ils
   reçoivent les mêmes données et passent par la même API qu'un widget tiers. Si le contrat suffit pour eux, il
   suffit pour les autres — et il sera éprouvé dès le premier jour.
2. **Un widget ne parle qu'à l'API publique** (lecture, ordres, flux en direct). Jamais aux services du serveur :
   c'est ce qui permettra de le réutiliser tel quel dans une PWA ou une appli mobile.
3. **Un widget qui échoue ne casse pas le tableau de bord.** Script introuvable, exception, élément jamais défini :
   la carte affiche le widget standard de l'entité et un bandeau d'erreur. Comme un plugin qui plante ne fait pas
   tomber le cœur.
4. **Une absence de donnée n'est pas zéro** (principe 5 de l'architecture) : un widget reçoit explicitement
   « indisponible » ou « jamais reçu », et doit le montrer.

## 2. Deux façons d'écrire un widget

### 2.1 Web Component (format de référence)

Un dossier, sans recompilation de Krissvia :

```
widgets/
  jauge-cuve/
    widget.json        le manifeste (§3)
    jauge-cuve.js      définit l'élément <kv-jauge-cuve>
```

N'importe quelle technologie qui produit un *custom element* convient (JS natif, Lit, Svelte, Vue…). Le dossier
`widgets/` est celui de l'installation ; un plugin peut aussi livrer les siens dans `widgets/` à côté de son
`plugin.json`.

### 2.2 Blazor

Un composant Razor dans une bibliothèque compilée avec Krissvia (« Krissvia.Widgets.Perso »), déclarée dans la
configuration (`Widgets:Assemblies`). Le composant porte un attribut `[KrissviaWidget("jauge-cuve")]`, reçoit un
`[Parameter] WidgetContext Context` (mêmes données que §4) et est rendu par le tableau de bord sans autre code.
Le manifeste est le même, avec `"blazor": "Krissvia.Widgets.Perso.JaugeCuve"` à la place de `script`/`element`.

Confort : on reste en C# et MudBlazor. Limite : il faut recompiler Krissvia pour l'ajouter, et il ne sera pas
réutilisable hors du serveur Blazor — pour une PWA ou une appli mobile, Blazor sait exporter un composant en
Web Component, ce qui rejoint §2.1.

## 3. Le manifeste `widget.json`

```json
{
  "contract": 1,
  "id": "jauge-cuve",
  "name": "Jauge de cuve",
  "description": "Niveau d'une cuve en pourcentage, avec seuil d'alerte.",
  "version": "1.0.0",
  "element": "kv-jauge-cuve",
  "script": "jauge-cuve.js",
  "slots": [
    { "name": "niveau", "type": "entity", "kinds": ["sensor"], "device_classes": ["moisture", "enum"], "required": true },
    { "name": "pompe",  "type": "entity", "kinds": ["switch"], "required": false }
  ],
  "size": { "cols": 2, "rows": 2 },
  "settings": {
    "type": "object",
    "properties": {
      "seuil": { "type": "number", "title": "Seuil d'alerte (%)", "default": 20, "minimum": 0, "maximum": 100 }
    }
  }
}
```

- `id` : minuscules, chiffres, `-` ; unique dans l'installation. `element` commence par `kv-`.
- `slots` : les entités attendues, chacune avec un rôle. Un widget simple n'a qu'un slot ; une carte « prise
  avec mesure » en a deux (le switch, sa puissance). Un slot vise une entité (`"type": "entity"`) filtrée par
  `kinds`, `device_classes`, `usages` — ou un objet de module (`"type": "thermostat"`, et plus tard d'autres).
- `size` : taille par défaut d'une carte, en colonnes et lignes de la grille du tableau de bord.
- `settings` : options propres à chaque carte, décrites avec le même sous-ensemble de JSON Schema que les réglages
  des plugins — le tableau de bord en tire le formulaire.

## 4. Ce que le widget reçoit

Le tableau de bord crée l'élément, puis lui affecte des **propriétés** (pas des attributs) à chaque changement :

| Propriété | Contenu |
|---|---|
| `entities` | Par slot : `{ entity_id, name, kind, device_class, unit, commands, room, usage }`. Absent si le slot facultatif n'est pas lié. |
| `states` | Par slot : `{ value, available, measured_at }`, ou `null` si jamais reçu. Remplacé à chaque changement d'état. |
| `settings` | Les options de la carte, validées contre le schéma du manifeste. |
| `kv` | L'hôte : voir §5. |

Exemple minimal :

```js
class JaugeCuve extends HTMLElement {
  #root = this.attachShadow({ mode: "open" });
  set states(s) { this._states = s; this.#render(); }
  set settings(o) { this._settings = o; this.#render(); }
  set kv(h) { this._kv = h; }

  #render() {
    const niveau = this._states?.niveau;
    this.#root.innerHTML = niveau == null ? "<p>Jamais reçu</p>"
      : !niveau.available ? "<p>Indisponible</p>"
      : `<p class="${niveau.value < (this._settings?.seuil ?? 20) ? "alerte" : ""}">${niveau.value} %</p>`;
  }
}
customElements.define("kv-jauge-cuve", JaugeCuve);
```

Thème : le widget hérite de variables CSS (`--kv-primary`, `--kv-text`, `--kv-text-secondary`, `--kv-surface`,
`--kv-success`, `--kv-warning`, `--kv-error`), recalculées en mode clair ou sombre. Le Shadow DOM est recommandé :
les styles du widget ne fuient pas, ceux de la page n'entrent pas.

## 5. Ce que le widget peut faire : l'hôte `kv`

```ts
kv.command(slot: string, command: string, args?: object): Promise<{ outcome, verification }>
kv.history(slot: string, from: Date, to: Date): Promise<Array<{ value, measured_at }>>   // version 2
kv.openEntity(slot: string): void      // ouvre la fiche de l'entité
```

- `command` passe par `POST /api/entities/{id}/commands` : l'ordre est **vérifié** comme tous les autres
  (ARCHITECTURE.md §7.3) ; la promesse rend `accepted` / `refused`… et `confirmed` / `not_confirmed`. Un widget
  ne doit pas afficher « allumé » parce qu'il a cliqué : il attend l'état suivant, qui arrive dans `states`.
- Un widget ne vise **que les entités de ses slots** : `command` refuse un autre identifiant. Il ne peut pas
  découvrir le reste de la maison.

## 6. Choisir le widget

Ordre de priorité, pour une carte du tableau de bord :

1. le widget choisi **sur cette carte** ;
2. sinon celui choisi **pour l'entité** (champ de l'utilisateur dans l'écran Entités, comme la pièce ou la
   puissance) ;
3. sinon le meilleur widget **par défaut** : celui dont le slot principal correspond le plus précisément
   (`usage` > `device_class` > `kind`), widgets standards à égalité perdante — un widget installé exprès gagne ;
4. sinon le widget générique « valeur », qui sait tout afficher.

Les listes de choix ne proposent que les widgets dont le slot principal accepte l'entité.

## 7. Widgets standards prévus (version 1)

Valeur (générique), température/humidité, interrupteur, lumière, ouverture, mouvement, puissance, prise avec
mesure (deux slots), thermostat (slot `thermostat`), batterie. Courbe et historique en version 2, avec
`kv.history`.

## 8. Ce qu'il faut côté serveur (préalable)

- `GET /api/entities` et `/api/entities/{id}` : registre + dernier état.
- `POST /api/entities/{id}/commands` : ordre vérifié, signé `widget:{id}`.
- **Flux en direct** : WebSocket `/api/live` (ou SSE) qui pousse chaque changement d'état et du registre, filtrable
  par entité. Le tableau de bord s'y abonne une fois et répartit vers les cartes.
- `GET /api/widgets` : le catalogue (standards, dossier `widgets/`, plugins), avec les erreurs de chargement.
- Authentification : la session de l'interface ou la clé d'API, comme le reste de `/api`.

## 9. Confiance

Un widget s'exécute dans le navigateur avec la session de l'utilisateur connecté : il pourrait appeler toute
l'API. La restriction de §5 est une **discipline**, pas une barrière. Donc :

- seul l'administrateur installe un widget (dossier sur le serveur, ou plugin qu'il a activé) ;
- l'écran du catalogue montre l'origine de chaque widget (standard, dossier local, plugin X) ;
- pas de chargement depuis une URL extérieure : `script` est un chemin relatif au dossier du widget.

## 10. Questions ouvertes

1. Le slot `thermostat` : objet de module dédié, ou les thermostats deviennent-ils des entités (`climate`)
   publiées par le cœur ? La seconde voie unifierait tout, mais crée des entités qui ne viennent d'aucun plugin.
2. Taille de grille : colonnes fixes (12) ou cartes à taille libre comme dans DomoMud ?
3. Version 1 de `kv.history` dès le tableau de bord, ou après ?
