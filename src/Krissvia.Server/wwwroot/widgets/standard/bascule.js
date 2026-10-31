import { KvWidget, esc, depuis, absence, icone, ConfirmationEnCarte } from "./commun.js";

/**
 * Base des cartes tout-ou-rien qu'on commande (interrupteur, radiateur, éclairage) : seul leur aspect change.
 * Le bouton n'affiche jamais « allumé » parce qu'on a cliqué : il attend l'état publié par l'appareil
 * (WIDGETS.md §5). Pendant l'attente, il est grisé ; un refus s'affiche sous le bouton.
 */
export class KvBascule extends KvWidget {
    /** Ce qui distingue chaque carte : icône allumée/éteinte, couleur, libellés de l'état et du bouton. */
    static aspect = {
        allume: "toggle_on", eteint: "toggle_off", couleur: "#ffa726", texte: "#fff",
        etatAllume: "Allumé", etatEteint: "Éteint", allumer: "Allumer", eteindre: "Éteindre",
    };

    /**
     * Vrai : éteindre demande un second clic (« Couper ? »), comme les gestes lourds des cartes de plugin. Pour ce
     * qui prive tout un circuit d'un coup — un disjoncteur —, pas pour une lampe.
     */
    static confirmerExtinction = false;

    static css = `
        .tete { display: flex; align-items: center; gap: 8px; min-width: 0; }
        .tete .ms { font-size: 22px; color: var(--kv-text-secondary, #777); opacity: .7; }
        .tete .titre { flex: 1; }
        .bouton { margin-top: auto; align-self: flex-start; border: 0; border-radius: 22px; padding: 8px 18px; font-weight: 500;
                  background: var(--kv-surface-2, rgba(127,127,127,.15)); color: var(--kv-text, #222); min-width: 96px; }
        .bouton[disabled] { opacity: .55; cursor: progress; }`;

    #attente = false;
    #erreur = null;
    #confirmation = new ConfirmationEnCarte(() => this.planifier());

    /** Un complément à la ligne d'état (la puissance d'un disjoncteur) ; vide par défaut. */
    complement() { return ""; }

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
        // Les cartes ne redéfinissent que ce qui les distingue : le reste (libellés du bouton) vient de la base.
        const a = { ...KvBascule.aspect, ...this.constructor.aspect };
        const entite = this._entities.interrupteur;
        const etat = this._states.interrupteur;
        const manque = absence(etat, entite);
        const allume = !manque && this.allume();
        const symbole = allume
            ? icone(a.allume, `color:${a.couleur};opacity:1;font-variation-settings:'FILL' 1;filter:drop-shadow(0 0 6px ${a.couleur}99)`)
            : icone(a.eteint);
        return `<div class="carte">
            <div class="tete">${symbole}<div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div></div>
            ${manque ?? `<div class="detail">${allume ? a.etatAllume : a.etatEteint} · ${esc(depuis(etat.measured_at))}${this.complement()}</div>`}
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
            <button class="bouton" ${allume ? `style="background:${a.couleur};color:${a.texte}"` : ""} ${manque || this.#attente ? "disabled" : ""}>
                ${this.#attente ? "…" : !allume ? a.allumer : this.#confirmation.attendue ? `${a.eteindre} ?` : a.eteindre}
            </button>
        </div>`;
    }

    apresRendu(racine) {
        racine.querySelector(".bouton")?.addEventListener("click", () => this.basculer());
    }

    async basculer() {
        if (!this._kv) return;
        const allumer = !this.allume();
        if (!allumer && this.constructor.confirmerExtinction && !this.#confirmation.demander("eteindre")) return;
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
