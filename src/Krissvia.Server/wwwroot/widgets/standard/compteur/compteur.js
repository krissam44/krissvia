import { KvBascule } from "../bascule.js";

/** Le relais d'un compteur d'énergie (usage « Compteur » dans l'écran Entités) : compteur électrique, ambre allumé. */
class KvCompteur extends KvBascule {
    static aspect = {
        allume: "electric_meter", eteint: "electric_meter", couleur: "#ffb300", texte: "#3a2c00",
        etatAllume: "Relais fermé", etatEteint: "Relais ouvert",
    };
}

customElements.define("kv-compteur", KvCompteur);
