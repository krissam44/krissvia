import { KvWidget, esc, absence, icone } from "/widgets/standard/commun.js";

const VERT = "#43a047", ORANGE = "#fb8c00", ROUGE = "#e53935", GRIS = "#90a4ae", BLEU = "#1e88e5";

/** Couleur d'un consommable : rouge sous son seuil, orange sous 25 %, vert sinon ; gris s'il n'a pas de niveau. */
function teinte(c) {
    if (typeof c.level !== "number") return GRIS;
    return c.low ? ROUGE : c.level <= 25 ? ORANGE : VERT;
}

/**
 * Une jauge en arc de 240°, ouverte en bas, comme celles de la carte imprimante de Gladys. `pathLength` ramène l'arc à
 * 100 : la part remplie est le pourcentage, sans calcul de longueur.
 */
function arc(c) {
    const r = 30, cx = 40, cy = 40, debut = 150, fin = 390;
    const point = a => [cx + r * Math.cos(a * Math.PI / 180), cy + r * Math.sin(a * Math.PI / 180)];
    const [x1, y1] = point(debut), [x2, y2] = point(fin);
    const chemin = `M ${x1.toFixed(2)} ${y1.toFixed(2)} A ${r} ${r} 0 1 1 ${x2.toFixed(2)} ${y2.toFixed(2)}`;
    const connu = typeof c.level === "number";
    const niveau = connu ? Math.max(0, Math.min(100, c.level)) : 0;
    const texte = connu ? `${Math.round(c.level)}<tspan class="pc" dx="2">%</tspan>` : c.some_remaining ? "OK" : "?";
    return `<svg viewBox="0 0 80 66" role="img" aria-label="${esc(c.name)} : ${connu ? `${Math.round(c.level)} %` : "niveau inconnu"}">
        <path d="${chemin}" pathLength="100" class="fond"/>
        <path d="${chemin}" pathLength="100" stroke="${teinte(c)}" stroke-dasharray="${niveau} 100" class="plein"${niveau === 0 ? ' style="display:none"' : ""}/>
        <text x="40" y="46" text-anchor="middle" class="valeur">${texte}</text></svg>`;
}

/** « 20:38 » le jour même, « 3 oct. 20:38 » sinon. */
function heure(iso) {
    if (!iso) return "—";
    const d = new Date(iso);
    const h = d.toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
    return d.toDateString() === new Date().toDateString() ? h : `${d.toLocaleDateString("fr-FR", { day: "numeric", month: "short" })} ${h}`;
}

/**
 * Carte « Imprimante », livrée par le plugin Imprimantes IPP (WIDGETS.md §13), d'après celle de Gladys : une jauge par
 * consommable, puis l'état, le dernier relevé et le consommable le plus bas, les alertes, et « Vérifier ». La carte
 * n'invente rien après le clic : le nouveau relevé arrive par l'état de l'entité. Éteinte, l'imprimante garde son
 * dernier relevé à l'écran, avec l'état « Éteinte ou injoignable ».
 */
