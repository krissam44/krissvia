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

### 2.2 Blazor (fait le 18/10/2026)

Un composant Razor dans une bibliothèque .NET qui ne référence que `Krissvia.Widgets.Blazor` (et, si besoin,
MudBlazor, déjà dans Krissvia). Le manifeste s'écrit en attributs :

```razor
@attribute [KrissviaWidget("jauge", "Jauge", Description = "…", Rows = 2,
    Settings = """{ "type": "object", "properties": { "max": { "type": "number", "default": 100 } } }""")]
@attribute [WidgetSlot("mesure", Kinds = ["sensor"], Required = true)]

<MudProgressCircular Value="@(Context.State("mesure")?.Number ?? 0)" />

@code {
    [Parameter] public WidgetContext Context { get; set; } = default!;
}
```

- `WidgetContext` porte les mêmes données qu'un Web Component (§4) : `Entities`, `States` (null = jamais reçu),
  `Settings`, et `CommandAsync(slot, commande, args)` — un ordre vérifié, limité aux entités des slots, signé
  `widget:{id}`. Un nouveau contexte arrive à chaque changement d'état : le composant se redessine seul.
- **Installation** : déposer la .dll à la racine du dossier `widgets` de l'installation (ou la nommer dans
  `Widgets:Assemblies`), puis **redémarrer** — un assemblage .NET ne se décharge pas, contrairement à un Web
  Component qui se prend à chaud.
- Rendu dans la page, côté serveur, dans une barrière d'erreur : une exception affiche un bandeau sur la carte, le
  reste du tableau de bord continue. Le thème MudBlazor (clair ou sombre) s'applique de lui-même.
- Pas d'isolation CSS (`.razor.css`) ni de fichiers statiques : les styles vont dans un `<style>` du composant,
  avec des classes préfixées.
- **Confiance** : c'est du code qui s'exécute sur le serveur, comme un plugin ; seul l'administrateur dépose une
  bibliothèque (§9).
- Limite : un widget Blazor ne s'affiche que dans l'interface Blazor de Krissvia. Pour une PWA ou une appli,
  écrire un Web Component (§2.1).
- Exemple complet : `exemples/Krissvia.Widgets.Exemple` (une jauge et un interrupteur).

## 3. Le manifeste `widget.json`

