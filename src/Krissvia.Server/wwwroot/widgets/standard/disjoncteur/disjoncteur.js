import { KvBascule } from "../bascule.js";
import { nombre } from "../commun.js";

/**
 * Un disjoncteur commandé (usage « Disjoncteur » dans l'écran Entités) : orange quand il est enclenché. Le couper prive
 * tout un circuit, d'où le second clic (« Couper ? »). Avec la puissance et le courant de son appareil, quand il les
 * mesure (SONOFF BASIC-ZB1GSP), pour voir ce que le circuit tire avant de le couper.
 */
class KvDisjoncteur extends KvBascule {
    static aspect = {
        allume: "electrical_services", eteint: "electrical_services", couleur: "#ff7043", texte: "#fff",
        etatAllume: "Enclenché", etatEteint: "Coupé", allumer: "Enclencher", eteindre: "Couper",
    };

    static confirmerExtinction = true;

    complement() {
        const valeur = slot => { const v = this._states[slot]?.value; return this._states[slot]?.available !== false && typeof v === "number" ? v : null; };
        const puissance = valeur("puissance"), courant = valeur("courant");
        return [puissance !== null ? ` · ${nombre(puissance, 0)} W` : "", courant !== null ? ` · ${nombre(courant, 1)} A` : ""].join("");
    }
}

customElements.define("kv-disjoncteur", KvDisjoncteur);
