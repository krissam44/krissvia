// Hôte des widgets du tableau de bord (WIDGETS.md §4-5).
//
// Blazor dessine la grille et ses cartes ; ce module remplit le corps de chaque carte avec l'élément de son widget,
// lui transmet entités, états et options, et lui prête l'hôte `kv` pour envoyer des ordres. Il ne parle qu'à l'API
// publique (/api/live, /api/entities/…/commands) : c'est le même contrat pour les widgets standards et pour les
// autres.
//
// Un widget qui échoue (script introuvable, élément jamais défini, exception) ne casse pas le tableau de bord : la
// carte affiche le widget générique « valeur » et un bandeau d'erreur.

const SECOURS = "valeur";

const hote = {
    racine: null,
    catalogue: {},          // id du widget → { element, script_url, … }
    entites: new Map(),     // entity_id → entité, sans son état
    etats: new Map(),       // entity_id → { value, available, measured_at } | null
    cartes: new Map(),      // id de carte → { cle, liens, element }
    ws: null,
    arret: false,
    delai: 1000,
    dotnet: null,           // la page Blazor, pour ce que le navigateur ne sait pas faire seul (la fenêtre d'historique)
};

/**
 * Démarre le flux en direct. `catalogue` : la liste de /api/widgets. `dotnet` : la page qui héberge les cartes, appelée
 * pour ouvrir la fenêtre d'historique d'une entité (`kv.openHistory`).
 */
export function demarrer(racine, catalogue, dotnet = null) {
    hote.racine = racine;
    hote.dotnet = dotnet;
    hote.catalogue = Object.fromEntries(catalogue.map(w => [w.id, w]));
    hote.arret = false;
    connecter();
}

export function arreter() {
    hote.arret = true;
    hote.ws?.close();
    hote.cartes.clear();
}

/**
 * Les cartes à afficher : [{ id, widget, entities: { slot: entity_id }, settings, title, principal }]. Rappelé après
 * chaque rendu. `title` : nom propre à la carte, donné à l'entité du slot `principal` à la place du sien.
 */
export async function appliquer(cartes) {
    const vues = new Set();
    for (const carte of cartes) {
        vues.add(carte.id);
        const corps = hote.racine?.querySelector(`[data-carte="${CSS.escape(carte.id)}"] .kv-carte-corps`);
        if (!corps) continue;

        const cle = JSON.stringify([carte.widget, carte.entities, carte.settings ?? null, carte.title ?? null]);
        const connue = hote.cartes.get(carte.id);
        if (connue && connue.cle === cle && corps.contains(connue.element)) continue;

        const montee = await monter(carte, corps);
        hote.cartes.set(carte.id, { cle, carte, ...montee });
        alimenter(hote.cartes.get(carte.id));
    }
    for (const id of [...hote.cartes.keys()]) if (!vues.has(id)) hote.cartes.delete(id);
}

// ── Flux en direct ───────────────────────────────────────────────────────────

