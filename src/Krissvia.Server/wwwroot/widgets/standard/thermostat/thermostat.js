import { KvWidget, esc, nombre, absence } from "../commun.js";

// Les modes viennent de l'entité (extra.modes) : seules les icônes sont choisies ici.
const ICONES = { comfort: "🔥", comfort1: "🔥", comfort2: "🔥", eco: "🍃", frost_protection: "❄️", off: "⏻" };

const COULEUR_STATUT = {
    sensor_lost: "var(--kv-error, #d33)", unknown_switch: "var(--kv-error, #d33)",
    inhibited: "var(--kv-warning, #e6a23c)", window_open: "var(--kv-warning, #e6a23c)",
    heating: "#f57c00", coasting: "#26c6da",
};

/**
 * Carte de thermostat : ambiante et extérieure, consigne avec −/+, rangée de modes, statut calculé par le cœur.
 * Le pas part en ordre relatif (adjust_target) : deux clics rapides font deux pas, quoi qu'affiche la carte.
 */
class KvThermostat extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .nom { font-weight: 600; font-size: 1rem; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .badge { font-size: .7rem; padding: 2px 8px; border-radius: 10px; border: 1px solid currentColor; white-space: nowrap; }
        .milieu { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin: 6px 0; flex: 1; }
        .ambiante .grand.chauffe { color: #f57c00; }
        .consigne { display: flex; align-items: center; gap: 6px; text-align: right; }
        .pas { width: 34px; height: 34px; border-radius: 50%; border: 1px solid var(--kv-text-secondary, #999); background: transparent;
               color: var(--kv-text, #222); font-size: 1.1rem; line-height: 1; }
        .pas[disabled] { opacity: .35; cursor: default; }
        .modes { display: grid; grid-template-columns: repeat(auto-fit, minmax(40px, 1fr)); gap: 4px; }
        .mode { border: 1px solid var(--kv-text-secondary, #bbb); border-radius: 8px; background: transparent; color: var(--kv-text, #222);
                padding: 4px 1px; font-size: .62rem; white-space: nowrap; overflow: hidden; display: flex; flex-direction: column; align-items: center; gap: 1px; }
        .mode .icone { font-size: .95rem; }
        .mode.actif { border-color: var(--kv-primary, #594ae2); background: color-mix(in srgb, var(--kv-primary, #594ae2) 15%, transparent); font-weight: 600; }
        .mode[disabled] { opacity: .55; cursor: progress; }`;

    #attente = false;
    #erreur = null;

    set states(v) {
        this.#attente = false;
        super.states = v;
    }

    rendu() {
        const entite = this._entities.thermostat;
        const etat = this._states.thermostat;
        const modes = entite?.extra?.modes ?? [];
        const rad = entite?.extra?.radiators;
        const sousTitre = [entite?.room, rad ? `${rad} rad.` : null].filter(Boolean).join(" · ");
        const manque = absence(etat, entite);
        const v = etat?.value ?? {};
        const figee = v.mode === "off" && !v.schedule_active;

        const corps = manque ?? `
            <div class="milieu">
                <div class="ambiante">
                    <div class="grand ${v.heating ? "chauffe" : ""}">${nombre(v.temperature)}<span class="unite">°C</span></div>
                    <div class="detail">ambiante${typeof v.humidity === "number" ? ` · 💧 ${nombre(v.humidity, 0)} %` : ""}</div>
                    ${typeof v.outdoor_temperature === "number" ? `<div class="detail">☁ ${nombre(v.outdoor_temperature)} °C ext.</div>` : ""}
                </div>
                <div class="consigne">
                    <button class="pas" data-pas="-1" aria-label="Baisser la consigne" ${figee || this.#attente ? "disabled" : ""}>−</button>
                    <div>
                        <div class="grand">${nombre(v.target)}</div>
                        <div class="detail">°C consigne${v.target_adjusted ? " (ajustée)" : ""}</div>
                    </div>
                    <button class="pas" data-pas="1" aria-label="Monter la consigne" ${figee || this.#attente ? "disabled" : ""}>+</button>
                </div>
            </div>
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
            <div class="modes">
                ${modes.map(m => `<button class="mode ${m.id === v.mode ? "actif" : ""}" data-mode="${esc(m.id)}" ${this.#attente ? "disabled" : ""}>
                    <span class="icone">${ICONES[m.id] ?? "•"}</span>${esc(m.label)}</button>`).join("")}
            </div>
            ${v.schedule_active ? `<div class="detail">Planning actif</div>` : ""}`;

        return `<div class="carte">
            <div class="entete">
                <div class="nom" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
                ${v.status_label ? `<span class="badge" style="color:${COULEUR_STATUT[v.status] ?? "var(--kv-text-secondary, #777)"}">${esc(v.status_label)}</span>` : ""}
            </div>
            ${sousTitre ? `<div class="piece">${esc(sousTitre)}</div>` : ""}
            ${corps}
        </div>`;
    }

    apresRendu(racine) {
        const pasConsigne = Number(this._entities.thermostat?.extra?.target_step) || 0.5;
        racine.querySelectorAll(".pas").forEach(b =>
            b.addEventListener("click", () => this.ordonner("adjust_target", { delta: Number(b.dataset.pas) * pasConsigne })));
        racine.querySelectorAll(".mode").forEach(b =>
            b.addEventListener("click", () => this.ordonner("set_mode", { mode: b.dataset.mode })));
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
