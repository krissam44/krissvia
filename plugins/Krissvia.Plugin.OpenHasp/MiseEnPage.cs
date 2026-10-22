using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>Les numéros de page et d'objet : la mise en page les pose, l'affichage et les appuis s'y réfèrent.</summary>
public static class Objets
{
    public const int PageThermostat = 1;
    public const int PageTemperatures = 2;
    public const int PageHorloge = 3;

    /// <summary>Fond plein écran, déclaré en premier sur chaque page (l'ordre des lignes est l'ordre d'empilement).</summary>
    public const int Fond = 50;

    // Page 1 : thermostat.
    public const int Titre = 1, Ambiante = 2, Moins = 5, LibelleConsigne = 6, Consigne = 7, Plus = 8, Statut = 9,
        Mode = 20, Planning = 21, Panneau = 30, BandeauChauffe = 18;

    /// <summary>Boutons de mode : identifiant de mode du cœur → objet, dans l'ordre de la platine déployée.</summary>
    public static readonly IReadOnlyList<(string Mode, int Objet, string Libelle)> Modes =
    [
        ("off", 10, "Off"), ("frost_protection", 11, "Hors-gel"), ("eco", 12, "Éco"),
        ("comfort2", 13, "Confort -2"), ("comfort1", 14, "Confort -1"), ("comfort", 15, "Confort"),
    ];

    // Page 2 : températures. La sonde i a son nom en 2i+1, sa valeur en 2i+2 et son arc en 20+i.
    public const int TitreTemperatures = 30;
    public static int NomSonde(int i) => 2 * i + 1;
    public static int ValeurSonde(int i) => 2 * i + 2;
    public static int ArcSonde(int i) => 20 + i;

    // Page 3 : horloge.
    public const int Heure = 40, Date = 41, ProchaineBascule = 42, Curseur = 20, Pourcentage = 21, LibelleLuminosite = 23;
    public static int BoutonInterrupteur(int i) => 10 + i;

    public static string Attribut(int page, int objet, string attribut) => $"p{page}b{objet}.{attribut}";
}

/// <summary>
/// Les pages d'une platine Guition 480×480, générées depuis les réglages : plus de <c>pages.jsonl</c> à déposer ni
/// d'identifiants d'objets à recopier dans une configuration. Fonction pure, poussée à chaque <c>online</c> de la platine.
/// </summary>
/// <remarks>
/// Polices 16, 24, 32 et 48 seulement : celles de la mise en page déployée sur les platines de l'appartement. Pas
/// d'icône MDI non plus, pour la même raison ; les séparateurs restent en ASCII (« · » sort en carré vide).
/// </remarks>
public static class MiseEnPage
{
    public const int Cote = 480;
    private const string Panneau = "#2C2C2E";
    private const string Bouton = "#3A3A3C";
    private const string TexteInactif = "#999999";
    private const string Bleu = "#0A84FF";
    private const string Accent = "#64B5F6";
    private const string Secondaire = "#888888";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Les pages présentes sur cette platine, dans l'ordre de la barre de navigation.</summary>
    public static IReadOnlyList<int> PagesPresentes(Platine platine, Reglages reglages)
    {
        var pages = new List<int>();
        if (platine.AThermostat) pages.Add(Objets.PageThermostat);
        if (reglages.SondesAffichees().Any()) pages.Add(Objets.PageTemperatures);
        pages.Add(Objets.PageHorloge);
        return pages;
    }

    /// <summary>Les lignes JSONL de toutes les pages, page 0 (navigation) comprise.</summary>
    public static IReadOnlyList<string> Generer(Platine platine, Reglages reglages)
    {
        var lignes = new List<JsonObject>();
        var pages = PagesPresentes(platine, reglages);
        Navigation(lignes, pages);
        if (platine.AThermostat) Thermostat(lignes, reglages.Affichage);
        if (pages.Contains(Objets.PageTemperatures)) Temperatures(lignes, reglages);
        Horloge(lignes, platine, reglages.Affichage);
        return [.. lignes.Select(l => l.ToJsonString(Json))];
    }

