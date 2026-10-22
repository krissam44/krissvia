import { KvWidget, esc, absence, icone, couleurCharge, nombre } from "/widgets/standard/commun.js";

const VERT = "#66bb6a", ORANGE = "#ffa726", ROUGE = "#ef5350";

/** Température du processeur : un Raspberry Pi bride vers 80 °C, s'en inquiéter à partir de 70. */
const couleurTemperature = t => t >= 75 ? ROUGE : t >= 65 ? ORANGE : VERT;

/** « 3 j 4 h », « 5 h 12 min », « 12 min ». */
function duree(secondes) {
    if (typeof secondes !== "number") return "";
    const j = Math.floor(secondes / 86400), h = Math.floor(secondes % 86400 / 3600), m = Math.floor(secondes % 3600 / 60);
    return j > 0 ? `${j} j ${h} h` : h > 0 ? `${h} h ${m} min` : `${m} min`;
}

/**
 * Carte « Santé de la machine », livrée par le plugin Raspberry (WIDGETS.md §13) : une barre par ressource, et
 * l'alimentation en tête quand elle pose problème — une sous-tension est la cause classique d'une carte SD ou d'une
 * base corrompue, elle ne doit pas se cacher en bas d'une liste.
 */
class KvRaspberry extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .modele { font-size: .7rem; color: var(--kv-text-secondary, #777); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .alerte { font-size: .75rem; font-weight: 600; padding: 4px 8px; border-radius: 6px; margin: 2px 0; }
        .ligne { display: grid; grid-template-columns: 92px 1fr 52px; align-items: center; gap: 8px; font-size: .78rem; }
        .ligne .valeur { text-align: right; font-variant-numeric: tabular-nums; }
        .jauge { height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); overflow: hidden; }
        .jauge > div { height: 100%; border-radius: 3px; }
        .lignes { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; }`;

    rendu() {
        const entite = this._entities.sante;
        const etat = this._states.sante;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);

        const probleme = v.undervoltage_now || v.throttled_now;
        const passe = !probleme && (v.undervoltage_since_boot || v.throttled_since_boot);
        const [libelle, couleur] = manque ? ["", "#78909c"] : probleme ? ["ALIMENTATION", ROUGE] : passe ? ["À SURVEILLER", ORANGE] : ["OK", VERT];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const entete = `<div class="entete">${icone("developer_board", `color:${couleur}`)}<div class="nom">${esc(v.hostname ?? entite?.name ?? "Machine")}</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const alertes = [
            v.undervoltage_now ? [ROUGE, "Sous-tension en ce moment : alimentation trop faible"] : null,
            v.throttled_now && !v.undervoltage_now ? [ROUGE, "Processeur bridé en ce moment"] : null,
            !v.undervoltage_now && v.undervoltage_since_boot ? [ORANGE, "Sous-tension depuis le démarrage"] : null,
            !v.throttled_now && v.throttled_since_boot && !v.undervoltage_since_boot ? [ORANGE, "Bridage depuis le démarrage"] : null,
        ].filter(Boolean).map(([c, t]) => `<div class="alerte" style="background:${c}1f;color:${c}">${t}</div>`).join("");

        const ligne = (nom, valeur, texte, part, c) => typeof valeur !== "number" ? "" :
            `<div class="ligne"><span>${nom}</span><div class="jauge"><div style="width:${Math.min(100, Math.max(0, part))}%;background:${c}"></div></div><span class="valeur">${texte}</span></div>`;
        const pct = (nom, p) => ligne(nom, p, `${nombre(p, 0)} %`, p, couleurCharge(p / 100));

        const bas = [
            v.uptime_s != null ? `en marche depuis ${duree(v.uptime_s)}` : null,
            typeof v.load_1 === "number" ? `charge ${nombre(v.load_1, 2)}` : null,
            typeof v.disk_free_gb === "number" ? `${nombre(v.disk_free_gb, 1)} Go libres` : null,
        ].filter(Boolean).join(" · ");

        return `<div class="carte">${entete}${v.model ? `<div class="modele">${esc(v.model)}</div>` : ""}${alertes}
            <div class="lignes">
                ${ligne("Température", v.cpu_temperature, `${nombre(v.cpu_temperature, 1)} °C`, v.cpu_temperature / 85 * 100, couleurTemperature(v.cpu_temperature))}
                ${pct("Processeur", v.cpu_percent)}${pct("Mémoire", v.memory_percent)}${pct("Disque", v.disk_percent)}
            </div>
            ${bas ? `<div class="detail bas">${bas}</div>` : ""}</div>`;
    }
}

customElements.define("kv-raspberry", KvRaspberry);
