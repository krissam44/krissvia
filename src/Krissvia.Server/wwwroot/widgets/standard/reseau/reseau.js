import { KvWidget, esc, absence, icone } from "../commun.js";

/**
 * Grille « Appareils vus par la box » de DomoMud : nom, adresse MAC, IP, état, et la case « Suivi » qui fait d'un
 * appareil une présence du foyer (commande set_tracked de l'entité). Recherche et filtre « connectés » ; la saisie
 * survit aux mises à jour de la liste.
 */
class KvReseau extends KvWidget {
    static css = `
        .entete { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; flex-wrap: wrap; }
        .entete .ms { font-size: 1.4rem; }
        .entete .nom { font-weight: 600; font-size: 1.05rem; flex: 1; white-space: nowrap; }
        .outils { display: flex; align-items: center; gap: 10px; font-size: .8rem; color: var(--kv-text-secondary, #777); }
        .outils input[type=search] { font: inherit; padding: 4px 8px; border-radius: 6px; border: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 40%, transparent);
            background: transparent; color: var(--kv-text, #222); width: 180px; }
        .table { flex: 1 1 0; flex-shrink: 1; min-height: 0; overflow: auto; border-top: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 20%, transparent); }
        table { width: 100%; border-collapse: collapse; font-size: .85rem; }
        th { position: sticky; top: 0; background: var(--kv-surface, #fff); text-align: left; font-weight: 600; padding: 8px; z-index: 1; }
        td { padding: 6px 8px; border-top: 1px solid color-mix(in srgb, var(--kv-text-secondary, #888) 15%, transparent); white-space: nowrap; }
        td.mac { font-family: ui-monospace, Consolas, monospace; font-size: .75rem; }
        .etat { padding: 1px 8px; border-radius: 10px; font-size: .72rem; border: 1px solid currentColor; }
        th.suivi, td.suivi { text-align: center; width: 60px; }
        td.suivi input { width: 18px; height: 18px; cursor: pointer; accent-color: #66bb6a; }
        tr.suivi td:first-child { font-weight: 600; }`;

    #filtre = "";
    #connectesSeuls = false;
    #attente = new Set();   // MAC dont la commande est en cours
    #erreur = null;

    rendu() {
        const entite = this._entities.reseau;
        const etat = this._states.reseau;
        const hotes = Array.isArray(etat?.value?.hosts) ? etat.value.hosts : [];
        const manque = absence(etat, entite);
        const connectes = hotes.filter(h => h.active).length;
        const entete = `<div class="entete">${icone("devices")}<span class="nom">Appareils vus par la box (${hotes.length})</span>
            <div class="outils">
                <input type="search" placeholder="Rechercher…" value="${esc(this.#filtre)}" aria-label="Rechercher un appareil">
                <label><input type="checkbox" class="seuls" ${this.#connectesSeuls ? "checked" : ""}> connectés (${connectes})</label>
            </div></div>`;
        if (manque) return `<div class="carte">${entete}${manque}</div>`;

        const f = this.#filtre.trim().toLowerCase();
        const lignes = hotes
            .filter(h => !this.#connectesSeuls || h.active)
            .filter(h => !f || [h.name, h.mac, h.ip].some(x => String(x ?? "").toLowerCase().includes(f)))
            // Les suivis d'abord, puis par nom : c'est eux qu'on vient regarder.
            .sort((a, b) => (b.tracked - a.tracked) || String(a.name).localeCompare(String(b.name), "fr", { sensitivity: "base" }))
            .map(h => {
                const c = h.active ? "#66bb6a" : "#78909c";
                return `<tr class="${h.tracked ? "suivi" : ""}">
                    <td>${esc(h.name)}</td><td class="mac">${esc(h.mac)}</td><td>${esc(h.ip ?? "")}</td>
                    <td><span class="etat" style="color:${c}">${h.active ? "Connecté" : "Déconnecté"}</span></td>
                    <td class="suivi"><input type="checkbox" data-mac="${esc(h.mac)}" ${h.tracked ? "checked" : ""} ${this.#attente.has(h.mac) ? "disabled" : ""}
                        aria-label="Suivre la présence de ${esc(h.name)}"></td></tr>`;
            }).join("");

        return `<div class="carte">${entete}
            ${this.#erreur ? `<div class="erreur">${esc(this.#erreur)}</div>` : ""}
            <div class="table"><table>
                <thead><tr><th>Appareil</th><th>Adresse MAC</th><th>IP</th><th>État</th><th class="suivi">Suivi</th></tr></thead>
                <tbody>${lignes}</tbody>
            </table></div></div>`;
    }

    apresRendu(racine) {
        const recherche = racine.querySelector("input[type=search]");
        recherche?.addEventListener("input", () => {
            this.#filtre = recherche.value;
            const position = recherche.selectionStart;
            this.planifier();
            // Le rendu remplace le champ : on lui rend le focus et le curseur, sinon la saisie s'interromprait.
            queueMicrotask(() => queueMicrotask(() => {
                const nouveau = this.shadowRoot?.querySelector("input[type=search]");
                nouveau?.focus();
                nouveau?.setSelectionRange(position, position);
            }));
        });
        racine.querySelector("input.seuls")?.addEventListener("change", e => { this.#connectesSeuls = e.target.checked; this.planifier(); });
        racine.querySelectorAll("td.suivi input").forEach(c => c.addEventListener("change", () => this.suivre(c.dataset.mac, c.checked)));
    }

    async suivre(mac, oui) {
        if (!this._kv) return;
        this.#attente.add(mac);
        this.#erreur = null;
        this.planifier();
        const r = await this._kv.command("reseau", "set_tracked", { mac, tracked: oui });
        this.#attente.delete(mac);
        if (r.outcome !== "accepted") this.#erreur = r.detail ?? "Ordre refusé.";
        this.planifier();
    }
}

customElements.define("kv-reseau", KvReseau);
