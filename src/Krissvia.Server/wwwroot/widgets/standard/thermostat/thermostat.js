import { KvWidget, esc, nombre, absence } from "../commun.js";

// Couleurs et icônes de la carte de DomoMud (ThermostatConfig). Les libellés viennent de l'entité (extra.modes) :
// un mode inconnu d'ici s'affiche quand même, en gris.
const MODES = {
    comfort: { couleur: "#ff5722", icone: "local_fire_department" },
    comfort1: { couleur: "#ffa726", icone: "local_fire_department" },
    comfort2: { couleur: "#ffd60a", icone: "local_fire_department" },
    eco: { couleur: "#66bb6a", icone: "eco" },
    frost_protection: { couleur: "#42a5f5", icone: "ac_unit" },
    off: { couleur: "#546e7a", icone: "power_off" },
};
const MODE_INCONNU = { couleur: "#78909c", icone: "thermostat" };

const STATUTS = {
    heating: "#ff5722", coasting: "#26c6da", idle: "#66bb6a", off: "#546e7a",
    inhibited: "#ffa726", window_open: "#42a5f5", sensor_lost: "#ef5350", unknown_switch: "#ef5350",
};

const CHAUFFE = "#ff5722";
const INERTIE = "#26c6da";
const PLANNING = "#00b4d8";

/** Couleur de l'ambiante, par paliers comme DomoMud : froid, bien, tiède, chaud. */
function couleurAmbiante(t) {
    if (typeof t !== "number") return "var(--kv-text-secondary, #777)";
    return t < 14 ? "#42a5f5" : t < 18 ? "#66bb6a" : t < 22 ? "#ffa726" : "#ef5350";
}

const ms = (nom, style = "") => `<span class="ms" aria-hidden="true"${style ? ` style="${style}"` : ""}>${nom}</span>`;

/**
 * Carte de thermostat, redessinée d'après celle de DomoMud : en-tête (flamme, statut, planning, réglages), ambiante →
 * consigne avec −/+, barre TPI ou d'inertie, rangée de modes. Tout ce qu'elle montre est calculé par le cœur (statut,
 * part de chauffe) : la carte ne recalcule rien, elle colore. Le pas part en ordre relatif (adjust_target) : deux
 * clics rapides font deux pas, quoi qu'affiche la carte.
 */
class KvThermostat extends KvWidget {
    static css = `
        .ms { font-family: 'Material Symbols Outlined'; font-weight: normal; font-style: normal; line-height: 1; letter-spacing: normal;
              text-transform: none; white-space: nowrap; direction: ltr; font-feature-settings: 'liga'; -webkit-font-smoothing: antialiased;
              display: inline-block; vertical-align: middle; font-size: 1em; }
        .carte { padding: 0; gap: 0; border-radius: 12px; transition: box-shadow .3s; }
        .entete { display: flex; align-items: center; gap: 6px; padding: 8px 10px 6px 12px; border-bottom: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 25%, transparent); }
        .flamme { font-size: 1.5rem; }
        .titres { flex: 1; min-width: 0; }
        .nom { font-weight: 600; font-size: .95rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .badge { padding: 2px 8px; border-radius: 8px; font-size: .68rem; font-weight: 700; white-space: nowrap; }
        .icone-btn { border: 0; background: transparent; padding: 4px; border-radius: 50%; display: inline-flex; color: var(--kv-text-secondary, #777); }
        .icone-btn .ms { font-size: 1.25rem; }
        .icone-btn:hover { background: color-mix(in srgb, currentColor 12%, transparent); }
        .icone-btn[disabled] { opacity: .5; cursor: progress; }
        .corps { padding: 0 14px 6px; flex: 1; display: flex; flex-direction: column; justify-content: center; min-height: 0; }
        .temperatures { display: flex; align-items: center; justify-content: space-between; gap: 5px; }
        .bloc { text-align: center; min-width: 64px; }
        .valeur { font-size: 2.1rem; font-weight: 700; line-height: 1; }
        .legende { font-size: .7rem; color: var(--kv-text-secondary, #777); margin-top: 2px; }
        .annexe { font-size: .68rem; margin-top: 1px; }
        .annexe .ms { font-size: .8rem; }
        .fleche { font-size: 1.1rem; color: var(--kv-text-secondary, #999); opacity: .6; flex-shrink: 0; }
        .consigne { display: flex; align-items: center; gap: 6px; flex: 1; justify-content: flex-end; }
        .pas { width: 30px; height: 30px; border-radius: 50%; border: 0; flex-shrink: 0; margin-bottom: 18px; display: inline-flex; align-items: center;
               justify-content: center; background: color-mix(in srgb, var(--kv-text-secondary, #888) 15%, transparent); color: var(--kv-text-secondary, #777); }
        .pas .ms { font-size: 1.2rem; }
        .pas[disabled] { opacity: .5; cursor: progress; }
        .barre { margin-top: 6px; }
        .barre .lignes { display: flex; justify-content: space-between; font-size: .65rem; color: var(--kv-text-secondary, #777); margin-bottom: 2px; }
        .barre .lignes .ms { font-size: .75rem; }
        .jauge { height: 4px; border-radius: 2px; background: color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); overflow: hidden; }
        .jauge > div { height: 100%; border-radius: 2px; transition: width .4s; }
        .modes { display: flex; gap: 3px; padding: 4px 10px 10px; flex-wrap: wrap; }
        .mode { flex: 1; min-width: 52px; text-align: center; padding: 5px 2px; border-radius: 7px; display: block; margin: 0; font-size: .62rem;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; background: var(--kv-surface, transparent);
                color: var(--kv-text-secondary, #777); border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 30%, transparent); transition: all .15s ease; }
        .mode .ms { font-size: .95rem; display: block; margin: 0 auto 1px; }
        .mode[disabled] { cursor: progress; }
        .erreur { padding: 0 14px; }`;

