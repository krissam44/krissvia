import { KvWidget, esc, absence, icone, depuis } from "/widgets/standard/commun.js";

const VERT = "#66bb6a", GRIS = "#78909c", ROUGE = "#ef5350", BLEU = "#42a5f5";

/**
 * Carte « Platine openHASP », livrée par le plugin (WIDGETS.md §13) : l'état de l'écran mural et ses deux gestes de
 * dépannage. Repeindre efface la platine et lui renvoie toutes ses pages (le cas d'un écran resté sur l'ancien
 * affichage de sa mémoire) ; Redémarrer relance l'ESP32, qui se repeint de lui-même à son retour. Le redémarrage
 * demande confirmation : l'écran reste noir une quinzaine de secondes.
 */
class KvPlatine extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .actions { display: flex; gap: 6px; flex-wrap: wrap; }
        .actions button { flex: 1; min-width: 90px; padding: 5px 4px; border-radius: 7px; font-size: .75rem; font-weight: 600; display: inline-flex;
                          align-items: center; justify-content: center; gap: 4px; background: var(--kv-surface, transparent); color: var(--kv-text, #222);
                          border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 35%, transparent); }
        .actions button .ms { font-size: 1rem; }
        .actions button[disabled] { opacity: .45; cursor: default; }
        .ok { font-size: .75rem; color: ${VERT}; }`;

    #attente = false;
    #message = null;
    #erreur = null;

    rendu() {
        const entite = this._entities.ecran;
        const etat = this._states.ecran;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);
        const [libelle, couleur] = manque ? ["", GRIS] : !v.online ? ["HORS LIGNE", ROUGE] : v.asleep ? ["EN VEILLE", BLEU] : ["EN LIGNE", VERT];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const nom = entite?.name && v.node && !entite.name.startsWith(`Platine ${v.node}`) ? entite.name : `Platine ${v.node ?? ""}`.trim();
        const entete = `<div class="entete">${icone("tablet", `color:${couleur}`)}<div class="nom">${esc(nom)}</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const detail = [
            typeof v.pages === "number" ? `${v.pages} page(s)` : "",
            v.last_paint ? `peinte ${depuis(v.last_paint)}` : "pas encore peinte",
        ].filter(Boolean).join(" · ");
        const actif = v.online && !this.#attente;
        return `<div class="carte">${entete}
            <div class="detail">${esc(detail)}</div>
            <div class="actions bas">
                <button data-commande="repaint"${actif ? "" : " disabled"}>${icone("format_paint")}Repeindre</button>
                <button data-commande="reboot"${actif ? "" : " disabled"}>${icone("restart_alt")}Redémarrer</button>
            </div>
            ${this.#message ? `<div class="ok">${esc(this.#message)}</div>` : ""}
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}</div>`;
    }

    apresRendu(racine) {
        const noeud = this._states.ecran?.value?.node ?? "cette platine";
        racine.querySelectorAll("button[data-commande]").forEach(b => b.addEventListener("click", () => {
            const c = b.dataset.commande;
            if (c === "reboot" && !confirm(`Redémarrer la platine ${noeud} ? Son écran restera noir une quinzaine de secondes.`)) return;
            this.#ordonner(c);
        }));
    }

    async #ordonner(commande) {
        if (!this._kv) return;
        this.#attente = true;
        this.#message = this.#erreur = null;
        this.planifier();
        try {
            const r = await this._kv.command("ecran", commande);
            if (r.outcome === "accepted") this.#message = commande === "repaint" ? "Pages renvoyées." : "Redémarrage demandé.";
            else this.#erreur = r.detail ?? "Ordre refusé.";
        } catch {
            this.#erreur = "Serveur injoignable.";
        }
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-platine", KvPlatine);