```json
{
  "contract": 2,
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
  "size": { "cols": 3, "rows": 2 },
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
  L'usage est choisi par l'utilisateur dans l'écran Entités (`radiateur`, `eclairage`, `prise`, `compteur` pour un interrupteur) ; une
  entité `light` a d'elle-même l'usage `eclairage`.
- `size` : taille par défaut d'une carte. `cols` en douzièmes de la largeur d'un grand écran (1 à 12 : 3 = un quart,
  quatre cartes par ligne, comme un `lg="3"` de MudGrid), `rows` en lignes (1 à 4). Contrat 2 ; un manifeste du
  contrat 1 comptait en sixièmes (1 à 6) : il est encore lu, sa largeur doublée. Même unité pour l'attribut
  `Cols` d'un widget Blazor (2 par défaut).
- `settings` : options propres à chaque carte, décrites avec le même sous-ensemble de JSON Schema que les réglages
  des plugins — le tableau de bord en tire le formulaire.

## 4. Ce que le widget reçoit

Le tableau de bord crée l'élément, puis lui affecte des **propriétés** (pas des attributs) à chaque changement :

| Propriété | Contenu |
|---|---|
| `entities` | Par slot : `{ entity_id, name, kind, device_class, unit, commands, room, usage }`. Absent si le slot facultatif n'est pas lié. Si la carte a un nom propre (« Nom sur la carte »), il remplace `name` dans l'entité du slot principal : le widget n'a rien à faire. |
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

> Fait le 16/10/2026 : entités, ordres vérifiés et flux en direct (voir API.md, « Entités et flux en direct »).
> Fait le 18/10/2026 : `GET /api/widgets` et les pages du tableau de bord (`/api/dashboards`).

- `GET /api/entities` et `/api/entities/{id}` : registre + dernier état.
- `POST /api/entities/{id}/commands` : ordre vérifié, signé `widget:{id}`.
- **Flux en direct** : WebSocket `/api/live` (ou SSE) qui pousse chaque changement d'état et du registre, filtrable
  par entité. Le tableau de bord s'y abonne une fois et répartit vers les cartes.
- `GET /api/widgets` : le catalogue (standards, dossier `widgets/`, plugins activés), avec les erreurs de chargement.
- Authentification : la session de l'interface ou la clé d'API, comme le reste de `/api`.

## 9. Confiance

Un widget s'exécute dans le navigateur avec la session de l'utilisateur connecté : il pourrait appeler toute
l'API. La restriction de §5 est une **discipline**, pas une barrière. Donc :

- seul l'administrateur installe un widget (dossier sur le serveur, ou plugin qu'il a activé) ;
- l'écran du catalogue montre l'origine de chaque widget (standard, dossier local, plugin X) ;
- pas de chargement depuis une URL extérieure : `script` est un chemin relatif au dossier du widget.

## 10. Décisions (16/10/2026)

1. **Les thermostats deviennent des entités `climate`, publiées par le cœur** (fait le 16/10/2026, voir API.md). Un seul chemin pour tout (API, flux,
   widgets, scénarios, historique) ; même modèle ensuite pour l'alarme, la présence, le délesteur. Le descripteur
   porte la liste des modes (le widget les dessine depuis les données, jamais en dur), les bornes et le pas de la
   consigne, le nombre de radiateurs ; l'état porte ambiante, humidité et extérieure si connues, consigne, mode,
   planning actif, chauffe, part de chauffe TPI et le **statut calculé par le cœur**. Ordres : `set_mode`,
   `set_schedule`, `set_target` (valeur exacte) et `adjust_target` (pas relatif, calculé côté serveur : pas de
   double « + » sur une valeur périmée). Le widget standard reprend la carte de DomoMud (couleurs des modes,
   barre TPI ou d'inertie, icônes Material Symbols, chargées par la page hôte) ; l'écran Thermostats affiche
   ce même widget (celui choisi pour l'entité s'il est un Web Component), avec le même hôte que le tableau de bord. Les modes
   restent ceux de Krissvia : Arrêt, Hors-gel, Éco, Confort −2, Confort −1, Confort.
2. **Grille responsive**, comme Gladys, Home Assistant et DomoMud : le nombre de colonnes suit la largeur de
   l'écran, une carte occupe `size.cols` × `size.rows` cellules (ramenée à la largeur disponible sur téléphone).
3. `kv.history` (courbes, historique) : **plus tard**, pas dans la version 1 du tableau de bord.

## 11. Tableau de bord, version 1 (18/10/2026)

Organisation reprise de DomoMud : des **pages** (« Confort », « Technique »…), des **sections** repliables, et dans
chaque section une grille de cartes. Mode édition sur une copie : « Enregistrer » écrit tout, « Annuler » ne
laisse rien ; replier une section hors édition s'enregistre aussitôt.

- Grille : 12 colonnes sur grand écran, comme MudGrid et DomoMud ; largeur d'une carte de 1 à 12 (le menu propose
  1/6, 1/4, 1/3, 1/2, 2/3, 3/4 et pleine largeur), hauteur de 1 à 4. Sur tablette, 4 colonnes (une carte y prend
  la moitié de sa largeur, arrondie au-dessus) ; sur téléphone, 2 (une demi-ligne jusqu'à 2/12, puis la ligne).
  Les pages écrites avant la grille de 12 (sans `grid`) sont relues en doublant leurs largeurs.
- Cartes : glisser-déposer, y compris d'une section à l'autre ; flèches ◀ ▶ pour le téléphone ; menu de taille.
- Nom sur la carte (`title` de la carte, 80 caractères au plus) : remplace, sur cette carte seulement, le nom de l'entité principale ; vide, la carte suit le nom de l'écran Entités.
- **Proposer** (mode édition) : ajoute une section par famille (thermostats, températures, interrupteurs et
  lumières, capteurs, énergie), chaque entité avec son meilleur widget ; les slots secondaires prennent les entités
  du même appareil (l'humidité d'une sonde) ; les radiateurs pilotés par un thermostat ne sont pas proposés. Rien
  n'est écrit avant « Enregistrer » — DomoMud générait ses sections tout seul au premier affichage.
- Widgets standards : `valeur`, `temperature`, `interrupteur`, `radiateur`, `eclairage`, `prise` et `compteur` (le même bouton, avec la flamme, l'ampoule, la fiche ou le compteur de leur usage ; base commune `bascule.js`), `ouverture`, `puissance`, `thermostat`, `meteo` (entité `weather` du plugin Météo : conditions et prévision sur 7 jours, à la manière de la carte de DomoMud),
  `linky` (puissance apparente et part de l'abonnement, HP/HC, courant, tension, index), `energie` (tuiles d'un compteur :
  puissance, tension, courant, index), `delesteur` (entité `load_shedding` publiée par le cœur) et `alarme` (entité `alarm` :
  état, décomptes, clavier qui arme et désarme avec un code, `ALARME.md` §6). À score égal, le widget
  dont le slot principal accepte le moins de classes l'emporte (Linky devant Puissance), après la règle « installé exprès ».
- Widgets locaux : dossier `widgets` à côté de la base (ou `Widgets:Directory`), publié sous `/widgets/local`.
- **Widgets livrés par un plugin** (22/10/2026) : le dossier `widgets/` à côté du `plugin.json`, un sous-dossier par
  widget, comme `widgets/` de l'installation. Voir §13.

## 13. Widgets livrés par un plugin (22/10/2026)

Un plugin qui publie une entité particulière (une machine virtuelle Proxmox, la santé d'un Raspberry) livre la carte
qui va avec, sans toucher au cœur :

```
Krissvia.Plugin.Proxmox/        (le dossier du plugin.json : bin/… pour un plugin C#, le dossier lui-même en Python)
  plugin.json
  widgets/
    proxmox-vm/
      widget.json
      proxmox-vm.js
```

- **Plugins ACTIVÉS seulement** : un plugin désactivé dans Services ne publie plus ses widgets — ils sortent du
  catalogue, leurs fichiers répondent 404, et une carte qui en utilisait un retombe sur le widget standard de son
  entité (vérifié). L'administrateur a activé le plugin : c'est la règle de confiance du §9.
- Publiés sous `/widgets/plugins/{id}/{dossier}/{fichier}`, origine `plugin:{id}` (affichée « plugin {id} » dans les
  listes de choix). Rien n'est servi hors du dossier `widgets/` du plugin, où se trouvent aussi ses réglages :
  `..`, chemin absolu, lecteur Windows, antislash et segment vide sont refusés (`WidgetCatalog.SafeFile`, testé).
- Identifiant déjà pris : le premier trouvé l'emporte, dans l'ordre standards, dossier local, plugins (par
  identifiant) ; le refus s'affiche dans les erreurs du catalogue. Un plugin ne peut donc pas remplacer un widget
  standard : il livre le sien sous un autre nom, et le choix par défaut le préfère s'il est plus précis (§6) — un
  widget de plugin compte comme « installé exprès ».
- Le script peut importer la base des standards : `import { KvWidget, esc } from "/widgets/standard/commun.js";`.
- **Plugin C#** : faire copier le dossier à côté de l'exécutable, comme `plugin.json` :
  `<Content Include="widgets\**" CopyToOutputDirectory="PreserveNewest" />`. **Plugin Python** : rien à faire.
- Relu à chaque chargement du catalogue : activer un plugin, ou modifier un de ses widgets, se voit sans redémarrer.

## 12. Widget par entité (18/10/2026)

Le §6 au complet. L'écran Entités donne à chaque entité un champ **Widget** (« Automatique » par défaut : le plus
précis), enregistré dans le registre comme la pièce ou la puissance. Une carte peut :

- **suivre son entité** (`"entity": "sensor.salon"`, sans `widget`) : son widget est celui de l'entité, ses slots
  secondaires se lient aux entités actives du même appareil (l'humidité d'une sonde). Changer le widget d'une sonde
  dans l'écran Entités change toutes ses cartes, en direct. C'est le choix par défaut à l'ajout, et celui de
  « Proposer » ;
- **imposer** son widget et ses liens (`"widget"`, `"entities"`), comme avant.

Un widget choisi pour l'entité mais devenu impossible (retiré du dossier, ou qui refuse l'entité) laisse place au
plus précis ; une entité disparue s'affiche dans « valeur ». La résolution est dans
`Krissvia.Core.Widgets.WidgetResolver`.
