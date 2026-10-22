import { KvWidget, esc, absence, icone, couleurCharge, nombre } from "/widgets/standard/commun.js";

const VERT = "#66bb6a", GRIS = "#78909c", ROUGE = "#ef5350";

/** « 3 j 4 h », « 5 h 12 min », « 12 min ». */
function duree(secondes) {
    if (typeof secondes !== "number") return "";
    const j = Math.floor(secondes / 86400), h = Math.floor(secondes % 86400 / 3600), m = Math.floor(secondes % 3600 / 60);
    return j > 0 ? `${j} j ${h} h` : h > 0 ? `${h} h ${m} min` : `${m} min`;
}

function taille(octets) {
    if (typeof octets !== "number" || octets <= 0) return "";
    const go = octets / 1024 ** 3;
    return go >= 1 ? `${nombre(go, 1)} Go` : `${Math.round(octets / 1024 ** 2)} Mo`;
}

/**
 * Carte « Machine virtuelle », livrée par le plugin Proxmox (WIDGETS.md §13) : l'état et les ressources, les ordres
 * (un arrêt ou un redémarrage se confirme : ce n'est pas une lampe), et les deux réglages de la machine — surveillée
 * (alerte si elle s'arrête) et relancée automatiquement. La carte n'affiche pas « en marche » parce qu'elle a cliqué :
 * elle attend le relevé suivant.
 */
class KvProxmoxVm extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .sous { font-size: .7rem; color: var(--kv-text-secondary, #777); }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .alerte { font-size: .75rem; font-weight: 600; padding: 4px 8px; border-radius: 6px; background: ${ROUGE}1f; color: ${ROUGE}; }
        .ligne { display: grid; grid-template-columns: 80px 1fr 64px; align-items: center; gap: 8px; font-size: .78rem; }
        .ligne .valeur { text-align: right; font-variant-numeric: tabular-nums; }
        .jauge { height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); overflow: hidden; }
        .jauge > div { height: 100%; border-radius: 3px; }
        .lignes { display: flex; flex-direction: column; gap: 5px; margin: 4px 0; }
        .actions { display: flex; gap: 6px; flex-wrap: wrap; }
        .actions button { flex: 1; min-width: 70px; padding: 5px 4px; border-radius: 7px; font-size: .75rem; font-weight: 600; display: inline-flex;
                          align-items: center; justify-content: center; gap: 4px; background: var(--kv-surface, transparent); color: var(--kv-text, #222);
                          border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 35%, transparent); }
        .actions button .ms { font-size: 1rem; }
        .actions button[disabled] { opacity: .45; cursor: default; }
        .reglages { display: flex; gap: 12px; font-size: .74rem; color: var(--kv-text-secondary, #777); }
        .reglages label { display: inline-flex; align-items: center; gap: 4px; cursor: pointer; }`;

    #attente = false;
    #erreur = null;

    rendu() {
        const entite = this._entities.machine;
        const etat = this._states.machine;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);

        const enMarche = v.status === "running";
        const [libelle, couleur] = manque ? ["", GRIS] : v.unexpected_stop ? ["ARRÊT INATTENDU", ROUGE] : enMarche ? ["EN MARCHE", VERT] : ["ARRÊTÉE", GRIS];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const type = v.type === "lxc" ? "LXC" : "VM";
        const entete = `<div class="entete">${icone(v.type === "lxc" ? "deployed_code" : "dns", `color:${couleur}`)}<div class="nom">${esc(v.name ?? entite?.name ?? "Machine")}</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const ligne = (nom, p, texte) => typeof p !== "number" ? "" :
            `<div class="ligne"><span>${nom}</span><div class="jauge"><div style="width:${Math.min(100, p)}%;background:${couleurCharge(p / 100)}"></div></div><span class="valeur">${texte}</span></div>`;
        const lignes = enMarche ? [
            ligne("Processeur", v.cpu_percent, `${nombre(v.cpu_percent, 0)} %`),
            ligne("Mémoire", v.memory_percent, `${nombre(v.memory_percent, 0)} %`),
            ligne("Disque", v.disk_percent, `${nombre(v.disk_percent, 0)} %`),
        ].join("") : "";

        const occupe = this.#attente ? " disabled" : "";
        const bouton = (commande, ic, texte, actif) => `<button data-commande="${commande}"${actif ? occupe : " disabled"}>${icone(ic)}${texte}</button>`;
        const case_ = (commande, coche, texte) => `<label><input type="checkbox" data-reglage="${commande}"${coche ? " checked" : ""}${occupe}>${texte}</label>`;

        return `<div class="carte">${entete}
            <div class="sous">${type} ${esc(v.vmid)} sur ${esc(v.node ?? "?")}${v.max_cpu ? ` · ${v.max_cpu} cœur(s)` : ""}${v.max_memory_bytes ? ` · ${taille(v.max_memory_bytes)}` : ""}</div>
            ${v.unexpected_stop ? `<div class="alerte">Arrêtée sans qu'on le demande${v.auto_restart ? " — relance automatique abandonnée" : ""}</div>` : ""}
            <div class="lignes">${lignes}</div>
            ${enMarche && v.uptime_s != null ? `<div class="detail">en marche depuis ${duree(v.uptime_s)}</div>` : ""}
            <div class="actions bas">
                ${bouton("start", "play_arrow", "Démarrer", !enMarche)}
                ${bouton("shutdown", "power_settings_new", "Arrêter", enMarche)}
                ${bouton("reboot", "restart_alt", "Redémarrer", enMarche)}
            </div>
            <div class="reglages">${case_("set_tracked", v.tracked, "Surveillée")}${case_("set_auto_restart", v.auto_restart, "Relance auto")}</div>
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}</div>`;
    }

    apresRendu(racine) {
        const nom = this._states.machine?.value?.name ?? "cette machine";
        racine.querySelectorAll("button[data-commande]").forEach(b => b.addEventListener("click", () => {
            const c = b.dataset.commande;
            if (c !== "start" && !confirm(`${c === "shutdown" ? "Arrêter" : "Redémarrer"} « ${nom} » ?`)) return;
            this.#ordonner(c);
        }));
        racine.querySelectorAll("input[data-reglage]").forEach(i => i.addEventListener("change", () =>
            this.#ordonner(i.dataset.reglage, { enabled: i.checked })));
    }

    async #ordonner(commande, args) {
        if (!this._kv) return;
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        try {
            const r = await this._kv.command("machine", commande, args);
            if (r.outcome !== "accepted") this.#erreur = r.detail ?? "Ordre refusé.";
        } catch {
            this.#erreur = "Serveur injoignable.";
        }
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-proxmox-vm", KvProxmoxVm);
