import { KvWidget, esc, nombre, depuis, absence } from "../commun.js";

class KvTemperature extends KvWidget {
    static css = `.goutte { color: var(--kv-primary, #594ae2); }`;

    rendu() {
        const entite = this._entities.temperature;
        const etat = this._states.temperature;
        const humidite = this._states.humidite;
        const h = humidite?.available && typeof humidite.value === "number"
            ? `<div class="detail"><span class="goutte">💧</span> ${nombre(humidite.value, 0)} %</div>` : "";
        return `<div class="carte">
            <div class="titre" title="${esc(entite?.name)}">${esc(entite?.name ?? "—")}</div>
            ${absence(etat, entite) ?? `<div class="grand">${nombre(etat.value)}<span class="unite">°C</span></div>`}
            ${h}
            <div class="detail bas">${esc(depuis(etat?.measured_at))}</div>
        </div>`;
    }
}

customElements.define("kv-temperature", KvTemperature);
