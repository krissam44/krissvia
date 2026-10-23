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
 * Au centre l'ambiante, l'humidité, la consigne en grand et la flamme quand il chauffe. Dessous le mode « jusqu'à »
 * la prochaine bascule du planning, ou « en manuel » ; en haut à droite, le planning ; en bas, les six modes.
 * Comme les autres cartes, rien n'est affiché parce qu'on a cliqué : la carte attend l'état suivant.
 */
class KvThermostatCadran extends KvWidget {
    static historique = "thermostat";

    static css = `
        .carte { padding: 12px 14px 10px; gap: 6px; }
        .entete { display: flex; align-items: center; gap: 6px; }
        .entete .nom { flex: 1; min-width: 0; font-size: .78rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase;
                       color: var(--kv-text-secondary, #777); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .icone-btn { border: 0; background: transparent; padding: 4px; border-radius: 50%; display: inline-flex; color: var(--kv-text-secondary, #777); }
        .icone-btn .ms { font-size: 1.3rem; }
        .icone-btn:hover { background: color-mix(in srgb, currentColor 12%, transparent); }
        .cadran { position: relative; flex: 1 1 auto; min-height: 150px; display: flex; justify-content: center; }
        .cadran svg { height: 100%; max-height: 240px; width: auto; max-width: 100%; touch-action: none; overflow: visible; }
        .fond { fill: none; stroke: color-mix(in srgb, var(--kv-text-secondary, #888) 22%, transparent); stroke-width: 12; stroke-linecap: round; }
        .rempli { fill: none; stroke-width: 12; stroke-linecap: round; }
        .curseur { fill: var(--kv-surface, #fff); stroke-width: 4; cursor: grab; }
        .glisse .curseur { cursor: grabbing; }
        .pas { position: absolute; width: 34px; height: 34px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
               background: var(--kv-surface, #fff); color: var(--kv-text, #222); font-size: 1.2rem; line-height: 1;
               border: 1.5px solid color-mix(in srgb, var(--kv-text-secondary, #888) 45%, transparent); }
        .pas:hover { border-color: var(--kv-text, #222); }
        .pas[disabled] { opacity: .45; cursor: progress; }
        .centre { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; pointer-events: none; }
        .ambiante { font-size: .95rem; font-weight: 600; }
        .ambiante .ms { font-size: 1.05rem; color: var(--kv-text-secondary, #777); }
        .humidite { font-size: .8rem; color: var(--kv-text-secondary, #777); }
        .humidite .ms { font-size: .9rem; color: #42a5f5; }
        .consigne { display: flex; align-items: flex-start; line-height: 1; margin-top: 4px; }
        .consigne .entier { font-size: 3.4rem; font-weight: 300; letter-spacing: -.02em; }
        .consigne .cote { display: flex; flex-direction: column; margin-left: 2px; font-size: 1.1rem; line-height: 1.05; }
        .flamme { margin-top: 4px; font-size: 1.6rem; }
        .statut { align-self: center; display: inline-flex; align-items: center; gap: 6px; padding: 6px 14px; border-radius: 22px; font-size: .9rem; }
        .statut b { font-weight: 600; }
        .modes { display: flex; justify-content: space-between; gap: 2px; }
        .mode { flex: 1; min-width: 0; border: 0; background: transparent; color: var(--kv-text-secondary, #666); padding: 4px 0;
                display: flex; flex-direction: column; align-items: center; gap: 3px; font: inherit; font-size: .72rem; border-radius: 10px; }
        .mode .ms { font-size: 1.6rem; }
        .mode .lib { padding: 1px 7px; border-radius: 10px; white-space: nowrap; }
        .mode.actif .lib { font-weight: 700; color: #1d1d1d; }
        .mode[disabled] { cursor: progress; }`;

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
            ? `<div class="ambiante">${ms("device_thermostat")} ${nombre(v.temperature)} °C</div>` : `<div class="ambiante">—</div>`;
        const humidite = typeof v.humidity === "number" ? `<div class="humidite">${ms("water_drop")} ${nombre(v.humidity, 0)} %</div>` : "";
        const flamme = v.heating ? ms("local_fire_department", `color:#ff7a00;filter:drop-shadow(0 0 6px #ff7a0088)`) : "";
        const occupe = this.#attente ? "disabled" : "";

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
                <svg viewBox="0 0 220 220" class="arc" role="slider" aria-label="Consigne" aria-valuemin="${min}" aria-valuemax="${max}" aria-valuenow="${consigne ?? ""}">
                    <path class="fond" d="${arc(DEBUT, DEBUT + ETENDUE)}"/>
                    <path class="rempli" d="${part > 0 ? arc(DEBUT, angle) : ""}" stroke="${couleur}" style="filter:drop-shadow(0 0 5px ${couleur}99)"/>
                    <circle class="curseur" cx="${kx.toFixed(1)}" cy="${ky.toFixed(1)}" r="10" stroke="${couleur}"/>
                </svg>
                <div class="centre">
                    ${ambiante}${humidite}
                    <div class="consigne"><span class="entier">${entier}</span><span class="cote"><span>°C</span><span>${consigne === null ? "" : `.${decimale}`}</span></span></div>
                    <div class="flamme">${flamme}</div>
                </div>
                <button class="pas plus" data-pas="1" aria-label="Augmenter la consigne" ${occupe}>+</button>
                <button class="pas moins" data-pas="-1" aria-label="Baisser la consigne" ${occupe}>−</button>
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

        // Les boutons − / + aux deux bouts de l'arc, placés d'après la taille réelle du cadran.
        const svg = racine.querySelector("svg.arc");
        const cadran = racine.querySelector(".cadran");
        if (svg && cadran) {
            const placer = () => {
                const b = svg.getBoundingClientRect(), c = cadran.getBoundingClientRect();
                const echelle = b.width / 220;
                for (const [sel, angle] of [[".plus", DEBUT + ETENDUE], [".moins", DEBUT]]) {
                    const [x, y] = point(angle);
                    const bouton = racine.querySelector(sel);
                    if (!bouton) continue;
                    bouton.style.left = `${b.left - c.left + x * echelle + 6 * echelle - 17}px`;
                    bouton.style.top = `${b.top - c.top + y * echelle - 17}px`;
                }
            };
            placer();
            requestAnimationFrame(placer);
        }

        racine.querySelectorAll(".pas").forEach(b =>
            b.addEventListener("click", () => this.ordonner("adjust_target", { delta: Number(b.dataset.pas) * pas })));
        racine.querySelectorAll(".mode").forEach(b =>
            b.addEventListener("click", () => this.ordonner("set_mode", { mode: b.dataset.mode })));
        racine.querySelector(".planning")?.addEventListener("click", () =>
            this.ordonner("set_schedule", { active: !this._states.thermostat?.value?.schedule_active }));

        // Glisser le curseur (ou toucher l'arc) : la consigne suit, l'ordre part au lâcher, en valeur exacte.
        if (!svg) return;
        const valeur = e => {
            const r = svg.getBoundingClientRect();
            const x = (e.clientX - r.left) / r.width * 220, y = (e.clientY - r.top) / r.height * 220;
            let a = Math.atan2(y - CY, x - CX) * 180 / Math.PI;
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
            racine.querySelector(".consigne .entier").textContent = Math.trunc(v);
            racine.querySelector(".consigne .cote span:last-child").textContent = `.${Math.round((v - Math.trunc(v)) * 10)}`;
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
