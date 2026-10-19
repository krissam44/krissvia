import { KvWidget, esc, nombre, absence, icone, couleurCharge } from "../commun.js";

/** Heures creuses d'après le libellé du tarif (« HEURES CREUSES », « HC BLEU »…), null s'il n'en dit rien. */
function heuresCreuses(libelle) {
    if (typeof libelle !== "string") return null;
    const t = libelle.toUpperCase();
    if (t.includes("CREUS") || /\bHC\b/.test(t)) return true;
    if (t.includes("PLEIN") || /\bHP\b/.test(t)) return false;
    return null;
}

/**
 * Carte du compteur Linky, comme celle de DomoMud : puissance apparente et sa part de l'abonnement, jauge, HP/HC, puis
 * courant, tension et index. Les mesures secondaires sont celles du même appareil, liées toutes seules.
 */
class KvLinky extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .entete .ms { font-size: 1.4rem; color: #ef5350; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; }
        .centre { text-align: center; margin-bottom: 8px; }
        .valeur { font-size: 2.2rem; font-weight: 700; line-height: 1; }
        .valeur .u { font-size: 1rem; font-weight: 400; }
        .jauge { height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); overflow: hidden; }
        .jauge > div { height: 100%; border-radius: 3px; transition: width .4s; }
        .ligne { display: flex; justify-content: space-between; margin-top: 8px; font-size: .72rem; color: var(--kv-text-secondary, #777); }`;

    rendu() {
        const entite = this._entities.puissance;
        const etat = this._states.puissance;
        const valeur = s => this._states[s]?.available && this._states[s].value != null ? this._states[s].value : null;
        const hc = heuresCreuses(valeur("tarif"));
        const badge = hc === null ? "" : (c => `<span class="badge" style="background:${c}22;color:${c};border:1px solid ${c}44">${hc ? "HC" : "HP"}</span>`)(hc ? "#66bb6a" : "#ff5722");
        // Le nom de l'appareil (« Linky ») plutôt que celui de la mesure (« … puissance apparente », « … sinsts »).
        const nom = (entite?.name ?? "").replace(/\s*(puissance apparente|sinsts)$/i, "") || "Linky";
        const entete = `<div class="entete">${icone("electric_meter")}<div class="nom" title="${esc(nom)}">${esc(nom)}</div>${badge}</div>`;
        const manque = absence(etat, entite);
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        // L'abonnement est une option de la carte : le compteur ne le publie pas.
        const abonnement = (Number(this._settings?.abonnement_kva) || 6) * 1000;
        const va = typeof etat.value === "number" ? etat.value : null;
        const part = va !== null ? Math.min(1, Math.max(0, va / abonnement)) : 0;
        const couleur = couleurCharge(part);
        const bas = [
            valeur("courant") !== null ? `<span>${nombre(valeur("courant"), 0)} A</span>` : "",
            valeur("tension") !== null ? `<span>${nombre(valeur("tension"), 0)} V</span>` : "",
            valeur("index") !== null ? `<span>${nombre(valeur("index"), 0)} kWh</span>` : "",
        ].filter(Boolean);

        return `<div class="carte">
            ${entete}
            <div class="centre">
                <div class="valeur" style="color:${couleur}">${nombre(va, 0)}<span class="u"> VA</span></div>
                <div class="detail">${Math.round(part * 100)} % · ${nombre(va === null ? null : va / 1000, 2)} kVA sur ${nombre(abonnement / 1000)}</div>
            </div>
            <div class="jauge"><div style="width:${part * 100}%;background:${couleur}"></div></div>
            ${bas.length ? `<div class="ligne">${bas.join("")}</div>` : ""}
        </div>`;
    }
}

customElements.define("kv-linky", KvLinky);