    #attente = false;
    #erreur = null;

    set states(v) {
        this.#attente = false;
        super.states = v;
    }

    rendu() {
        const entite = this._entities.thermostat;
        const etat = this._states.thermostat;
        const v = etat?.value ?? {};
        const manque = absence(etat, entite);
        const inertie = !manque && v.status === "coasting";
        const chauffe = !manque && !!v.heating;
        const off = v.mode === "off";
        const attente = this.#attente ? "disabled" : "";

        const teinte = inertie ? INERTIE : chauffe ? CHAUFFE : null;
        const cadre = teinte ? `box-shadow: inset 0 0 0 1px ${teinte}55, inset 0 -24px 40px -28px ${teinte}55;` : "";

        const rad = entite?.extra?.radiators;
        const sousTitre = [entite?.room, typeof rad === "number" ? `${rad} rad.` : null].filter(Boolean).join(" · ");
        const statut = STATUTS[v.status] ?? "#78909c";
        const flamme = inertie ? ms("mode_cool", `color:${INERTIE};filter:drop-shadow(0 0 6px ${INERTIE}88)`)
            : ms("local_fire_department", chauffe ? `color:${CHAUFFE};filter:drop-shadow(0 0 6px ${CHAUFFE}88)` : "color:var(--kv-text-secondary, #777);opacity:.6");
        const planning = !!v.schedule_active;
        const configuration = entite?.extra?.configuration;

        const entete = `
            <div class="entete">
                <span class="flamme">${flamme}</span>
                <div class="titres">
                    <div class="nom" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
                    ${sousTitre ? `<div class="piece">${esc(sousTitre)}</div>` : ""}
                </div>
                ${!manque && v.status_label ? `<div class="badge" style="background:${statut}22;color:${statut};border:1px solid ${statut}44">${esc(v.status_label)}</div>` : ""}
                ${!manque ? `<button class="icone-btn planning" title="${planning ? "Planning actif : passer en manuel" : "Mode manuel : reprendre le planning"}"
                        aria-pressed="${planning}" ${attente} style="${planning ? `color:${PLANNING}` : ""}">${ms("calendar_month", planning ? "font-variation-settings:'FILL' 1" : "")}</button>` : ""}
                ${configuration ? `<button class="icone-btn reglages" title="Réglages du thermostat">${ms("settings")}</button>` : ""}
            </div>`;

        if (manque) return `<div class="carte">${entete}<div class="corps">${manque}</div></div>`;

        const mode = MODES[v.mode] ?? MODE_INCONNU;
        const pas = (sens, icone, libelle) =>
            `<button class="pas" data-pas="${sens}" aria-label="${libelle}" ${attente}>${ms(icone)}</button>`;
        const exterieure = typeof v.outdoor_temperature === "number"
            ? `<div class="annexe" style="color:#42a5f5">${ms("cloudy")} ${nombre(v.outdoor_temperature)}°C ext.</div>` : "";
        const humidite = typeof v.humidity === "number"
            ? `<div class="annexe" style="color:var(--kv-text-secondary, #777)">${ms("humidity_percentage")} ${nombre(v.humidity, 0)} %</div>` : "";
        const fenetre = v.window_open ? `<div class="annexe" style="color:#42a5f5">${ms("sensor_door")} Fenêtre ouverte</div>` : "";

        const modes = entite?.extra?.modes ?? [];
        const boutonsModes = modes.map(m => {
            const c = (MODES[m.id] ?? MODE_INCONNU).couleur;
            // Consigne ajustée à la main : aucun mode n'est « le » mode affiché, comme dans DomoMud.
            const actif = !v.target_adjusted && m.id === v.mode;
            const style = actif ? `background:${c}22;color:${c};border-color:${c}55;font-weight:700` : "";
            return `<button class="mode" data-mode="${esc(m.id)}" aria-pressed="${actif}" title="${esc(m.label)}" style="${style}" ${attente}>
                ${ms((MODES[m.id] ?? MODE_INCONNU).icone)}${esc(m.label)}</button>`;
        }).join("");

        return `<div class="carte" style="${cadre}">
            ${entete}
            <div class="corps">
                <div class="temperatures">
                    <div class="bloc">
                        <div class="valeur" style="color:${couleurAmbiante(v.temperature)}">${nombre(v.temperature)}</div>
                        <div class="legende">°C ambiance</div>
                        ${exterieure}${humidite}${fenetre}
                    </div>
                    <span class="fleche">${ms("arrow_right_alt")}</span>
                    <div class="consigne">
                        ${off ? "" : pas(-1, "remove", "Baisser la consigne")}
                        <div class="bloc">
                            <div class="valeur" style="color:${mode.couleur}">${nombre(v.target)}</div>
                            <div class="legende">°C consigne</div>
                        </div>
                        ${off ? "" : pas(1, "add", "Monter la consigne")}
                    </div>
                </div>
                ${off ? "" : this.barre(v, mode.couleur)}
            </div>
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
            <div class="modes">${boutonsModes}</div>
        </div>`;
    }

