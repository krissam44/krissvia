import { KvBascule } from "../bascule.js";

/** Une prise, un relais : l'aspect par défaut de KvBascule. */
class KvInterrupteur extends KvBascule {}

customElements.define("kv-interrupteur", KvInterrupteur);
