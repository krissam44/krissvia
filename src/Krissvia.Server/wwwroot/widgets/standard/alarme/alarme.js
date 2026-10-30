import { KvWidget, esc, absence, icone } from "../commun.js";

const VERT = "#66bb6a", BLEU = "#1e88e5", ORANGE = "#ffa726", ROUGE = "#e53935", GRIS = "#78909c";

/** L'aspect de chaque état publié par le cœur (sensor.maison_alarme, docs/ALARME.md). */
const ETATS = {
    disarmed: { libelle: "DÉSARMÉE", couleur: VERT, icone: "lock_open" },
    exit_delay: { libelle: "SORTIE", couleur: ORANGE, icone: "timer" },
    armed: { libelle: "ARMÉE", couleur: BLEU, icone: "lock" },
    entry_delay: { libelle: "ENTRÉE", couleur: ORANGE, icone: "timer" },
    triggered: { libelle: "DÉCLENCHÉE", couleur: ROUGE, icone: "notification_important" },
};

/** Le code tapé s'efface seul après ce délai : un clavier laissé à moitié composé ne doit pas attendre le suivant. */
const OUBLI_MS = 30_000;

/**
 * Carte d'alarme : l'état et ses décomptes, le capteur en cause, et un clavier pour armer ou désarmer AVEC son code
 * (commandes arm / disarm de l'entité). La carte ne décide rien : un code faux, un clavier bloqué, un armement refusé
 * (porte ouverte) reviennent du cœur avec leur raison, affichée telle quelle. Le code n'est jamais montré, seulement
 * le nombre de chiffres tapés.
 */
class KvAlarme extends KvWidget {
    static css = `
        .carte { gap: 6px; }
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .nom { font-weight: 600; font-size: .9rem; flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .entete .ms { font-size: 1.4rem; }
        .badge { padding: 2px 8px; border-radius: 6px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .etat { text-align: center; }
        .etat .grand { font-size: 1.6rem; font-weight: 600; }
        .declenchee .grand { animation: clignote 1s steps(2, start) infinite; }
        @keyframes clignote { to { visibility: hidden; } }
        .code { display: flex; justify-content: center; gap: 6px; min-height: 14px; margin: 2px 0; }
        .code span { width: 10px; height: 10px; border-radius: 50%; background: var(--kv-text, #222); }
        .code .vide { font-size: .72rem; color: var(--kv-text-secondary, #777); width: auto; height: auto; background: none; }
        /* Le pavé est le seul élément qui cède quand la carte manque de hauteur : un message de refus, une erreur ou un
           décompte en plus rétrécissent les touches (34 → 22 px) au lieu de pousser « Armer » hors de la carte. */
        .carte > .pave { flex: 0 1 auto; min-height: calc(4 * 22px + 3 * 5px); }   /* jamais une rangée sous le bouton */
        .pave { display: grid; grid-template-columns: repeat(3, 1fr); grid-auto-rows: minmax(22px, 34px); gap: 5px; }
        .etat .detail, .erreur { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; }
        .touche { padding: 0; min-height: 0; border-radius: 8px; font-size: 1rem; font-weight: 500; color: var(--kv-text, #222);
                  border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 30%, transparent); background: var(--kv-surface, transparent); }
        .touche:hover { background: color-mix(in srgb, var(--kv-text-secondary, #888) 12%, transparent); }
        .touche .ms { font-size: 1.1rem; }
        .action { margin-top: auto; width: 100%; padding: 8px 0; border: 0; border-radius: 8px; font-weight: 700; font-size: .85rem; color: #fff;
                  display: flex; align-items: center; justify-content: center; gap: 6px; }
        .action[disabled], .touche[disabled] { opacity: .5; cursor: progress; }
        .erreur { text-align: center; }`;

    #code = "";
    #attente = false;
    #erreur = null;
    #oubli = null;
    #ombre = null;