    /** Inertie : ce qu'il reste à redescendre entre les deux seuils. Sinon la part de chauffe du cycle TPI. */
    barre(v, couleurMode) {
        if (v.status === "coasting") {
            const depassement = Math.max(0, (v.temperature ?? 0) - (v.target ?? 0));
            const haut = v.coasting_high, bas = v.coasting_low;
            const pct = typeof haut === "number" && typeof bas === "number" && haut > bas
                ? Math.min(100, Math.max(0, (1 - (depassement - bas) / (haut - bas)) * 100)) : 0;
            return `<div class="barre">
                <div class="lignes"><span style="color:${INERTIE}">${ms("mode_cool")} Refroidissement naturel</span></div>
                <div class="jauge"><div style="width:${pct}%;background:${INERTIE}"></div></div>
            </div>`;
        }
        if (typeof v.power !== "number") return "";
        const pct = Math.min(100, Math.max(0, v.power * 100));
        const cycle = typeof v.cycle_seconds === "number" ? v.cycle_seconds : null;
        return `<div class="barre">
            <div class="lignes">
                <span>TPI : ${Math.round(pct)} %</span>
                ${cycle ? `<span>${Math.round(v.power * cycle)} s / ${cycle} s</span>` : ""}
            </div>
            <div class="jauge"><div style="width:${pct}%;background:${couleurMode}"></div></div>
        </div>`;
    }

    apresRendu(racine) {
        const extra = this._entities.thermostat?.extra ?? {};
        const pasConsigne = Number(extra.target_step) || 0.5;
        racine.querySelectorAll(".pas").forEach(b =>
            b.addEventListener("click", () => this.ordonner("adjust_target", { delta: Number(b.dataset.pas) * pasConsigne })));
        racine.querySelectorAll(".mode").forEach(b =>
            b.addEventListener("click", () => this.ordonner("set_mode", { mode: b.dataset.mode })));
        racine.querySelector(".planning")?.addEventListener("click", () =>
            this.ordonner("set_schedule", { active: !this._states.thermostat?.value?.schedule_active }));
        // Un chemin du site, jamais une URL venue d'ailleurs : la carte ne connaît pas le routeur de Blazor, le navigateur suffit.
        racine.querySelector(".reglages")?.addEventListener("click", () => {
            if (typeof extra.configuration === "string" && /^\/[^/\\]/.test(extra.configuration)) location.assign(extra.configuration);
        });
    }

    async ordonner(commande, args) {
        if (!this._kv) return;
        this.#attente = true;
        this.#erreur = null;
        this.planifier();
        const r = await this._kv.command("thermostat", commande, args);
        if (r.outcome !== "accepted") this.#erreur = r.detail ?? "Ordre refusé.";
        this.#attente = false;
        this.planifier();
    }
}

customElements.define("kv-thermostat", KvThermostat);
