import { KvWidget, esc, absence, icone } from "../commun.js";

const VERT = "#66bb6a", GRIS = "#78909c", ORANGE = "#ffa726";

/**
 * Présence du foyer : un badge (occupée, vide, détection perdue) et la liste des personnes suivies avec leur pastille.
 * Tout est calculé par le cœur ; « détection perdue » veut dire que la maison est tenue pour occupée par sécurité.
 */
class KvPresence extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; }
        .entete .ms { font-size: 1.4rem; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .personnes { display: flex; flex-direction: column; gap: 6px; overflow: hidden; }
        .personne { display: flex; align-items: center; gap: 8px; font-size: .85rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .point { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
        .etat { margin-left: auto; font-size: .72rem; color: var(--kv-text-secondary, #777); }`;

    rendu() {
        const entite = this._entities.presence;
        const etat = this._states.presence;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);

        const [libelle, couleur, ic] = manque ? ["", GRIS, "home"]
            : v.detection_lost ? ["INCONNUE", ORANGE, "help"]
            : !v.tracked ? ["NON SUIVIE", GRIS, "home"]
            : v.occupied ? ["OCCUPÉE", VERT, "home"]
            : ["VIDE", GRIS, "door_open"];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const entete = `<div class="entete">${icone(ic, `color:${couleur}`)}<div class="nom">Présence</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const ligne = (nom, c, texte) => `<div class="personne"><span class="point" style="background:${c}"></span>${esc(nom)}<span class="etat">${texte}</span></div>`;
        const personnes = [
            ...(v.present ?? []).map(n => ligne(n, VERT, "présent")),
            ...(v.absent ?? []).map(n => ligne(n, GRIS, "absent")),
            ...(v.unknown ?? []).map(n => ligne(n, ORANGE, "inconnu")),
        ].join("");

        const depuis = !v.occupied && v.empty_since
            ? `<div class="detail bas">Vide depuis ${new Date(v.empty_since).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" })}</div>` : "";
        const note = v.detection_lost ? `<div class="detail bas" style="color:${ORANGE}">Détection perdue : maison tenue pour occupée par sécurité.</div>`
            : !v.tracked ? `<div class="detail">Aucun appareil suivi : choisissez-les dans les réglages du plugin Livebox.</div>` : "";

        return `<div class="carte">${entete}<div class="personnes">${personnes}</div>${note}${depuis}</div>`;
    }
}

customElements.define("kv-presence", KvPresence);
