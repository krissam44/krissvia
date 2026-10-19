import { KvWidget, esc, nombre, absence } from "../commun.js";

// Codes temps WMO → emoji, comme la carte de DomoMud : pas de police ni d'image à charger, et le système les dessine
// en couleur. Le libellé, lui, vient du plugin (value.condition).
function emoji(code, jour = true) {
    switch (code) {
        case 0: return jour ? "☀️" : "🌙";
        case 1: return jour ? "🌤️" : "🌙";
        case 2: return "⛅";
        case 3: return "☁️";
        case 45: case 48: return "🌫️";
        case 51: case 53: case 55: case 56: case 57: return "🌦️";
        case 61: case 63: case 65: case 66: case 67: return "🌧️";
        case 71: case 73: case 75: case 77: case 85: case 86: return "❄️";
        case 80: case 81: case 82: return "🌦️";
        case 95: case 96: case 99: return "⛈️";
        default: return "❓";
    }
}

/** « 2026-10-19T08:36 » (heure locale du lieu, telle que la donne Open-Meteo) → « 08:36 ». */
const heure = t => typeof t === "string" && t.length >= 16 ? t.slice(11, 16) : null;

/** « Auj. » pour aujourd'hui, sinon le jour abrégé : « Mar. », « Mer. »… */
function jourCourt(date) {
    const d = new Date(`${date}T12:00:00`);
    const auj = new Date();
    if (d.toDateString() === auj.toDateString()) return "Auj.";
    const j = d.toLocaleDateString("fr-FR", { weekday: "short" });
    return j.charAt(0).toUpperCase() + j.slice(1);
}

/**
 * Carte météo d'un lieu (entité « weather » du plugin Météo) : température, ressenti et ciel du moment, humidité,
 * vent, soleil, puis la prévision sur 7 jours. Rien à commander : la carte ne fait que montrer.
 */
class KvMeteo extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; }
        .entete .icone { font-size: 1.6rem; line-height: 1; }
        .entete .nom { font-size: 1.1rem; font-weight: 500; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .maintenant { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; margin-top: 6px; }
        .temp { font-size: 2.8rem; font-weight: 700; color: #ffb300; line-height: 1; }
        .temp .unite { font-size: 1.2rem; font-weight: 400; }
        .ciel { color: var(--kv-text-secondary, #777); font-size: .9rem; }
        .details { display: flex; gap: 14px; flex-wrap: wrap; margin-top: 8px; color: var(--kv-text-secondary, #777); font-size: .85rem; }
        .trait { border: 0; border-top: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 25%, transparent); margin: 10px 0 8px; width: 100%; }
        .jours { display: flex; gap: 4px; justify-content: space-between; }
        .jour { flex: 1; text-align: center; min-width: 0; }
        .jour .nom-jour { font-size: .72rem; color: var(--kv-text-secondary, #777); }
        .jour .ic { font-size: 1.25rem; line-height: 1.4; }
        .jour .max { font-size: .78rem; font-weight: 600; }
        .jour .min { font-size: .72rem; color: var(--kv-text-secondary, #777); }
        .jour .pluie { font-size: .68rem; color: #42a5f5; }
        .maj { font-size: .72rem; color: var(--kv-text-secondary, #999); text-align: right; }`;

    rendu() {
        const entite = this._entities.meteo;
        const etat = this._states.meteo;
        const v = etat?.value && typeof etat.value === "object" ? etat.value : {};
        const nom = v.name ?? entite?.name ?? "Météo";
        const manque = absence(etat, entite);

        const entete = `<div class="entete">
            <span class="icone">${manque ? "🌡️" : emoji(v.code, v.is_day !== false)}</span>
            <div class="nom" title="${esc(nom)}">${esc(nom)}</div>
        </div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const details = [
            typeof v.humidity === "number" ? `<span title="Humidité">💧 ${nombre(v.humidity, 0)} %</span>` : "",
            typeof v.wind_speed === "number" ? `<span title="Vent">🌬️ ${nombre(v.wind_speed, 0)} km/h</span>` : "",
            v.precipitation > 0 ? `<span title="Précipitations">🌧️ ${nombre(v.precipitation)} mm</span>` : "",
            heure(v.sunrise) ? `<span title="Lever du soleil">🌅 ${heure(v.sunrise)}</span>` : "",
            heure(v.sunset) ? `<span title="Coucher du soleil">🌇 ${heure(v.sunset)}</span>` : "",
        ].join("");

        const jours = (Array.isArray(v.forecast) ? v.forecast : []).slice(0, 7).map(j => `
            <div class="jour" title="${esc(j.condition ?? "")}">
                <div class="nom-jour">${esc(jourCourt(j.date))}</div>
                <div class="ic">${emoji(j.code)}</div>
                <div class="max">${nombre(j.max, 0)}°</div>
                <div class="min">${nombre(j.min, 0)}°</div>
                ${j.precipitation_probability > 0 ? `<div class="pluie">${j.precipitation_probability}%</div>` : ""}
            </div>`).join("");

        const maj = etat.measured_at ? new Date(etat.measured_at).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" }) : null;

        return `<div class="carte">
            ${entete}
            <div class="maintenant">
                <div class="temp">${nombre(v.temperature)}<span class="unite"> °C</span></div>
                <div class="ciel">
                    <div>${esc(v.condition ?? "")}</div>
                    ${typeof v.apparent_temperature === "number" ? `<div>Ressenti ${nombre(v.apparent_temperature)}°C</div>` : ""}
                </div>
            </div>
            <div class="details">${details}</div>
            ${jours ? `<hr class="trait"><div class="jours">${jours}</div>` : ""}
            ${maj ? `<div class="maj bas">MAJ ${maj}</div>` : ""}
        </div>`;
    }
}

customElements.define("kv-meteo", KvMeteo);
