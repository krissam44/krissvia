import { KvWidget, esc, nombre, absence, icone } from "../commun.js";

/** kbit/s → « 34.2 Mbit/s » ou « 850 kbit/s ». */
function debit(kbps) {
    if (typeof kbps !== "number") return null;
    return kbps >= 1000 ? `${nombre(kbps / 1000, 1)} Mbit/s` : `${nombre(kbps, 0)} kbit/s`;
}

/** Marge de bruit : au-dessus de 6 dB la ligne est stable, en dessous de 3 dB elle décroche. */
function couleurMarge(db) {
    return typeof db !== "number" ? "var(--kv-text-secondary, #777)" : db >= 6 ? "#66bb6a" : db >= 3 ? "#ffa726" : "#ef5350";
}

/**
 * Carte « Connexion Internet » de DomoMud : IP publique, débits descendant et montant (et leur maximum), qualité de
 * ligne (marge de bruit, atténuation, profil). Une entité « internet_connection » de n'importe quelle box ; ce qu'elle
 * n'expose pas (pas de DSL sur la fibre) n'est simplement pas montré.
 */
class KvConnexion extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; }
        .entete .ms { font-size: 1.5rem; }
        .entete .nom { font-weight: 600; font-size: 1.05rem; }
        .badge { padding: 2px 10px; border-radius: 12px; font-size: .72rem; font-weight: 600; }
        .colonnes { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; text-align: center; }
        .v { font-size: 1.35rem; font-weight: 700; line-height: 1.2; }
        .l { font-size: .68rem; letter-spacing: .05em; color: var(--kv-text-secondary, #777); text-transform: uppercase; margin-top: 2px; }
        .m { font-size: .68rem; color: var(--kv-text-secondary, #888); margin-top: 2px; }
        .s { font-size: .78rem; color: var(--kv-text-secondary, #777); }
        .note { margin-top: 10px; font-size: .68rem; color: var(--kv-text-secondary, #999); text-align: center; }`;

    rendu() {
        const entite = this._entities.connexion;
        const etat = this._states.connexion;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);

        const enLigne = v.up === true;
        const couleur = manque ? "#78909c" : enLigne ? "#66bb6a" : "#ef5350";
        const libelle = manque ? "" : `${enLigne ? "En ligne" : "Hors ligne"}${v.link_type ? ` · ${esc(String(v.link_type).toUpperCase())}` : ""}`;
        const badge = libelle ? `<span class="badge" style="color:${couleur};border:1px solid ${couleur}">${libelle}</span>` : "";
        const entete = `<div class="entete">${icone("language")}<span class="nom">Connexion Internet</span>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const colonnes = [];
        if (v.public_ip) colonnes.push(`<div><div class="v" style="color:#26a69a">${esc(v.public_ip)}</div><div class="l">IP publique</div></div>`);
        if (debit(v.down_kbps)) colonnes.push(`<div><div class="v" style="color:#42a5f5">↓ ${debit(v.down_kbps)}</div><div class="l">Débit descendant</div>
            ${debit(v.down_max_kbps) ? `<div class="m">max ${debit(v.down_max_kbps)}</div>` : ""}</div>`);
        if (debit(v.up_kbps)) colonnes.push(`<div><div class="v" style="color:#ffa726">↑ ${debit(v.up_kbps)}</div><div class="l">Débit montant</div>
            ${debit(v.up_max_kbps) ? `<div class="m">max ${debit(v.up_max_kbps)}</div>` : ""}</div>`);
        if (typeof v.snr_down_db === "number") {
            const pire = Math.min(v.snr_down_db, typeof v.snr_up_db === "number" ? v.snr_up_db : v.snr_down_db);
            colonnes.push(`<div><div class="v" style="color:${couleurMarge(pire)};font-size:1.05rem">SNR ${nombre(v.snr_down_db)} dB${typeof v.snr_up_db === "number" ? ` / ${nombre(v.snr_up_db)} dB` : ""}</div>
                ${typeof v.attenuation_down_db === "number" ? `<div class="s">Att. ${nombre(v.attenuation_down_db)} dB${typeof v.attenuation_up_db === "number" ? ` / ${nombre(v.attenuation_up_db)} dB` : ""}</div>` : ""}
                <div class="l">Qualité ligne (↓/↑)${v.profile ? ` · ${esc(v.profile)}` : ""}</div></div>`);
        }

        const note = debit(v.down_kbps) ? `<div class="note">Débits négociés à la synchronisation de la ligne (pas une mesure de vitesse). Marge de bruit (SNR) élevée = ligne stable ; atténuation élevée = ligne longue.</div>` : "";
        return `<div class="carte">${entete}<div class="colonnes">${colonnes.join("")}</div>${note}</div>`;
    }
}

customElements.define("kv-connexion", KvConnexion);