    private static void Navigation(List<JsonObject> lignes, IReadOnlyList<int> pages)
    {
        const int marge = 5, ecart = 5;
        var largeur = (Cote - 2 * marge - ecart * (pages.Count - 1)) / pages.Count;
        for (var i = 0; i < pages.Count; i++)
        {
            var nom = pages[i] switch
            {
                Objets.PageThermostat => "Thermostat",
                Objets.PageTemperatures => "Températures",
                _ => "Horloge",
            };
            var o = Btn(0, i + 1, marge + i * (largeur + ecart), 435, largeur, 42, nom, 16, Panneau, "#FFFFFF", 10);
            o["action"] = $"p{pages[i]}";
            lignes.Add(o);
        }
    }

    private static void Thermostat(List<JsonObject> l, Affichage a)
    {
        const int p = Objets.PageThermostat;
        l.Add(Fond(p, a.CouleurFond));
        l.Add(Libelle(p, Objets.Titre, 90, 8, 300, 40, "", 24, Accent));
        l.Add(Btn(p, Objets.Planning, 400, 8, 72, 38, "Prog", 16, Bouton, TexteInactif, 10));
        l.Add(Libelle(p, Objets.Ambiante, 0, 50, Cote, 64, "--.-°", 48, "#FFFFFF"));
        l.Add(Libelle(p, Objets.Mode, 0, 118, Cote, 28, "Mode: --", 16, Secondaire));
        l.Add(Libelle(p, Objets.Statut, 0, 146, Cote, 28, "Statut: --", 16, Secondaire));

        var panneau = Objet(p, Objets.Panneau, "obj", 20, 182, 440, 100);
        panneau["bg_color"] = Panneau;
        panneau["radius"] = 18;
        panneau["border_width"] = 0;
        panneau["click"] = false;
        l.Add(panneau);
        l.Add(Btn(p, Objets.Moins, 34, 192, 96, 80, "-", 48, Bleu, "#FFFFFF", 12));
        l.Add(Libelle(p, Objets.LibelleConsigne, 130, 196, 220, 26, "Consigne", 16, Secondaire));
        l.Add(Libelle(p, Objets.Consigne, 130, 224, 220, 44, "--.-°", 32, "#FFFFFF"));
        l.Add(Btn(p, Objets.Plus, 350, 192, 96, 80, "+", 48, Bleu, "#FFFFFF", 12));

        // Bandeau de chauffe : masqué au repos, coloré quand le relais est fermé.
        var bandeau = Objet(p, Objets.BandeauChauffe, "obj", 20, 286, 440, 6);
        bandeau["bg_color"] = "#FF5722";
        bandeau["radius"] = 3;
        bandeau["border_width"] = 0;
        bandeau["click"] = false;
        bandeau["hidden"] = true;
        l.Add(bandeau);

        for (var i = 0; i < Objets.Modes.Count; i++)
        {
            var (_, objet, libelle) = Objets.Modes[i];
            l.Add(Btn(p, objet, 20 + i % 3 * 150, i < 3 ? 298 : 364, 140, 56, libelle, 16, Bouton, TexteInactif, 10));
        }
    }

    private static void Temperatures(List<JsonObject> l, Reglages r)
    {
        const int p = Objets.PageTemperatures;
        l.Add(Fond(p, r.Affichage.CouleurFond));
        l.Add(Libelle(p, Objets.TitreTemperatures, 0, 4, Cote, 30, "Températures", 24, Accent));

        var sondes = r.SondesAffichees().ToList();
        foreach (var (i, x, y) in Grille(sondes.Count))
        {
            var s = sondes[i];
            var arc = Objet(p, Objets.ArcSonde(i), "arc", x, y, 140, 140);
            arc["min"] = (int)Math.Floor(s.Min);
            arc["max"] = (int)Math.Ceiling(s.Max);
            arc["val"] = (int)Math.Floor(s.Min);
            arc["line_width"] = 12;
            arc["line_width10"] = 12;
            arc["line_color"] = Bouton;
            // L'indicateur d'un arc est sa partie 10 : « line_color10 », pas « arc_color » qu'openHASP ignore en silence.
            arc["line_color10"] = Bleu;
            arc["bg_opa"] = 0;
            arc["border_width"] = 0;
            arc["click"] = false;
            l.Add(arc);
            l.Add(Libelle(p, Objets.ValeurSonde(i), x, y + 50, 140, 40, "--.-°", 32, "#FFFFFF"));
            l.Add(Libelle(p, Objets.NomSonde(i), x, y + 140, 140, 26, "", 16, "#AAAAAA"));
        }
    }

