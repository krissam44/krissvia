// Base commune des widgets standards. Un widget tiers n'en a pas besoin : il suffit d'un custom element qui
// accepte les propriétés entities, states, settings et kv (WIDGETS.md §4). Celle-ci ne fait que regrouper le
// rendu (une fois par tâche, même si trois propriétés changent) et les formats.

const FR = "fr-FR";

export const STYLE = `
    :host { display: block; height: 100%; color: var(--kv-text, #222); font-family: inherit; }
    .carte { box-sizing: border-box; height: 100%; padding: 12px 14px; display: flex; flex-direction: column; gap: 4px; }
    .carte > * { flex-shrink: 0; }   /* une carte trop pleine déborde en bas au lieu d'écraser son titre */
    .titre { font-size: .82rem; color: var(--kv-text-secondary, #777); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .piece { font-size: .72rem; color: var(--kv-text-secondary, #777); }
    .grand { font-size: 1.9rem; font-weight: 500; line-height: 1.15; }
    .unite { font-size: .95rem; font-weight: 400; color: var(--kv-text-secondary, #777); margin-left: 2px; }
    .detail { font-size: .75rem; color: var(--kv-text-secondary, #777); }
    .absent { font-size: .95rem; font-style: italic; color: var(--kv-text-secondary, #777); }
    .indispo { color: var(--kv-warning, #e6a23c); }
    .erreur { font-size: .75rem; color: var(--kv-error, #d33); }
    .bas { margin-top: auto; }
    button { font: inherit; cursor: pointer; }
`;

export class KvWidget extends HTMLElement {
    #racine = this.attachShadow({ mode: "open" });
    #prevu = false;
    _entities = {};
    _states = {};
    _settings = {};
    _kv = null;

    set entities(v) { this._entities = v ?? {}; this.planifier(); }
    set states(v) { this._states = v ?? {}; this.planifier(); }
    set settings(v) { this._settings = v ?? {}; this.planifier(); }
    set kv(h) { this._kv = h; }

    planifier() {
        if (this.#prevu) return;
        this.#prevu = true;
        queueMicrotask(() => {
            this.#prevu = false;
            this.#racine.innerHTML = `<style>${STYLE}${this.constructor.css ?? ""}</style>${this.rendu()}`;
            this.apresRendu?.(this.#racine);
        });
    }

    /** À écrire par chaque widget : le HTML de la carte. */
    rendu() { return ""; }
}

/** Échappe un texte avant de le mettre dans du HTML : un nom d'entité vient de l'utilisateur ou d'un plugin. */
export function esc(texte) {
    return String(texte ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

export function nombre(v, decimales = 1) {
    return typeof v === "number"
        ? v.toLocaleString(FR, { minimumFractionDigits: 0, maximumFractionDigits: decimales })
        : "—";
}

/** « à l'instant », « il y a 3 min », « il y a 2 h »… */
export function depuis(instant) {
    if (!instant) return "";
    const s = Math.max(0, (Date.now() - new Date(instant).getTime()) / 1000);
    if (s < 60) return "à l'instant";
    if (s < 3600) return `il y a ${Math.floor(s / 60)} min`;
    if (s < 86400) return `il y a ${Math.floor(s / 3600)} h`;
    return `le ${new Date(instant).toLocaleDateString(FR)}`;
}

/** Le bloc à montrer quand il n'y a pas de valeur exploitable, ou null s'il y en a une (WIDGETS.md §1, principe 4). */
export function absence(etat, entite) {
    if (entite?.unknown) return `<div class="absent">Entité inconnue</div>`;
    if (etat == null) return `<div class="absent">Jamais reçu</div>`;
    if (!etat.available) return `<div class="absent indispo">Indisponible</div>`;
    return null;
}

/** Libellé d'un état vrai/faux selon la famille et la classe d'appareil. */
export function libelleBooleen(entite, valeur) {
    switch (entite?.device_class) {
        case "opening": return valeur ? "Ouvert" : "Fermé";
        case "occupancy": return valeur ? "Présence" : "Rien";
        case "moisture": return valeur ? "Fuite" : "Sec";
        case "smoke": return valeur ? "Fumée" : "Normal";
        default:
            if (entite?.kind === "switch" || entite?.kind === "light") return valeur ? "Allumé" : "Éteint";
            return valeur ? "Actif" : "Inactif";
    }
}
