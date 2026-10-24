import { KvWidget, esc, nombre, absence } from "../commun.js";

// Les six modes de Krissvia, dans l'ordre de la rangée (du plus chaud à l'arrêt) ; libellés tirés de l'entité.
const MODES = [
    { id: "comfort", icone: "local_fire_department", couleur: "#ff7a00" },
    { id: "comfort1", icone: "mode_heat", couleur: "#ffa726" },
    { id: "comfort2", icone: "heat", couleur: "#f4b400" },
    { id: "eco", icone: "eco", couleur: "#43a047" },
    { id: "frost_protection", icone: "ac_unit", couleur: "#42a5f5" },
    { id: "off", icone: "power_settings_new", couleur: "#78909c" },
];
const INCONNU = { icone: "thermostat", couleur: "#78909c" };
const PLANNING = "#00b4d8";

// L'arc : 270° ouverts à droite, comme Gladys. Le minimum est en bas à droite (−), le maximum en haut à droite (+).
const DEBUT = 45, ETENDUE = 270, R = 84, CX = 110, CY = 110;
// Le cadre du dessin, serré sur l'anneau et les boutons : pas de marge vide qui volerait de la hauteur à la roue.
const VUE = "16 16 194 194";
// Les boutons − / + dans l'ouverture de l'arc, près de ses deux bouts. Dessinés DANS le SVG, ils suivent sa taille.
const BOUTONS = [{ pas: -1, angle: 22, signe: "−", nom: "Baisser la consigne" }, { pas: 1, angle: -22, signe: "+", nom: "Augmenter la consigne" }];

const ms = (nom, style = "") => `<span class="ms" aria-hidden="true"${style ? ` style="${style}"` : ""}>${nom}</span>`;

/** Le point de l'arc à un angle donné (degrés, 0 = à droite, sens des aiguilles d'une montre : y vers le bas en SVG). */
function point(angle) {
    const a = angle * Math.PI / 180;
    return [CX + R * Math.cos(a), CY + R * Math.sin(a)];
}

/** Un arc SVG d'un angle à un autre, dans le sens des aiguilles d'une montre. */
function arc(de, a) {
    const [x1, y1] = point(de), [x2, y2] = point(a);
    return `M ${x1.toFixed(1)} ${y1.toFixed(1)} A ${R} ${R} 0 ${a - de > 180 ? 1 : 0} 1 ${x2.toFixed(1)} ${y2.toFixed(1)}`;
}

/**
 * Thermostat en cadran, à la manière de Gladys : la consigne est un curseur sur un arc — on le fait glisser (l'ordre part
 * au lâcher, en valeur exacte), ou on la règle par − / + (pas relatif, calculé par le cœur : deux clics font deux pas).
 * Dans la roue, la consigne seule et la flamme quand il chauffe ; à sa droite l'ambiante et l'humidité. Dessous le mode
 * « jusqu'à » la prochaine bascule du planning, ou « en manuel » ; en haut à droite, le planning ; en bas, les six modes.
 * Consigne, flamme et boutons sont dessinés dans le SVG : ils grandissent avec la roue, sans calcul de position.
 * Comme les autres cartes, rien n'est affiché parce qu'on a cliqué : la carte attend l'état suivant.
 */
class KvThermostatCadran extends KvWidget {
    static historique = "thermostat";

