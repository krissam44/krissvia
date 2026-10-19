import { KvBascule } from "../bascule.js";

/** Une prise commandée (usage « Prise » dans l'écran Entités) : fiche électrique, bleue quand elle est alimentée. */
class KvPrise extends KvBascule {
    static aspect = {
        allume: "power", eteint: "power", couleur: "#29b6f6", texte: "#fff",
        etatAllume: "Alimentée", etatEteint: "Coupée",
    };
}

customElements.define("kv-prise", KvPrise);
