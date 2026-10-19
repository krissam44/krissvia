import { KvBascule } from "../bascule.js";

/** Une lumière, ou l'interrupteur d'un éclairage (usage « Éclairage » dans l'écran Entités) : ampoule, jaune allumée. */
class KvEclairage extends KvBascule {
    static aspect = {
        allume: "lightbulb", eteint: "lightbulb", couleur: "#ffc107", texte: "#3a2c00",
        etatAllume: "Allumée", etatEteint: "Éteinte",
    };
}

customElements.define("kv-eclairage", KvEclairage);
