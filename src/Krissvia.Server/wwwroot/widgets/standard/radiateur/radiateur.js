import { KvBascule } from "../bascule.js";

/** L'interrupteur d'un radiateur (usage « Radiateur » dans l'écran Entités) : flamme, orange quand il chauffe. */
class KvRadiateur extends KvBascule {
    static aspect = {
        allume: "local_fire_department", eteint: "local_fire_department", couleur: "#ff5722", texte: "#fff",
        etatAllume: "Chauffe", etatEteint: "Arrêté",
    };
}

customElements.define("kv-radiateur", KvRadiateur);
