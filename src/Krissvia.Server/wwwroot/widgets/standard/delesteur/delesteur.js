import { KvWidget, esc, nombre, absence, icone, couleurCharge } from "../commun.js";

const ACTIF = "#f44336";

/**
 * Carte du délesteur, comme celle de DomoMud : puissance lue et sa part de l'abonnement, jauge, et ce qui est délesté.
 * Tout vient de l'entité publiée par le cœur : la carte ne décide rien.
 */
class KvDelesteur extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; }
        .entete .ms { font-size: 1.4rem; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; }
        .centre { text-align: center; margin-bottom: 8px; }
        .valeur { font-size: 2.2rem; font-weight: 700; line-height: 1; }
        .valeur .u { font-size: 1rem; font-weight: 400; }
        .jauge { height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); overflow: hidden; }
        .jauge > div { height: 100%; border-radius: 3px; transition: width .4s; }
        .pied { font-size: .72rem; margin-top: 6px; text-align: center; color: var(--kv-text-secondary, #777); }
        .pied.actif { color: ${ACTIF}; font-weight: 600; }`;

    rendu() {
        const entite = this._entities.delesteur;
        const etat = this._states.delesteur;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);
        const actif = !manque && v.shedding;

        const statut = manque ? "" : !v.enabled ? ["ARRÊTÉ", "#78909c"] : v.source_unavailable ? ["SANS MESURE", "#ffa726"] : actif ? ["ACTIF", ACTIF] : ["OK", "#66bb6a"];
        const badge = statut ? `<span class="badge" style="background:${statut[1]}22;color:${statut[1]};border:1px solid ${statut[1]}44">${statut[0]}</span>` : "";
        const entete = `<div class="entete">${icone("electrical_services", `color:${actif ? ACTIF : "#ff9800"}`)}<div class="nom">Délesteur</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const abonnement = Number(v.subscription_va) || 0;
        const part = abonnement > 0 && typeof v.power === "number" ? Math.min(1, Math.max(0, v.power / abonnement)) : 0;
        const couleur = couleurCharge(part);
        const delestes = Array.isArray(v.shed) ? v.shed : [];
        const pied = delestes.length === 0
            ? (v.devices ? "Aucun équipement délesté" : "Aucun équipement délestable")
            : `⚡ ${delestes.length === 1 ? "Délesté" : `${delestes.length} délestés`} : ${delestes.map(d => esc(d.name)).join(", ")}`;

        // Teinte du cadre pendant un délestage, comme DomoMud.
        const cadre = actif ? `box-shadow: inset 0 0 0 1px ${ACTIF}55, 0 4px 20px ${ACTIF}22;` : "";
        return `<div class="carte" style="border-radius:12px;${cadre}">
            ${entete}
            <div class="centre">
                <div class="valeur" style="color:${couleur}">${nombre(v.power, 0)}<span class="u"> VA</span></div>
                <div class="detail">${abonnement > 0 ? `${Math.round(part * 100)} % de l'abonnement (${nombre(abonnement / 1000)} kVA)` : ""}${v.source ? ` · ${esc(v.source)}` : ""}</div>
            </div>
            <div class="jauge"><div style="width:${part * 100}%;background:${couleur}"></div></div>
            <div class="pied ${delestes.length ? "actif" : ""}">${pied}</div>
        </div>`;
    }
}

customElements.define("kv-delesteur", KvDelesteur);
