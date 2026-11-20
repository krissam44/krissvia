import { KvWidget, esc, absence, icone, depuis } from "/widgets/standard/commun.js";

const VERT = "#66bb6a", GRIS = "#78909c", ROUGE = "#ef5350", BLEU = "#42a5f5";

/**
 * Carte « Matter », livrée par le plugin Matter (WIDGETS.md §13) : le serveur Matter est-il joignable, quels
 * appareils il connaît, et l'appairage d'un nouvel appareil à partir de son code d'appairage (11 chiffres, ou le
 * texte « MT:… » du QR code). Le code tapé est gardé entre deux rafraîchissements de la carte.
 */
class KvMatter extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .noeuds { font-size: .78rem; display: flex; flex-direction: column; gap: 2px; max-height: 6.5em; overflow: auto; }
        .noeuds span.hs { color: ${ROUGE}; }
        .appairer { display: flex; gap: 6px; }
        .appairer input { flex: 1; min-width: 0; padding: 5px 8px; border-radius: 7px; font: inherit; font-size: .8rem;
                          border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 35%, transparent);
                          background: var(--kv-surface, transparent); color: var(--kv-text, #222); }
        .appairer button { padding: 5px 10px; border-radius: 7px; font-size: .75rem; font-weight: 600; border: 0;
                           background: ${BLEU}; color: #fff; display: inline-flex; align-items: center; gap: 4px; }
        .appairer button[disabled] { opacity: .45; cursor: default; }
        .bt { font-size: .72rem; display: flex; align-items: center; gap: 4px; }
        .ok { font-size: .75rem; color: ${VERT}; }`;

    #code = "";
    #bluetooth = false;
    #erreur = null;
    #envoi = false;

    rendu() {
        const entite = this._entities.controleur;
        const etat = this._states.controleur;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);
        const [libelle, couleur] = manque ? ["", GRIS] : v.connected ? ["CONNECTÉ", VERT] : ["SERVEUR INJOIGNABLE", ROUGE];
        const badge = libelle ? `<span class="badge" style="background:${couleur}22;color:${couleur};border:1px solid ${couleur}44">${libelle}</span>` : "";
        const entete = `<div class="entete">${icone("hub", `color:${couleur}`)}<div class="nom">Matter</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const noeuds = Array.isArray(v.nodes) ? v.nodes : [];
        const liste = noeuds.length
            ? noeuds.map(n => `<span class="${n.available ? "" : "hs"}">${esc(n.name)}${n.available ? "" : " — injoignable"}</span>`).join("")
            : `<span class="detail">Aucun appareil appairé.</span>`;

        const a = v.commissioning ?? {};
        const enCours = a.state === "running";
        const suivi = enCours ? `<div class="detail">Appairage en cours… (jusqu'à 3 minutes)</div>`
            : a.state === "done" ? `<div class="ok">« ${esc(a.name ?? "?")} » appairé ${esc(depuis(a.at))}.</div>`
            : a.state === "failed" ? `<div class="erreur">Appairage échoué : ${esc(a.detail ?? "")}</div>` : "";
        const btPossible = v.bluetooth_enabled && v.wifi_credentials_set;
        const actif = v.connected && !enCours && !this.#envoi;

        return `<div class="carte">${entete}
            <div class="noeuds">${liste}</div>
            <div class="appairer bas">
                <input type="text" inputmode="text" placeholder="Code d'appairage (11 chiffres ou MT:…)" value="${esc(this.#code)}"${actif ? "" : " disabled"}>
                <button${actif && this.#code.trim() ? "" : " disabled"}>${icone("add_link")}Appairer</button>
            </div>
            ${btPossible ? `<label class="bt"><input type="checkbox"${this.#bluetooth ? " checked" : ""}> Appareil neuf, par Bluetooth</label>` : ""}
            ${suivi}
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}</div>`;
    }

    apresRendu(racine) {
        const champ = racine.querySelector(".appairer input");
        const bouton = racine.querySelector(".appairer button");
        champ?.addEventListener("input", () => {
            this.#code = champ.value;
            if (bouton) bouton.disabled = !this.#code.trim();
        });
        champ?.addEventListener("keydown", e => { if (e.key === "Enter" && this.#code.trim()) this.appairer(); });
        bouton?.addEventListener("click", () => this.appairer());
        racine.querySelector(".bt input")?.addEventListener("change", e => { this.#bluetooth = e.target.checked; });
    }

    async appairer() {
        if (!this._kv || this.#envoi) return;
        this.#envoi = true;
        this.#erreur = null;
        this.planifier();
        const r = await this._kv.command("controleur", "commission", { code: this.#code.trim(), bluetooth: this.#bluetooth });
        this.#envoi = false;
        if (r.outcome === "accepted") this.#code = "";
        else this.#erreur = r.detail ?? "Appairage refusé.";
        this.planifier();
    }
}

customElements.define("kv-matter", KvMatter);