class KvImprimante extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 6px; font-size: .7rem; font-weight: 700; letter-spacing: .08em;
                  text-transform: uppercase; color: var(--kv-text-secondary, #777); }
        .entete .ms { font-size: 1rem; }
        .nom { font-weight: 600; font-size: .95rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .carte { container-type: inline-size; }
        .jauges { display: grid; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); gap: 8px; margin: 4px 0; }
        /* Quatre jauges (toner + trois pièces, la Samsung) : 2 × 2 comme Gladys plutôt que 3 + 1, sur une ligne si la carte est large. */
        .jauges.quatre { grid-template-columns: repeat(2, 1fr); }
        @container (min-width: 440px) { .jauges.quatre { grid-template-columns: repeat(4, 1fr); } }
        .jauge { display: flex; flex-direction: column; align-items: center; padding: 6px 4px 8px; border-radius: 12px;
                 background: color-mix(in srgb, var(--kv-surface, #fff) 88%, var(--kv-text-secondary, #888) 12%); }
        .jauge svg { width: 72px; height: 60px; }
        .jauge .fond { fill: none; stroke: color-mix(in srgb, var(--kv-text-secondary, #888) 22%, transparent); stroke-width: 7; stroke-linecap: round; }
        .jauge .plein { fill: none; stroke-width: 7; stroke-linecap: round; }
        .jauge .valeur { font-size: 17px; font-weight: 600; fill: var(--kv-text, #222); }
        .jauge .pc { font-size: 9px; font-weight: 400; fill: var(--kv-text-secondary, #777); }
        .jauge .lib { font-size: .72rem; text-align: center; color: var(--kv-text-secondary, #777); line-height: 1.2; }
        .lignes { display: flex; flex-direction: column; gap: 4px; font-size: .8rem; }
        .ligne { display: flex; align-items: center; gap: 8px; }
        .ligne .pastille { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
        .ligne .val { margin-left: auto; font-weight: 600; text-align: right; }
        .alertes { display: flex; flex-direction: column; gap: 3px; }
        .alerte { font-size: .75rem; font-weight: 600; padding: 3px 8px; border-radius: 6px; background: ${ROUGE}1f; color: ${ROUGE}; }
        .alerte.info { background: ${ORANGE}1f; color: ${ORANGE}; }
        .actions button { display: inline-flex; align-items: center; gap: 6px; padding: 6px 14px; border-radius: 10px; font-size: .8rem; font-weight: 600;
                          background: var(--kv-surface, #fff); color: var(--kv-text, #222);
                          border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 30%, transparent); }
        .actions button .ms { font-size: 1rem; color: ${BLEU}; }
        .actions button[disabled] { opacity: .55; cursor: default; }
        .actions button[disabled] .ms { animation: tourne 1s linear infinite; }
        @keyframes tourne { to { transform: rotate(360deg); } }`;

    #attente = false;
    #erreur = null;

    rendu() {
        const entite = this._entities.imprimante;
        const etat = this._states.imprimante;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        // Le cœur nomme l'entité « appareil + Imprimante » : le nom de l'imprimante suffit, sauf si l'utilisateur en a
        // choisi un autre (renommage, « Nom sur la carte »).
        const nom = entite?.name && v.name && !entite.name.startsWith(v.name) ? entite.name : v.name ?? v.model ?? entite?.name ?? "Imprimante";
        const entete = `<div class="entete">${icone("print")}Imprimante</div><div class="nom">${esc(nom)}</div>`;
        const manque = absence(etat, entite);
        if (manque && !(etat && !etat.available && Array.isArray(v.markers))) return `<div class="carte">${entete}${manque}</div>`;

        const marqueurs = Array.isArray(v.markers) ? v.markers : [];
        const jauges = marqueurs.length
            ? `<div class="jauges${marqueurs.length === 4 ? " quatre" : ""}">${marqueurs.map(c => `<div class="jauge">${arc(c)}<span class="lib">${esc(c.name)}</span></div>`).join("")}</div>`
            : `<div class="absent">Aucun consommable annoncé</div>`;

        const allumee = etat.available;
        const couleurEtat = !allumee ? GRIS : v.state === "stopped" ? ROUGE : v.state === "processing" ? BLEU : v.problem ? ORANGE : VERT;
        const bas = v.lowest;
        const couleurBas = bas ? teinte(marqueurs.find(m => m.name === bas.name) ?? bas) : GRIS;
        const ligne = (couleur, libelle, valeur) =>
            `<div class="ligne"><span class="pastille" style="background:${couleur}"></span><span>${libelle}</span><span class="val">${valeur}</span></div>`;
        const alertes = allumee && Array.isArray(v.alerts) && v.alerts.length
            ? `<div class="alertes">${v.alerts.map(a => `<div class="alerte${v.problem ? "" : " info"}">${esc(a)}</div>`).join("")}</div>` : "";

        return `<div class="carte">${entete}${jauges}
            <div class="lignes">
                ${ligne(couleurEtat, "État", esc(allumee ? v.state_label ?? "—" : "Éteinte ou injoignable"))}
                ${ligne("#546e7a", "Dernier relevé", heure(v.last_read))}
                ${bas ? ligne(couleurBas, "Niveau le plus bas", `${esc(bas.name)} · ${Math.round(bas.level)} %`) : ""}
            </div>
            ${alertes}
            <div class="actions bas"><button data-verifier${this.#attente ? " disabled" : ""}>${icone("sync")}Vérifier</button></div>
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}</div>`;
    }

    apresRendu(racine) {
        racine.querySelector("button[data-verifier]")?.addEventListener("click", () => this.#verifier());
    }

    async #verifier() {
        if (!this._kv || this.#attente) return;
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        try {
            const r = await this._kv.command("imprimante", "refresh");
            if (r.outcome !== "accepted") this.#erreur = r.detail ?? (r.outcome === "unreachable" ? "L'imprimante ne répond pas." : "Relevé refusé.");
        } catch {
            this.#erreur = "Serveur injoignable.";
        }
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-imprimante", KvImprimante);
