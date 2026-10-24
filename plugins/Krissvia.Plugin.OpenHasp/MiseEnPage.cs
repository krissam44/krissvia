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
    public const int PageAlarme = 4;

    /// <summary>Fond plein écran, déclaré en premier sur chaque page (l'ordre des lignes est l'ordre d'empilement).</summary>
    public const int Fond = 50;

    // Page 1 : thermostat. Les icônes sont des objets à part : le texte voisin est réécrit à chaque rafraîchissement.
    public const int Titre = 1, Ambiante = 2, IconeTitre = 3, IconeAmbiante = 4, Moins = 5, LibelleConsigne = 6, Consigne = 7,
        Plus = 8, Statut = 9, IconeMode = 16, IconeStatut = 17, Mode = 20, Planning = 21, Panneau = 30, BandeauChauffe = 18;

    /// <summary>
    /// Boutons de mode, dans l'ordre de l'écran de DomoMud : du plus chaud (Confort, en haut à gauche) à l'arrêt. Le
    /// libellé complet sert à la ligne « Mode: », le court au bouton, l'icône (MDI) aux deux.
    /// </summary>
    public static readonly IReadOnlyList<(string Mode, int Objet, string Libelle, string Court, string Icone)> Modes =
    [
        ("comfort", 15, "Confort", "Confort", Icones.Flamme), ("comfort1", 14, "Confort -1", "Conf. -1", Icones.Flamme),
        ("comfort2", 13, "Confort -2", "Conf. -2", Icones.Flamme), ("eco", 12, "Éco", "Éco", Icones.Feuille),
        ("frost_protection", 11, "Hors-gel", "Hors-gel", Icones.Flocon), ("off", 10, "Off", "Off", Icones.Arret),
    ];

    // Page 2 : températures. La sonde i a son nom en 2i+1, sa valeur en 2i+2 et son arc en 20+i.
    public const int TitreTemperatures = 30;
    public static int NomSonde(int i) => 2 * i + 1;
    public static int ValeurSonde(int i) => 2 * i + 2;
    public static int ArcSonde(int i) => 20 + i;

    // Page 3 : horloge.
    public const int Heure = 40, Date = 41, ProchaineBascule = 42, Curseur = 20, Pourcentage = 21, LibelleLuminosite = 23;
    public static int BoutonInterrupteur(int i) => 10 + i;

    // Page 4 : digicode. Le chiffre d est l'objet 10+d.
    public const int EtatAlarme = 1, DetailAlarme = 2, CodeSaisi = 3, Effacer = 20, Retour = 21, Armer = 30, Desarmer = 31;
    public static int Chiffre(int d) => 10 + d;

    public static string Attribut(int page, int objet, string attribut) => $"p{page}b{objet}.{attribut}";
}

/// <summary>
/// Icônes Material Design de la police des platines : le codepoint MDI F0xxxx s'écrit Exxxx (on retire « F0 »). Toutes
/// figurent dans le sous-ensemble compilé dans openHASP et s'affichaient déjà sur l'écran de DomoMud.
/// </summary>
public static class Icones
{
    public const string Thermometre = "\uE50F", Calendrier = "\uE0ED", Flamme = "\uE238", Radiateur = "\uE438",
        Moins = "\uE374", Plus = "\uE415", Feuille = "\uE32A", Flocon = "\uE717", Arret = "\uE425",
        Goutte = "\uE58E", Horloge = "\uE150", Bouclier = "\uE99D", Soleil = "\uE5A8";
}

