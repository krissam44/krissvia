import { KvWidget, esc, nombre, depuis, absence, libelleBooleen } from "../commun.js";

/** N'importe quelle entité : nombre et unité, vrai/faux, texte, ou le libellé d'un état composite. */
class KvValeur extends KvWidget {
    static historique = "entite";
    rendu() {
        const entite = this._entities.entite;
        const etat = this._states.entite;
        return `<div class="carte">
            <div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
            ${absence(etat, entite) ?? `<div class="grand">${this.valeur(entite, etat.value)}</div>`}
            <div class="detail bas">${esc(depuis(etat?.measured_at))}</div>
        </div>`;
    }

    valeur(entite, v) {
        if (typeof v === "number") return `${nombre(v, 2)}${entite?.unit ? `<span class="unite">${esc(entite.unit)}</span>` : ""}`;
        if (typeof v === "boolean") return esc(libelleBooleen(entite, v));
        if (v && typeof v === "object") {
            if (typeof v.status_label === "string") return esc(v.status_label);
            if (typeof v.on === "boolean") return v.on ? "Allumée" : "Éteinte";
            return `<span style="font-size:.8rem">${esc(JSON.stringify(v))}</span>`;
        }
        return esc(v);
    }
}

customElements.define("kv-valeur", KvValeur);