    rendu() {
        const entite = this._entities.alarme;
        const etat = this._states.alarme;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const manque = absence(etat, entite);
        const aspect = manque ? { libelle: "", couleur: GRIS, icone: "shield" } : ETATS[v.state] ?? { libelle: String(v.state ?? "?"), couleur: GRIS, icone: "shield" };

        const badge = aspect.libelle ? `<span class="badge" style="background:${aspect.couleur}22;color:${aspect.couleur};border:1px solid ${aspect.couleur}44">${aspect.libelle}</span>` : "";
        const entete = `<div class="entete">${icone(aspect.icone, `color:${aspect.couleur}`)}<div class="nom">${esc(entite?.name ?? "Alarme")}</div>${badge}</div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const desarmee = v.state === "disarmed";
        const occupe = this.#attente ? " disabled" : "";
        const touches = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "effacer", "0", "retour"].map(t =>
            t === "effacer" ? `<button class="touche" data-touche="effacer" title="Tout effacer"${occupe}>C</button>`
            : t === "retour" ? `<button class="touche" data-touche="retour" title="Effacer un chiffre"${occupe}>${icone("backspace")}</button>`
            : `<button class="touche" data-touche="${t}"${occupe}>${t}</button>`).join("");
        const points = this.#points();
        const action = desarmee
            ? `<button class="action" data-commande="arm" style="background:${BLEU}"${occupe}>${icone("lock")}Armer</button>`
            : `<button class="action" data-commande="disarm" style="background:${ROUGE}"${occupe}>${icone("lock_open")}Désarmer</button>`;

        // Le bouton vient en dernier : seul le pavé cède de la hauteur, le bouton reste au bas de la carte.
        return `<div class="carte">${entete}${this.#etat(v, aspect)}
            <div class="code" aria-label="${this.#code.length} chiffre(s) tapé(s)">${points}</div>
            <div class="pave">${touches}</div>${action}</div>`;
    }

    #points() {
        return this.#code.length > 0 ? "<span></span>".repeat(this.#code.length) : `<span class="vide">Tapez votre code</span>`;
    }

    /**
     * La ligne d'état : décompte, capteur en cause et heure du déclenchement, ou dernier refus d'armement. Une erreur
     * (code faux, armement refusé) PREND LA PLACE de la ligne de détail au lieu de s'y ajouter — elle répète souvent le
     * dernier refus, et chaque ligne en plus se paie en hauteur de touches. Le grand décompte, lui, reste.
     */
    #etat(v, aspect) {
        const heure = i => new Date(i).toLocaleString("fr-FR", { weekday: "short", hour: "2-digit", minute: "2-digit" });
        const detail = (html, style = "") => this.#erreur
            ? `<div class="erreur" title="${esc(this.#erreur)}">${esc(this.#erreur)}</div>`
            : `<div class="detail"${style}>${html}</div>`;
        switch (v.state) {
            case "exit_delay":
                return `<div class="etat"><div class="grand" style="color:${aspect.couleur}">${v.countdown} s</div>${detail("Armement : sortez et refermez")}</div>`;
            case "entry_delay":
                return `<div class="etat"><div class="grand" style="color:${aspect.couleur}">${v.countdown} s</div>${detail(`${esc(v.sensor ?? "")} : tapez votre code`)}</div>`;
            case "triggered":
                return `<div class="etat declenchee"><div class="grand" style="color:${aspect.couleur}">Intrusion</div>
                    ${detail(`${esc(v.sensor ?? "")}${v.triggered_at ? `, ${heure(v.triggered_at)}` : ""}${v.ringing ? " — sirène" : ""}`)}</div>`;
            case "armed":
                return `<div class="etat">${detail("Maison sous surveillance")}</div>`;
            default:
                return v.faults && v.faults !== "OK"
                    ? `<div class="etat">${detail(`Dernier refus : ${esc(v.faults)}`, ` style="color:${ORANGE}" title="${esc(v.faults)}"`)}</div>`
                    : `<div class="etat">${detail("Prête à être armée")}</div>`;
        }
    }

    /**
     * Touches sur pointerdown, pas click : la carte se redessine à chaque état reçu (chaque seconde d'un décompte), et un
     * clic dont l'appui et le relâchement encadrent un redessin est perdu — un chiffre tapé au mauvais moment disparaît.
     */
    apresRendu(racine) {
        this.#ombre = racine;
        racine.querySelectorAll(".touche").forEach(b => b.addEventListener("pointerdown", e => { e.preventDefault(); this.#taper(b.dataset.touche); }));
        racine.querySelector(".action")?.addEventListener("pointerdown", e => { e.preventDefault(); this.#ordonner(e.currentTarget.dataset.commande); });
    }

    #taper(touche) {
        if (touche === "effacer") this.#code = "";
        else if (touche === "retour") this.#code = this.#code.slice(0, -1);
        else if (this.#code.length < 12) this.#code += touche;
        clearTimeout(this.#oubli);
        this.#oubli = setTimeout(() => { this.#code = ""; this.#erreur = null; this.planifier(); }, OUBLI_MS);

        // Un chiffre ne redessine que les points : rien ne bouge sous le doigt pendant qu'on tape. L'erreur reste donc
        // affichée jusqu'à l'ordre suivant — l'effacer ici changerait la hauteur au-dessus du pavé.
        const code = this.#ombre?.querySelector(".code");
        if (code) {
            code.innerHTML = this.#points();
            code.setAttribute("aria-label", `${this.#code.length} chiffre(s) tapé(s)`);
        } else {
            this.planifier();
        }
    }

    async #ordonner(commande) {
        if (!this._kv) return;
        if (this.#code.length === 0) {
            this.#erreur = "Tapez d'abord votre code.";
            this.planifier();
            return;
        }

        const code = this.#code;
        this.#code = "";   // effacé avant l'envoi : un code ne reste pas à l'écran, bon ou faux
        clearTimeout(this.#oubli);
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        try {
            const r = await this._kv.command("alarme", commande, { code });
            if (r.outcome !== "accepted") this.#erreur = majuscule(r.detail ?? "Ordre refusé.");
        } catch {
            this.#erreur = "Serveur injoignable.";
        }
        this.#attente = false;
        this.planifier();
    }
}

function majuscule(texte) {
    return texte.charAt(0).toUpperCase() + texte.slice(1);
}

customElements.define("kv-alarme", KvAlarme);