    static css = `
        .carte { padding: 10px 12px 8px; gap: 4px; }
        .entete { display: flex; align-items: center; gap: 6px; }
        .entete .nom { flex: 1; min-width: 0; font-size: .74rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase;
                       color: var(--kv-text-secondary, #777); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .icone-btn { border: 0; background: transparent; padding: 3px; border-radius: 50%; display: inline-flex; color: var(--kv-text-secondary, #777); }
        .icone-btn .ms { font-size: 1.2rem; }
        .icone-btn:hover { background: color-mix(in srgb, currentColor 12%, transparent); }
        .cadran { flex: 1 1 auto; min-height: 110px; display: flex; align-items: center; justify-content: center; gap: 8px; }
        .cadran svg { height: 100%; max-height: 260px; aspect-ratio: 1; width: auto; max-width: 72%; min-width: 0; touch-action: none; }
        .fond { fill: none; stroke: color-mix(in srgb, var(--kv-text-secondary, #888) 22%, transparent); stroke-width: 12; stroke-linecap: round; }
        .rempli { fill: none; stroke-width: 12; stroke-linecap: round; }
        .curseur { fill: var(--kv-surface, #fff); stroke-width: 4; cursor: grab; }
        .glisse .curseur { cursor: grabbing; }
        .entier { font-size: 62px; font-weight: 300; letter-spacing: -.02em; fill: var(--kv-text, #222); }
        .cote { font-size: 17px; fill: var(--kv-text, #222); }
        .flamme { font-family: 'Material Symbols Outlined'; font-size: 26px; fill: #ff7a00; filter: drop-shadow(0 0 4px #ff7a0088); }
        .pas { cursor: pointer; outline: none; }
        .pas circle { fill: var(--kv-surface, #fff); stroke: color-mix(in srgb, var(--kv-text-secondary, #888) 45%, transparent); stroke-width: 1.5; }
        .pas text { font-size: 20px; fill: var(--kv-text, #222); pointer-events: none; }
        .pas:hover circle, .pas:focus-visible circle { stroke: var(--kv-text, #222); }
        .pas.occupe { opacity: .45; cursor: progress; }
        .mesures { flex: 0 0 auto; display: flex; flex-direction: column; gap: 4px; }
        .ambiante { font-size: 1.15rem; font-weight: 600; white-space: nowrap; }
        .ambiante .ms { font-size: 1.15rem; color: var(--kv-text-secondary, #777); vertical-align: -3px; }
        .humidite { font-size: .85rem; color: var(--kv-text-secondary, #777); white-space: nowrap; }
        .humidite .ms { font-size: .95rem; color: #42a5f5; vertical-align: -2px; }
        .statut { align-self: center; display: inline-flex; align-items: center; gap: 5px; padding: 3px 12px; border-radius: 18px; font-size: .78rem; }
        .statut .ms { font-size: 1rem; }
        .statut b { font-weight: 600; }
        .modes { display: flex; justify-content: space-between; gap: 2px; }
        .mode { flex: 1; min-width: 0; border: 0; background: transparent; color: var(--kv-text-secondary, #666); padding: 2px 0;
                display: flex; flex-direction: column; align-items: center; gap: 2px; font: inherit; font-size: .64rem; border-radius: 8px; }
        .mode .ms { font-size: 1.25rem; }
        .mode .lib { max-width: 100%; box-sizing: border-box; padding: 0 5px; border-radius: 8px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .mode.actif .lib { font-weight: 700; color: #1d1d1d; }
        .mode[disabled] { cursor: progress; }
        /* Carte étroite : les icônes seules (le nom reste en infobulle), sauf le mode en cours, dont le libellé déborde
           sur la place laissée vide par ses voisins au lieu d'être coupé. */
        .carte { container-type: inline-size; }
        @container (max-width: 360px) {
            .mode:not(.actif) .lib { visibility: hidden; }
            .mode.actif .lib { max-width: none; overflow: visible; }
        }`;

    #attente = false;
    #erreur = null;
    /** Consigne montrée pendant qu'on fait glisser le curseur, avant l'envoi de l'ordre. */
    #provisoire = null;
    /** Pendant un glisser, un rendu complet remplacerait le SVG tenu sous le doigt : il attend le lâcher. */
    #glisse = false;
    #enAttente = false;

    planifier() {
        if (this.#glisse) {
            this.#enAttente = true;
            return;
        }
        super.planifier();
    }

