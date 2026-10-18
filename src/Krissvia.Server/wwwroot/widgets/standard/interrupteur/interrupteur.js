import { KvWidget, esc, depuis, absence } from "../commun.js";

/**
 * Le bouton n'affiche jamais « allumé » parce qu'on a cliqué : il attend l'état publié par l'appareil
 * (WIDGETS.md §5). Pendant l'attente, il est grisé ; un refus s'affiche sous le bouton.
 */
class KvInterrupteur extends KvWidget {
    static css = `
        .bouton { margin-top: auto; align-self: flex-start; border: 0; border-radius: 22px; padding: 8px 18px; font-weight: 500;
                  background: var(--kv-surface-2, rgba(127,127,127,.15)); color: var(--kv-text, #222); min-width: 96px; }
        .bouton.allume { background: var(--kv-warning, #ffa726); color: #fff; }
        .bouton[disabled] { opacity: .55; cursor: progress; }`;

    #attente = false;
    #erreur = null;

    allume() {
        const v = this._states.interrupteur?.value;
        return typeof v === "boolean" ? v : typeof v?.on === "boolean" ? v.on : null;
    }

    set states(v) {
        // Un nouvel état met fin à l'attente : c'est lui qui fait foi.
        this.#attente = false;
        super.states = v;
    }

    rendu() {
        const entite = this._entities.interrupteur;
        const etat = this._states.interrupteur;
        const manque = absence(etat, entite);
        const allume = this.allume();
        return `<div class="carte">
            <div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
            ${manque ?? `<div class="detail">${allume ? "Allumé" : "Éteint"} · ${esc(depuis(etat.measured_at))}</div>`}
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
            <button class="bouton ${allume ? "allume" : ""}" ${manque || this.#attente ? "disabled" : ""}>
                ${this.#attente ? "…" : allume ? "Éteindre" : "Allumer"}
            </button>
        </div>`;
    }

    apresRendu(racine) {
        racine.querySelector(".bouton")?.addEventListener("click", () => this.basculer());
    }

    async basculer() {
        if (!this._kv) return;
        const allumer = !this.allume();
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        const r = await this._kv.command("interrupteur", allumer ? "turn_on" : "turn_off");
        if (r.outcome !== "accepted") this.#erreur = r.detail ?? "Ordre refusé.";
        else if (r.verification === "not_confirmed") this.#erreur = "L'appareil n'a pas confirmé.";
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-interrupteur", KvInterrupteur);