function connecter() {
    const ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/api/live`);
    hote.ws = ws;
    ws.onopen = () => {
        hote.delai = 1000;
        hote.racine?.classList.remove("kv-hors-ligne");
    };
    ws.onmessage = e => {
        const message = JSON.parse(e.data);
        if (message.type === "entities") {
            hote.entites = new Map();
            for (const { state, ...entite } of message.entities) {
                hote.entites.set(entite.entity_id, entite);
                hote.etats.set(entite.entity_id, state ?? null);
            }
            for (const carte of hote.cartes.values()) alimenter(carte);
        } else if (message.type === "states") {
            const changees = new Set(Object.keys(message.states));
            for (const [id, etat] of Object.entries(message.states)) hote.etats.set(id, etat ?? null);
            for (const carte of hote.cartes.values())
                if (Object.values(carte.liens).some(id => changees.has(id))) alimenter(carte);
        }
    };
    ws.onclose = () => {
        if (hote.ws !== ws) return;
        hote.racine?.classList.add("kv-hors-ligne");
        if (hote.arret) return;
        setTimeout(() => { if (!hote.arret) connecter(); }, hote.delai);
        hote.delai = Math.min(hote.delai * 2, 30000);
    };
}

// ── Cartes ───────────────────────────────────────────────────────────────────

async function monter(carte, corps) {
    corps.replaceChildren();
    const widget = hote.catalogue[carte.widget];
    let erreur = null;
    let element = null;
    let liens = carte.entities ?? {};

    if (!widget) erreur = `Widget « ${carte.widget} » introuvable.`;
    else {
        try {
            element = await creer(widget);
        } catch (e) {
            erreur = `Widget « ${widget.name} » : ${e?.message ?? e}`;
        }
    }

    if (!element) {
        // Le générique ne connaît qu'un slot : on lui donne l'entité principale de la carte.
        liens = { entite: Object.values(carte.entities ?? {})[0] ?? "" };
        try {
            element = await creer(hote.catalogue[SECOURS]);
        } catch {
            element = document.createElement("div");
        }
    }

    element.kv = hoteDeCarte(carte, liens);
    if (erreur) {
        const bandeau = document.createElement("div");
        bandeau.className = "kv-carte-erreur";
        bandeau.textContent = erreur;
        bandeau.title = erreur;
        corps.append(bandeau);
    }
    corps.append(element);
    return { element, liens };
}

async function creer(widget) {
    if (!widget) throw new Error("widget de secours absent");
    await import(widget.script_url);
    if (!customElements.get(widget.element)) {
        await Promise.race([customElements.whenDefined(widget.element), new Promise(r => setTimeout(r, 3000))]);
        if (!customElements.get(widget.element)) throw new Error(`l'élément <${widget.element}> n'est jamais défini.`);
    }
    return document.createElement(widget.element);
}

function alimenter(carte) {
    const entities = {};
    const states = {};
    for (const [slot, id] of Object.entries(carte.liens)) {
        if (!id) continue;
        entities[slot] = hote.entites.get(id) ?? { entity_id: id, name: id, unknown: true };
        states[slot] = hote.etats.get(id) ?? null;
    }
    // Le nom propre à la carte remplace celui de l'entité principale (le premier slot, ou l'unique du widget de secours).
    const titre = carte.carte.title;
    const principal = carte.carte.principal in carte.liens ? carte.carte.principal : Object.keys(carte.liens)[0];
    if (titre && entities[principal]) entities[principal] = { ...entities[principal], name: titre };
    try {
        carte.element.settings = carte.carte.settings ?? {};
        carte.element.entities = entities;
        carte.element.states = states;
    } catch (e) {
        console.error(`Carte ${carte.carte.id} :`, e);
    }
}

/** L'hôte `kv` d'une carte : un widget ne vise que les entités de ses slots (WIDGETS.md §5). */
function hoteDeCarte(carte, liens) {
    return Object.freeze({
        async command(slot, command, args) {
            const id = liens[slot];
            if (!id) return { outcome: "refused", verification: "not_sent", detail: `slot « ${slot} » non lié` };
            try {
                const reponse = await fetch(`/api/entities/${encodeURIComponent(id)}/commands`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ command, args, source: `widget:${carte.widget}` }),
                });
                const corps = await reponse.json();
                return reponse.ok ? corps : { outcome: "refused", verification: "not_sent", detail: corps.error };
            } catch (e) {
                return { outcome: "unreachable", verification: "not_sent", detail: String(e?.message ?? e) };
            }
        },
        /**
         * La courbe d'un slot (WIDGETS.md §5) : [{ value, measured_at }], du plus ancien au plus récent. Au-delà de
         * 3 jours, une valeur par heure (moyenne pondérée par le temps), avec min et max. `options.field` choisit le
         * champ d'un objet (thermostat : "temperature", "target", "power", "outdoor_temperature"…).
         */
        async history(slot, from, to, options = {}) {
            const id = liens[slot];
            if (!id) throw new Error(`slot « ${slot} » non lié`);
            const p = new URLSearchParams({ from: new Date(from).toISOString(), to: new Date(to).toISOString() });
            if (options.field) p.set("field", options.field);
            const reponse = await fetch(`/api/entities/${encodeURIComponent(id)}/history?${p}`);
            const corps = await reponse.json();
            if (!reponse.ok) throw new Error(corps.error ?? `historique indisponible (${reponse.status})`);
            return corps.points.map(pt => ({ value: pt.v, measured_at: pt.t, min: pt.min, max: pt.max }));
        },
        openEntity(slot) {
            if (liens[slot]) location.href = "/entites";
        },
        /** Ouvre la fenêtre d'historique de l'entité d'un slot (HISTORIQUE.md §5) ; sans effet hors d'une page Krissvia. */
        openHistory(slot) {
            if (liens[slot] && hote.dotnet) hote.dotnet.invokeMethodAsync("OuvrirHistorique", liens[slot]);
        },
    });
}