/// <summary>
/// Les pages d'une platine Guition 480×480, générées depuis les réglages : plus de <c>pages.jsonl</c> à déposer ni
/// d'identifiants d'objets à recopier dans une configuration. Fonction pure, poussée à chaque <c>online</c> de la platine.
/// </summary>
/// <remarks>
/// Les tailles de police de l'écran de DomoMud (20, 28, et 84 pour l'heure) : le firmware des Guition (ESP32-S3) rend
/// toute taille par FreeType. Chacune coûte un cache de glyphes, d'où la liste fermée <see cref="Polices"/>. Les icônes
/// sont celles de <see cref="Icones"/> ; les séparateurs restent en ASCII (« · » sort en carré vide).
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

    /// <summary>Les seules tailles employées : une taille de plus, c'est un cache de glyphes de plus sur l'ESP32.</summary>
    public static readonly IReadOnlySet<int> Polices = new HashSet<int> { 16, 20, 24, 28, 32, 48, 84 };

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Les pages présentes sur cette platine, dans l'ordre de la barre de navigation.</summary>
    public static IReadOnlyList<int> PagesPresentes(Platine platine, Reglages reglages)
    {
        var pages = new List<int>();
        if (platine.AThermostat) pages.Add(Objets.PageThermostat);
        if (reglages.SondesAffichees().Any()) pages.Add(Objets.PageTemperatures);
        pages.Add(Objets.PageHorloge);
        if (platine.ADigicode(reglages)) pages.Add(Objets.PageAlarme);
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
        if (pages.Contains(Objets.PageAlarme)) Digicode(lignes, reglages.Affichage);
        return [.. lignes.Select(l => l.ToJsonString(Json))];
    }

    private static void Navigation(List<JsonObject> lignes, IReadOnlyList<int> pages)
    {
        const int marge = 5, ecart = 5;
        var largeur = (Cote - 2 * marge - ecart * (pages.Count - 1)) / pages.Count;
        for (var i = 0; i < pages.Count; i++)
        {
            var (nom, icone) = pages[i] switch
            {
                Objets.PageThermostat => ("Thermostat", Icones.Thermometre),
                Objets.PageTemperatures => ("Sondes", Icones.Goutte),
                Objets.PageAlarme => ("Alarme", Icones.Bouclier),
                _ => ("Horloge", Icones.Horloge),
            };
            // L'icône devant le nom, comme DomoMud, tant qu'il y a la place : à quatre boutons de 113 px, le nom seul.
            var texte = pages.Count <= 3 ? $"{icone} {nom}" : nom;
            var o = Btn(0, i + 1, marge + i * (largeur + ecart), 435, largeur, 42, texte, 20, Panneau, "#FFFFFF", 10);
            o["action"] = $"p{pages[i]}";
            lignes.Add(o);
        }
    }

    /// <summary>
    /// L'écran de DomoMud : en-tête icône / pièce / planning, l'ambiante en grand, mode et statut avec leur icône, le
    /// panneau de consigne un ton au-dessus du fond, puis les six modes de Confort à Off.
    /// </summary>
    private static void Thermostat(List<JsonObject> l, Affichage a)
    {
        const int p = Objets.PageThermostat;
        l.Add(Fond(p, a.CouleurFond));
        // Les deux encadrants du titre font 48 de large avec la même marge : le titre est centré pile sur l'écran.
        l.Add(Libelle(p, Objets.IconeTitre, 10, 8, 48, 40, Icones.Thermometre, 28, Accent));
        l.Add(Libelle(p, Objets.Titre, 58, 8, 364, 40, "", 28, Accent));
        l.Add(Btn(p, Objets.Planning, 422, 8, 48, 38, Icones.Calendrier, 24, Bouton, TexteInactif, 10));

        l.Add(Libelle(p, Objets.IconeAmbiante, 140, 62, 40, 44, Icones.Thermometre, 32, Accent));
        l.Add(Libelle(p, Objets.Ambiante, 180, 50, 160, 64, "--.-°", 48, "#FFFFFF"));

        // Icône + texte dans une boîte de largeur fixe : un centrage plein écran ferait danser l'icône avec le texte.
        l.Add(Libelle(p, Objets.IconeMode, 124, 118, 28, 28, Icones.Flamme, 20, Secondaire));
        l.Add(Ligne(Libelle(p, Objets.Mode, 152, 118, 204, 28, "Mode: --", 20, Secondaire)));
        l.Add(Libelle(p, Objets.IconeStatut, 124, 146, 28, 28, Icones.Radiateur, 20, Secondaire));
        l.Add(Ligne(Libelle(p, Objets.Statut, 152, 146, 204, 28, "Statut: --", 20, Secondaire)));

        var panneau = Objet(p, Objets.Panneau, "obj", 20, 182, 440, 84);
        panneau["bg_color"] = CouleurOpenHasp.Eclaircir(a.CouleurFond, 0.15);
        panneau["bg_grad_color"] = panneau["bg_color"]!.DeepClone();
        panneau["radius"] = 18;
        panneau["border_width"] = 0;
        panneau["click"] = false;
        l.Add(panneau);
        l.Add(Btn(p, Objets.Moins, 40, 192, 76, 64, Icones.Moins, 28, Bleu, "#FFFFFF", 12));
        l.Add(Libelle(p, Objets.LibelleConsigne, 120, 191, 240, 22, "Consigne", 20, Secondaire));
        l.Add(Libelle(p, Objets.Consigne, 120, 213, 240, 44, "--.-°", 32, "#FFFFFF"));
        l.Add(Btn(p, Objets.Plus, 364, 192, 76, 64, Icones.Plus, 28, Bleu, "#FFFFFF", 12));

        // Bandeau de chauffe : masqué au repos, coloré quand le relais est fermé.
        var bandeau = Objet(p, Objets.BandeauChauffe, "obj", 20, 272, 440, 6);
        bandeau["bg_color"] = "#FF5722";
        bandeau["radius"] = 3;
        bandeau["border_width"] = 0;
        bandeau["click"] = false;
        bandeau["hidden"] = true;
        l.Add(bandeau);

        for (var i = 0; i < Objets.Modes.Count; i++)
        {
            var (_, objet, _, court, icone) = Objets.Modes[i];
            l.Add(Btn(p, objet, 20 + i % 3 * 150, i < 3 ? 284 : 350, 140, 56, $"{icone} {court}", 20, Bouton, TexteInactif, 10));
        }
    }

    private static void Temperatures(List<JsonObject> l, Reglages r)
    {
        const int p = Objets.PageTemperatures;
        l.Add(Fond(p, r.Affichage.CouleurFond));
        l.Add(Libelle(p, Objets.TitreTemperatures, 0, 10, Cote, 34, $"{Icones.Goutte}  Températures", 24, Accent));

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
            l.Add(Libelle(p, Objets.NomSonde(i), x, y + 140, 140, 26, "", 20, "#AAAAAA"));
        }
    }

    /// <summary>
    /// Rangées de trois ; la dernière, incomplète, est centrée (cinq sondes : trois puis deux). L'ensemble est centré
    /// en hauteur entre le titre et la barre de navigation, au lieu d'être collé sous le titre.
    /// </summary>
    internal static IEnumerable<(int Index, int X, int Y)> Grille(int nombre)
    {
        const int sousTitre = 44, barre = 435, hauteurSonde = 166, pasRangee = 175;
        var rangees = (nombre + 2) / 3;
        var hauteur = rangees * hauteurSonde + (rangees - 1) * (pasRangee - hauteurSonde);
        var haut = sousTitre + (barre - sousTitre - hauteur) / 2;
        for (var i = 0; i < nombre; i++)
        {
            var rangee = i / 3;
            var dansRangee = Math.Min(3, nombre - rangee * 3);
            var largeur = dansRangee * 140 + (dansRangee - 1) * 15;
            yield return (i, (Cote - largeur) / 2 + i % 3 * 155, haut + rangee * pasRangee);
        }
    }

    private static void Horloge(List<JsonObject> l, Platine platine, Affichage a)
    {
        const int p = Objets.PageHorloge;
        l.Add(Fond(p, a.CouleurFond));
        var heure = Libelle(p, Objets.Heure, 0, 20, Cote, 104, "--:--", 84, "#FFFFFF");
        heure["template"] = "%H:%M";   // l'heure vient de la platine (NTP) : rien à pousser chaque minute
        l.Add(heure);
        l.Add(Libelle(p, Objets.Date, 0, 126, Cote, 34, "", 24, "#AAAAAA"));
        l.Add(Libelle(p, Objets.ProchaineBascule, 0, 162, Cote, 28, "", 20, Secondaire));

        var interrupteurs = platine.InterrupteursAffiches();
        var (largeur, ecart) = interrupteurs.Count switch { 1 => (200, 0), 2 => (200, 20), _ => (140, 15) };
        var total = interrupteurs.Count * largeur + (interrupteurs.Count - 1) * ecart;
        for (var i = 0; i < interrupteurs.Count; i++)
            l.Add(Btn(p, Objets.BoutonInterrupteur(i), (Cote - total) / 2 + i * (largeur + ecart), 204, largeur, 104, "", 24, Bouton, TexteInactif, 20));

        l.Add(Libelle(p, Objets.LibelleLuminosite, 0, 326, Cote, 28, $"{Icones.Soleil} Luminosité", 20, Secondaire));
        var curseur = Objet(p, Objets.Curseur, "slider", 20, 366, 350, 30);
        curseur["min"] = Luminosite.Minimum;
        curseur["max"] = Luminosite.Maximum;
        curseur["val"] = Luminosite.Borner(a.LuminositeDefaut);
        curseur["bg_color"] = Bouton;
        curseur["bg_color10"] = Bleu;
        curseur["bg_color20"] = "#FFFFFF";
        curseur["radius"] = 8;
        curseur["border_width"] = 0;
        l.Add(curseur);
        // Le pourcentage dès l'affichage, celui de la position du curseur : il n'attend plus qu'on y touche.
        l.Add(Libelle(p, Objets.Pourcentage, 385, 361, 75, 40, Luminosite.Libelle(Luminosite.Borner(a.LuminositeDefaut)), 20, "#FFFFFF"));
    }

    /// <summary>
    /// Le digicode : l'état en grand, une ligne de détail, le code masqué, un clavier 3×4 et les deux ordres. Des
    /// boutons séparés plutôt qu'un <c>btnmatrix</c> : chaque touche arrive alors comme n'importe quel appui.
    /// </summary>
    private static void Digicode(List<JsonObject> l, Affichage a)
    {
        const int p = Objets.PageAlarme;
        l.Add(Fond(p, a.CouleurFond));
        l.Add(Libelle(p, Objets.EtatAlarme, 0, 6, Cote, 40, "--", 32, "#FFFFFF"));
        l.Add(Libelle(p, Objets.DetailAlarme, 0, 48, Cote, 26, "", 16, Secondaire));
        l.Add(Libelle(p, Objets.CodeSaisi, 20, 78, 286, 42, "", 32, "#FFFFFF"));

        string[] touches = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "C", "0", "<"];
        for (var i = 0; i < touches.Length; i++)
        {
            var objet = touches[i] switch { "C" => Objets.Effacer, "<" => Objets.Retour, var d => Objets.Chiffre(d[0] - '0') };
            l.Add(Btn(p, objet, 20 + i % 3 * 98, 126 + i / 3 * 74, 90, 66, touches[i], 32, Panneau, "#FFFFFF", 12));
        }

        l.Add(Btn(p, Objets.Armer, 326, 126, 134, 140, "Armer", 24, Bouton, TexteInactif, 14));
        l.Add(Btn(p, Objets.Desarmer, 326, 274, 134, 140, "Désarmer", 24, Bouton, TexteInactif, 14));
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

    /// <summary>Un texte de longueur variable reste sur une ligne, terminé par « … » s'il déborde.</summary>
    private static JsonObject Ligne(JsonObject libelle)
    {
        libelle["mode"] = "dots";
        return libelle;
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
