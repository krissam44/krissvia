import { KvWidget, esc, nombre, depuis, absence } from "../commun.js";

class KvPuissance extends KvWidget {
    static historique = "mesure";
    static css = `.alerte { color: var(--kv-error, #d33); }`;

    rendu() {
        const entite = this._entities.mesure;
        const etat = this._states.mesure;
        const seuil = Number(this._settings?.seuil) || 0;
        const valeur = etat?.value;
        const alerte = seuil > 0 && typeof valeur === "number" && valeur > seuil;
        return `<div class="carte">
            <div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
            ${absence(etat, entite) ?? `<div class="grand ${alerte ? "alerte" : ""}">${nombre(valeur, Math.abs(valeur) >= 100 ? 0 : 2)}<span class="unite">${esc(entite?.unit ?? "")}</span></div>`}
            ${seuil > 0 ? `<div class="detail">seuil ${nombre(seuil, 0)} ${esc(entite?.unit ?? "")}</div>` : ""}
            <div class="detail bas">${esc(depuis(etat?.measured_at))}</div>
        </div>`;
    }
}

customElements.define("kv-puissance", KvPuissance);
