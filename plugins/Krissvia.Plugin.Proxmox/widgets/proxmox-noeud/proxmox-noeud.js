import { KvWidget, esc, absence, icone, couleurCharge, nombre } from "/widgets/standard/commun.js";

const VERT = "#66bb6a", ROUGE = "#ef5350";
const BLEU = "#42a5f5", VIOLET = "#ab47bc", ORANGE = "#ffa726";

/** « 55 j 11 h », « 5 h 12 min », « 12 min ». */
function duree(secondes) {
    if (typeof secondes !== "number") return "";
    const j = Math.floor(secondes / 86400), h = Math.floor(secondes % 86400 / 3600), m = Math.floor(secondes % 3600 / 60);
    return j > 0 ? `${j} j ${h} h` : h > 0 ? `${h} h ${m} min` : `${m} min`;
}

function taille(octets) {
    if (typeof octets !== "number" || octets <= 0) return "—";
    const go = octets / 1024 ** 3;
    return go >= 1 ? `${nombre(go, 1)} Go` : `${Math.round(octets / 1024 ** 2)} Mo`;
}

/**
 * Carte « Nœud Proxmox », livrée par le plugin (WIDGETS.md §13) : trois tuiles comme l'en-tête de la page Proxmox de
 * DomoMud — processeur, mémoire, disque système, chacune avec son pourcentage, sa jauge et le détail (cœurs, utilisé
 * / total). La couleur du chiffre est celle de la ressource ; celle de la jauge vire à l'orange puis au rouge quand
 * elle se remplit. Hors ligne, pas de pourcentage : un nœud injoignable n'a pas de mesure, pas « 0 % ».
 */
class KvProxmoxNoeud extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: .95rem; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .infos { font-size: .74rem; color: var(--kv-text-secondary, #777); margin-left: auto; }
        .tuiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px 20px; margin-top: 6px; }
        .tuile { display: flex; flex-direction: column; align-items: center; gap: 2px; }
        .tuile .pc { font-size: 1.7rem; font-weight: 600; line-height: 1.1; font-variant-numeric: tabular-nums; }
        .tuile .lib { font-size: .68rem; letter-spacing: .06em; text-transform: uppercase; color: var(--kv-text-secondary, #777); }
        .tuile .jauge { width: 100%; height: 4px; border-radius: 2px; margin-top: 4px; overflow: hidden;
                        background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); }
        .tuile .jauge > div { height: 100%; border-radius: 2px; }
        .tuile .sous { font-size: .7rem; color: var(--kv-text-secondary, #777); }`;

    rendu() {
        const entite = this._entities.noeud;
        const etat = this._states.noeud;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);

        const [libelle, couleur] = manque ? ["", "#78909c"] : v.online ? ["EN LIGNE", VERT] : ["HORS LIGNE", ROUGE];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const infos = manque || !v.online ? "" : [
            v.uptime_s != null ? `en marche depuis ${duree(v.uptime_s)}` : "",
            typeof v.guests_total === "number" ? `${v.guests_running} / ${v.guests_total} machine(s) en marche` : "",
        ].filter(Boolean).join(" · ");
        const entete = `<div class="entete">${icone("dns", `color:${couleur}`)}<span class="nom">${esc(v.name ?? entite?.name ?? "Nœud")}</span>${badge}<span class="infos">${infos}</span></div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;
        if (!v.online) return `<div class="carte">${entete}<div class="absent">Le nœud ne répond pas : pas de mesure.</div></div>`;

        const tuile = (p, nom, teinte, sous) => `<div class="tuile">
            <span class="pc" style="color:${teinte}">${typeof p === "number" ? `${nombre(p, 0)} %` : "—"}</span>
            <span class="lib">${nom}</span>
            <div class="jauge"><div style="width:${typeof p === "number" ? Math.min(100, p) : 0}%;background:${typeof p === "number" ? couleurCharge(p / 100) : "transparent"}"></div></div>
            <span class="sous">${sous}</span></div>`;

        return `<div class="carte">${entete}
            <div class="tuiles">
                ${tuile(v.cpu_percent, "Processeur", BLEU, v.max_cpu ? `${v.max_cpu} cœurs` : "")}
                ${tuile(v.memory_percent, "Mémoire", VIOLET, `${taille(v.memory_bytes)} / ${taille(v.max_memory_bytes)}`)}
                ${tuile(v.disk_percent, "Disque système", ORANGE, `${taille(v.disk_bytes)} / ${taille(v.max_disk_bytes)}`)}
            </div></div>`;
    }
}

customElements.define("kv-proxmox-noeud", KvProxmoxNoeud);