    rendu() {
        const entite = this._entities.thermostat;
        const etat = this._states.thermostat;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const extra = entite?.extra ?? {};
        const manque = absence(etat, entite);
        const planning = !!v.schedule_active;
        const entete = `<div class="entete">
            <div class="nom" title="${esc(entite?.name)}">${esc(entite?.name ?? "Thermostat")}</div>
            ${manque ? "" : `<button class="icone-btn planning" title="${planning ? "Planning actif : passer en manuel" : "En manuel : reprendre le planning"}"
                aria-pressed="${planning}" ${this.#attente ? "disabled" : ""} style="${planning ? `color:${PLANNING}` : ""}">${ms("calendar_month", planning ? "font-variation-settings:'FILL' 1" : "")}</button>`}
        </div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const min = Number(extra.min_target) || 5, max = Number(extra.max_target) || 30;
        const consigne = this.#provisoire ?? (typeof v.target === "number" ? v.target : null);
        const modeActuel = MODES.find(m => m.id === v.mode) ?? INCONNU;
        const couleur = v.mode === "off" ? "#90a4ae" : modeActuel.couleur;
        const part = consigne === null ? 0 : Math.min(1, Math.max(0, (consigne - min) / (max - min)));
        const angle = DEBUT + part * ETENDUE;
        const [kx, ky] = point(angle);

        const entier = consigne === null ? "—" : Math.trunc(consigne);
        const decimale = consigne === null ? "" : Math.round((consigne - Math.trunc(consigne)) * 10);
        const ambiante = typeof v.temperature === "number"
            ? `<div class="ambiante" title="Température ambiante">${ms("device_thermostat")} ${nombre(v.temperature)} °C</div>`
            : `<div class="ambiante" title="Température ambiante">—</div>`;
        const humidite = typeof v.humidity === "number" ? `<div class="humidite" title="Humidité">${ms("water_drop")} ${nombre(v.humidity, 0)} %</div>` : "";
        // Au-dessus de la consigne, dans l'anneau : en bas, l'arc la cacherait.
        const flamme = v.heating ? `<text class="flamme" x="${CX}" y="${CY - 34}" text-anchor="middle">local_fire_department</text>` : "";
        const occupe = this.#attente ? "disabled" : "";
        const boutons = BOUTONS.map(b => {
            const [x, y] = point(b.angle);
            return `<g class="pas ${this.#attente ? "occupe" : ""}" data-pas="${b.pas}" role="button" tabindex="0" aria-label="${b.nom}"
                       aria-disabled="${this.#attente}"><circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="15"/>
                <text x="${x.toFixed(1)}" y="${(y + 7).toFixed(1)}" text-anchor="middle">${b.signe}</text></g>`;
        }).join("");

        const libelleMode = id => (extra.modes ?? []).find(m => m.id === id)?.label ?? id;
        const nomMode = libelleMode(v.mode);
        // « Confort jusqu'à 22:30 » : la prochaine bascule du planning (heure locale, publiée par le cœur) ; sinon
        // « Confort en manuel ». Planning actif sans bascule dans les 24 h : le mode seul.
        const jusqua = planning && typeof v.next_change?.at === "string" ? ` jusqu'à ${esc(v.next_change.at.slice(11, 16))}`
            : planning ? "" : " en manuel";
        const statut = `<div class="statut" style="background:${couleur}1f;border:1px solid ${couleur}55">
            ${ms(modeActuel.icone, `color:${couleur}`)}<span><b style="color:${couleur}">${esc(nomMode)}</b>${jusqua}</span></div>`;

        const modes = MODES.map(m => {
            const actif = m.id === v.mode;
            return `<button class="mode ${actif ? "actif" : ""}" data-mode="${m.id}" ${occupe} aria-pressed="${actif}" title="${esc(libelleMode(m.id))}">
                ${ms(m.icone, `color:${actif ? m.couleur : "inherit"}${actif ? `;filter:drop-shadow(0 0 4px ${m.couleur}66)` : ""}`)}
                <span class="lib" style="${actif ? `background:${m.couleur}` : ""}">${esc(libelleMode(m.id))}</span></button>`;
        }).join("");

        return `<div class="carte">${entete}
            <div class="cadran">
                <svg viewBox="${VUE}" class="arc" role="slider" aria-label="Consigne" aria-valuemin="${min}" aria-valuemax="${max}" aria-valuenow="${consigne ?? ""}">
                    <path class="fond" d="${arc(DEBUT, DEBUT + ETENDUE)}"/>
                    <path class="rempli" d="${part > 0 ? arc(DEBUT, angle) : ""}" stroke="${couleur}" style="filter:drop-shadow(0 0 5px ${couleur}99)"/>
                    ${flamme}
                    <text class="entier" x="${CX + 14}" y="${CY + 21}" text-anchor="end">${entier}</text>
                    <text class="cote" x="${CX + 17}" y="${CY - 7}">°C</text>
                    <text class="cote decimale" x="${CX + 17}" y="${CY + 20}">${consigne === null ? "" : `.${decimale}`}</text>
                    <circle class="curseur" cx="${kx.toFixed(1)}" cy="${ky.toFixed(1)}" r="10" stroke="${couleur}"/>
                    ${boutons}
                </svg>
                <div class="mesures">${ambiante}${humidite}</div>
            </div>
            ${statut}
            <div class="modes">${modes}</div>
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
        </div>`;
    }

    apresRendu(racine) {
        const extra = this._entities.thermostat?.extra ?? {};
        const pas = Number(extra.target_step) || 0.5;
        const min = Number(extra.min_target) || 5, max = Number(extra.max_target) || 30;

        const svg = racine.querySelector("svg.arc");
        racine.querySelectorAll(".pas").forEach(b => {
            const ajuster = () => {
                if (!this.#attente) this.ordonner("adjust_target", { delta: Number(b.dataset.pas) * pas });
            };
            // Le bouton est dans le SVG : sans cela, l'appui démarrerait aussi un glisser du curseur.
            b.addEventListener("pointerdown", e => e.stopPropagation());
            b.addEventListener("click", ajuster);
            b.addEventListener("keydown", e => {
                if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    ajuster();
                }
            });
        });
        racine.querySelectorAll(".mode").forEach(b =>
            b.addEventListener("click", () => this.ordonner("set_mode", { mode: b.dataset.mode })));
        racine.querySelector(".planning")?.addEventListener("click", () =>
            this.ordonner("set_schedule", { active: !this._states.thermostat?.value?.schedule_active }));

        // Glisser le curseur (ou toucher l'arc) : la consigne suit, l'ordre part au lâcher, en valeur exacte.
        if (!svg) return;
        const valeur = e => {
            // Coordonnées de l'écran → celles du dessin (le cadre ne commence pas à 0).
            const { x, y } = new DOMPoint(e.clientX, e.clientY).matrixTransform(svg.getScreenCTM().inverse());
            const a = Math.atan2(y - CY, x - CX) * 180 / Math.PI;
            let rel = ((a - DEBUT) % 360 + 360) % 360;
            if (rel > ETENDUE) rel = rel > ETENDUE + (360 - ETENDUE) / 2 ? 0 : ETENDUE;   // dans l'ouverture : la borne la plus proche
            const brute = min + rel / ETENDUE * (max - min);
            return Math.min(max, Math.max(min, Math.round(brute / pas) * pas));
        };
        // Pendant le glisser, seuls l'arc, le curseur et le chiffre bougent : ils sont modifiés sur place.
        const montrer = v => {
            const part = Math.min(1, Math.max(0, (v - min) / (max - min)));
            const angle = DEBUT + part * ETENDUE;
            const [x, y] = point(angle);
            racine.querySelector(".rempli")?.setAttribute("d", part > 0 ? arc(DEBUT, angle) : "");
            const curseur = racine.querySelector(".curseur");
            curseur?.setAttribute("cx", x.toFixed(1));
            curseur?.setAttribute("cy", y.toFixed(1));
            racine.querySelector(".entier").textContent = Math.trunc(v);
            racine.querySelector(".decimale").textContent = `.${Math.round((v - Math.trunc(v)) * 10)}`;
        };
        svg.addEventListener("pointerdown", e => {
            if (this.#attente) return;
            svg.setPointerCapture(e.pointerId);
            svg.parentElement.classList.add("glisse");
            this.#glisse = true;
            this.#provisoire = valeur(e);
            montrer(this.#provisoire);
            const bouger = ev => {
                const v = valeur(ev);
                if (v !== this.#provisoire) {
                    this.#provisoire = v;
                    montrer(v);
                }
            };
            const lacher = () => {
                svg.removeEventListener("pointermove", bouger);
                this.#glisse = false;
                this.#enAttente = false;
                const cible = this.#provisoire;
                this.#provisoire = null;
                if (cible !== null && cible !== this._states.thermostat?.value?.target) this.ordonner("set_target", { temperature: cible });
                else this.planifier();
            };
            svg.addEventListener("pointermove", bouger);
            svg.addEventListener("pointerup", lacher, { once: true });
            svg.addEventListener("pointercancel", lacher, { once: true });
        });
    }

    async ordonner(commande, args) {
        if (!this._kv) return;
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        try {
            const r = await this._kv.command("thermostat", commande, args);
            if (r.outcome !== "accepted") this.#erreur = r.detail ?? "Ordre refusé.";
        } catch {
            this.#erreur = "Serveur injoignable.";
        }
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-thermostat-cadran", KvThermostatCadran);
