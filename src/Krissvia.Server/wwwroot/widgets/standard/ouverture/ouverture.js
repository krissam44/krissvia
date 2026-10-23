import { KvWidget, esc, depuis, absence, libelleBooleen } from "../commun.js";

class KvOuverture extends KvWidget {
    static historique = "capteur";
    static css = `.actif { color: var(--kv-warning, #e6a23c); } .alerte { color: var(--kv-error, #d33); }`;

    rendu() {
        const entite = this._entities.capteur;
        const etat = this._states.capteur;
        const manque = absence(etat, entite);
        const actif = etat?.value === true;
        const grave = ["moisture", "smoke"].includes(entite?.device_class);
        return `<div class="carte">
            <div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
            ${manque ?? `<div class="grand ${actif ? (grave ? "alerte" : "actif") : ""}">${esc(libelleBooleen(entite, actif))}</div>`}
            <div class="detail bas">${esc(depuis(etat?.measured_at))}</div>
        </div>`;
    }
}

customElements.define("kv-ouverture", KvOuverture);