    /// <summary>Rangées de trois ; la dernière, incomplète, est centrée (cinq sondes : trois puis deux).</summary>
    internal static IEnumerable<(int Index, int X, int Y)> Grille(int nombre)
    {
        for (var i = 0; i < nombre; i++)
        {
            var rangee = i / 3;
            var dansRangee = Math.Min(3, nombre - rangee * 3);
            var largeur = dansRangee * 140 + (dansRangee - 1) * 15;
            yield return (i, (Cote - largeur) / 2 + i % 3 * 155, 40 + rangee * 175);
        }
    }

    private static void Horloge(List<JsonObject> l, Platine platine, Affichage a)
    {
        const int p = Objets.PageHorloge;
        l.Add(Fond(p, a.CouleurFond));
        var heure = Libelle(p, Objets.Heure, 0, 40, Cote, 70, "--:--", 48, "#FFFFFF");
        heure["template"] = "%H:%M";   // l'heure vient de la platine (NTP) : rien à pousser chaque minute
        l.Add(heure);
        l.Add(Libelle(p, Objets.Date, 0, 118, Cote, 36, "", 24, "#AAAAAA"));
        l.Add(Libelle(p, Objets.ProchaineBascule, 0, 158, Cote, 28, "", 16, Secondaire));

        var interrupteurs = platine.InterrupteursAffiches();
        var (largeur, ecart) = interrupteurs.Count switch { 1 => (200, 0), 2 => (200, 20), _ => (140, 15) };
        var total = interrupteurs.Count * largeur + (interrupteurs.Count - 1) * ecart;
        for (var i = 0; i < interrupteurs.Count; i++)
            l.Add(Btn(p, Objets.BoutonInterrupteur(i), (Cote - total) / 2 + i * (largeur + ecart), 210, largeur, 104, "", 24, Bouton, TexteInactif, 20));

        l.Add(Libelle(p, Objets.LibelleLuminosite, 0, 334, Cote, 26, "Luminosité", 16, Secondaire));
        var curseur = Objet(p, Objets.Curseur, "slider", 20, 370, 350, 30);
        curseur["min"] = Luminosite.Minimum;
        curseur["max"] = Luminosite.Maximum;
        curseur["val"] = Luminosite.Borner(a.LuminositeDefaut);
        curseur["bg_color"] = Bouton;
        curseur["bg_color10"] = Bleu;
        curseur["bg_color20"] = "#FFFFFF";
        curseur["radius"] = 8;
        curseur["border_width"] = 0;
        l.Add(curseur);
        l.Add(Libelle(p, Objets.Pourcentage, 385, 365, 75, 40, "", 16, "#FFFFFF"));
    }

    // ── Briques ──────────────────────────────────────────────────────────────

    private static JsonObject Objet(int page, int id, string type, int x, int y, int w, int h) =>
        new() { ["page"] = page, ["id"] = id, ["obj"] = type, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };

    /// <summary>
    /// Fond : un objet plein écran, pas le <c>bg_color</c> de <c>id:0</c> (le fond sombre qu'on croyait lui devoir venait
    /// du thème). Uni : les deux extrémités du dégradé identiques et <c>bg_grad_dir</c> à 0, sinon le thème ajoute le sien.
    /// </summary>
    private static JsonObject Fond(int page, string couleur)
    {
        var o = Objet(page, Objets.Fond, "obj", 0, 0, Cote, Cote);
        o["bg_color"] = couleur;
        o["bg_grad_color"] = couleur;
        o["bg_grad_dir"] = 0;
        o["bg_opa"] = 255;
        o["radius"] = 0;
        o["border_width"] = 0;
        o["click"] = false;
        return o;
    }

    private static JsonObject Libelle(int page, int id, int x, int y, int w, int h, string texte, int police, string couleur)
    {
        var o = Objet(page, id, "label", x, y, w, h);
        o["text"] = texte;
        o["align"] = "center";
        o["text_font"] = police;
        o["text_color"] = couleur;
        o["bg_opa"] = 0;
        o["border_width"] = 0;
        return o;
    }

    private static JsonObject Btn(int page, int id, int x, int y, int w, int h, string texte, int police, string fond, string couleur, int rayon)
    {
        var o = Objet(page, id, "btn", x, y, w, h);
        o["text"] = texte;
        o["text_font"] = police;
        o["bg_color"] = fond;
        o["text_color"] = couleur;
        o["radius"] = rayon;
        o["border_width"] = 0;
        return o;
    }
}
