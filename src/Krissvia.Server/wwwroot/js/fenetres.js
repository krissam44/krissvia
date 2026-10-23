// Fenêtres déplaçables : on attrape la barre de titre d'un MudDialog (souris, stylet ou doigt) et la fenêtre suit.
// MudBlazor 9 ne le propose pas ; un seul écouteur sur le document vaut pour toutes les fenêtres, présentes et à venir,
// sans rien changer dans leurs composants. La barre de titre reste toujours à l'écran : une fenêtre lâchée trop loin
// ne doit pas devenir impossible à rattraper.
(() => {
    const MARGE = 80;      // ce qui reste visible de la fenêtre sur les côtés
    const TITRE = 40;      // hauteur gardée visible en bas
    let glisse = null;

    const borner = (v, min, max) => Math.min(Math.max(v, min), max);

    document.addEventListener("pointerdown", e => {
        if (e.button !== 0 || !(e.target instanceof Element)) return;
        const titre = e.target.closest(".mud-dialog .mud-dialog-title");
        // Les boutons de la barre (fermer) gardent leur clic.
        if (!titre || e.target.closest("button, a, input, select, textarea, [role=button]")) return;

        const fenetre = titre.closest(".mud-dialog");
        const [x, y] = (fenetre.dataset.kvDeplacement ?? "0,0").split(",").map(Number);
        glisse = { fenetre, titre, depart: [e.clientX, e.clientY], base: [x, y], cadre: fenetre.getBoundingClientRect(), pointeur: e.pointerId };
        titre.setPointerCapture(e.pointerId);
        e.preventDefault();
    });

    document.addEventListener("pointermove", e => {
        if (!glisse || e.pointerId !== glisse.pointeur) return;
        const { cadre, depart, base } = glisse;
        const dx = borner(e.clientX - depart[0], MARGE - cadre.right, innerWidth - MARGE - cadre.left);
        const dy = borner(e.clientY - depart[1], -cadre.top, innerHeight - TITRE - cadre.top);
        const x = base[0] + dx, y = base[1] + dy;
        glisse.fenetre.dataset.kvDeplacement = `${x},${y}`;
        glisse.fenetre.style.translate = `${x}px ${y}px`;
    });

    const lacher = e => {
        if (!glisse || e.pointerId !== glisse.pointeur) return;
        glisse.titre.releasePointerCapture?.(e.pointerId);
        glisse = null;
    };
    document.addEventListener("pointerup", lacher);
    document.addEventListener("pointercancel", lacher);
})();
