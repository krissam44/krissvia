import { KvWidget, esc, nombre, depuis, absence, icone } from "../commun.js";

// Tuiles de la carte « compteur » de DomoMud : la puissance, puis ce que l'appareil mesure d'autre.
const TUILES = [
    ["puissance", "W", "PUISSANCE", "#ffb300", 0],
    ["tension", "V", "TENSION", "#66bb6a", 1],
    ["courant", "A", "COURANT", "#ce93d8", 2],
    ["energie", "kWh", "INDEX", "#42a5f5", 2],
];

/**
 * Carte d'un compteur d'énergie (prise ou pince mesurée) : des tuiles, une par mesure liée. Pas encore de « depuis
 * minuit » ni de coût du jour : il faudra l'historique.
 */
class KvEnergie extends KvWidget {
    static historique = "puissance";
    static css = `
        .entete { display: flex; align-items: center; gap: 6px; margin-bottom: 8px; }
        .entete .ms { font-size: 1.3rem; color: #ffb300; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .entete .quand { font-size: .66rem; color: var(--kv-text-secondary, #999); white-space: nowrap; }
        /* Autant de tuiles par ligne que la carte en loge (140 px chacune) : toutes sur une ligne jusqu'au tiers d'un grand
           écran, deux par ligne en dessous. */
        .tuiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px 8px; }
        .tuile { padding: 6px 8px; border-radius: 8px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 8%, transparent); }
        .tuile .v { font-size: 1.35rem; font-weight: 700; line-height: 1.1; }
        .tuile .v .u { font-size: .8rem; font-weight: 400; margin-left: 2px; }
        .tuile .l { font-size: .62rem; letter-spacing: .04em; color: var(--kv-text-secondary, #777); }`;

    rendu() {
        const entite = this._entities.puissance;
        const etat = this._states.puissance;
        // Le nom de l'appareil plutôt que celui de la mesure : « Prise lave-linge », pas « Prise lave-linge power 0 ».
        const nom = (entite?.name ?? "—").replace(/\s+(puissance|power)(\s+\d+)?$/i, "");
        const entete = `<div class="entete">${icone("bolt")}<div class="nom" title="${esc(entite?.name)}">${esc(nom)}</div>
            <span class="quand">${esc(depuis(etat?.measured_at))}</span></div>`;
        const manque = absence(etat, entite);
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const tuiles = TUILES
            .filter(([slot]) => slot === "puissance" || this._entities[slot])
            .map(([slot, unite, libelle, couleur, dec]) => {
                const e = this._states[slot];
                const v = e?.available && typeof e.value === "number" ? nombre(e.value, dec) : "—";
                return `<div class="tuile"><div class="v" style="color:${couleur}">${v}<span class="u">${unite}</span></div><div class="l">${libelle}</div></div>`;
            }).join("");

        return `<div class="carte">${entete}<div class="tuiles">${tuiles}</div></div>`;
    }
}

customElements.define("kv-energie", KvEnergie);
