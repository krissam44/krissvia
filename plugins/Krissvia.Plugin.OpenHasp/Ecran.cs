using System.Globalization;
using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>
/// Ce qu'une platine doit montrer, attribut par attribut (<c>p1b2.text</c> → « 19.5° »), à partir des états suivis.
/// Fonction pure : le filtre de republication décide ensuite de ce qui part vraiment.
/// </summary>
public static class Ecran
{
    private const string Inactif = "#3A3A3C";
    private const string TexteInactif = "#999999";
    private const string TexteActif = "#FFFFFF";
    private const string Gris = "#888888";
    private const string PlanningActif = "#0A84FF";
    private const string Allume = "#FFD600";
    private const string TexteAllume = "#1C1C1E";
    private const string Rouge = "#EF5350";

    /// <summary>Couleurs du tableau de bord (widget thermostat), donc de la carte de DomoMud.</summary>
    private static readonly IReadOnlyDictionary<string, string> CouleursModes = new Dictionary<string, string>
    {
        ["comfort"] = "#FF5722", ["comfort1"] = "#FFA726", ["comfort2"] = "#FFD60A",
        ["eco"] = "#66BB6A", ["frost_protection"] = "#42A5F5", ["off"] = "#546E7A",
    };

    private static readonly IReadOnlyDictionary<string, string> CouleursStatuts = new Dictionary<string, string>
    {
        ["heating"] = "#FF5722", ["preheating"] = "#AB47BC", ["coasting"] = "#26C6DA", ["idle"] = "#66BB6A", ["off"] = "#546E7A",
        ["inhibited"] = "#FFA726", ["window_open"] = "#42A5F5", ["sensor_lost"] = Rouge, ["unknown_switch"] = Rouge,
    };

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<(string Attribut, string Valeur)> Attributs(
        Platine platine, Reglages reglages, IReadOnlyDictionary<string, EntityStateUpdate> etats, DateTime maintenantLocal,
        EtatClavier? clavier = null)
    {
        var a = new List<(string, string)>();
        string Lisible(string c) => CouleurOpenHasp.Lisible(c, reglages.Affichage.CouleurFond, reglages.Affichage.ContrasteMinimum);
        EntityStateUpdate? Etat(string? id) => id is { Length: > 0 } && etats.TryGetValue(id, out var e) ? e : null;

        // ── Page 1 : thermostat ──
        var thermostat = Etat(platine.Thermostat);
        var climat = Climat.Lire(thermostat);
        if (platine.AThermostat)
        {
            const int p = Objets.PageThermostat;
            a.Add((Objets.Attribut(p, Objets.Titre, "text"), platine.Piece is { Length: > 0 } piece ? piece : thermostat?.Name ?? ""));
            a.Add((Objets.Attribut(p, Objets.Ambiante, "text"), Degres(climat?.Temperature)));
            a.Add((Objets.Attribut(p, Objets.Consigne, "text"), Degres(climat?.Consigne)));

            // Consigne ajustée au +/- : aucun mode ne la décrit plus, aucun bouton n'est allumé (comme DomoMud).
            var couleurMode = climat is { Ajustee: false, Mode: { } m } ? CouleursModes.GetValueOrDefault(m, Gris) : Gris;
            a.Add((Objets.Attribut(p, Objets.Mode, "text"), climat switch
            {
                null => "Mode: --",
                { Ajustee: true } => "Mode: consigne ajustée",
                _ => $"Mode: {LibelleMode(climat.Mode)}",
            }));
            a.Add((Objets.Attribut(p, Objets.Mode, "text_color"), Lisible(couleurMode)));

            var couleurStatut = climat is null ? Rouge : CouleursStatuts.GetValueOrDefault(climat.Statut ?? "", Gris);
            a.Add((Objets.Attribut(p, Objets.Statut, "text"), climat is null ? "Statut: indisponible" : $"Statut: {climat.LibelleStatut ?? "--"}"));
            a.Add((Objets.Attribut(p, Objets.Statut, "text_color"), Lisible(couleurStatut)));

            // Le bandeau suit le relais, pas le mode : un Confort qui a atteint sa consigne ne chauffe pas.
            var chauffe = climat?.Chauffe == true;
            a.Add((Objets.Attribut(p, Objets.BandeauChauffe, "hidden"), chauffe ? "0" : "1"));
            if (chauffe) a.Add((Objets.Attribut(p, Objets.BandeauChauffe, "bg_color"), couleurStatut));

            // Les boutons sont des aplats : couleur du mode telle quelle, seul le texte passe par le contraste.
            foreach (var (mode, objet, _) in Objets.Modes)
            {
                var actif = climat is { Ajustee: false } && climat.Mode == mode;
                a.Add((Objets.Attribut(p, objet, "bg_color"), actif ? CouleursModes[mode] : Inactif));
                a.Add((Objets.Attribut(p, objet, "text_color"), actif ? TexteActif : TexteInactif));
            }

            var planning = climat?.Planning == true;
            a.Add((Objets.Attribut(p, Objets.Planning, "bg_color"), planning ? PlanningActif : Inactif));
            a.Add((Objets.Attribut(p, Objets.Planning, "text_color"), planning ? TexteActif : TexteInactif));
        }

        // ── Page 2 : températures (les mêmes sur toutes les platines) ──
        var sondes = reglages.SondesAffichees().ToList();
        for (var i = 0; i < sondes.Count; i++)
        {
            const int p = Objets.PageTemperatures;
            var s = sondes[i];
            var etat = Etat(s.Entite);
            var t = etat is { Available: true, Value: { ValueKind: JsonValueKind.Number } v } ? v.GetDouble() : (double?)null;
            a.Add((Objets.Attribut(p, Objets.NomSonde(i), "text"), s.Nom is { Length: > 0 } nom ? nom : etat?.Name ?? s.Entite));
            a.Add((Objets.Attribut(p, Objets.ValeurSonde(i), "text"), Degres(t)));
            var min = (int)Math.Floor(s.Min);
            var max = (int)Math.Ceiling(s.Max);
            a.Add((Objets.Attribut(p, Objets.ArcSonde(i), "val"),
                (t is { } x ? (int)Math.Round(Math.Clamp(x, min, Math.Max(min, max))) : min).ToString(CultureInfo.InvariantCulture)));
            if (t is { } temp) a.Add((Objets.Attribut(p, Objets.ArcSonde(i), "line_color10"), CouleurTemperature(temp)));
        }

        // ── Page 3 : horloge ──
        {
            const int p = Objets.PageHorloge;
            var exterieure = Etat(reglages.Affichage.Exterieure) is { Available: true, Value: { ValueKind: JsonValueKind.Number } ve } ? ve.GetDouble() : (double?)null;
            a.Add((Objets.Attribut(p, Objets.Date, "text"), LigneDate(maintenantLocal, exterieure)));
            a.Add((Objets.Attribut(p, Objets.ProchaineBascule, "text"), climat?.Prochaine is { } prochaine
                ? $"{LibelleMode(prochaine.Mode)} à {prochaine.A:HH:mm}"
                : ""));

            var interrupteurs = platine.InterrupteursAffiches();
            for (var i = 0; i < interrupteurs.Count; i++)
            {
                var it = interrupteurs[i];
                var etat = Etat(it.Entite);
                var allume = EstAllume(etat) == true;
                a.Add((Objets.Attribut(p, Objets.BoutonInterrupteur(i), "text"), it.Nom is { Length: > 0 } nom ? nom : etat?.Name ?? it.Entite));
                a.Add((Objets.Attribut(p, Objets.BoutonInterrupteur(i), "bg_color"), allume ? Allume : Inactif));
                a.Add((Objets.Attribut(p, Objets.BoutonInterrupteur(i), "text_color"), allume ? TexteAllume : TexteInactif));
            }
        }

        // ── Page 4 : digicode ──
        if (platine.ADigicode(reglages))
        {
            const int p = Objets.PageAlarme;
            var alarme = EtatAlarme.Lire(Etat(reglages.Alarme.Entite));
            var (texte, couleur) = alarme is null ? ("Alarme indisponible", Rouge) : alarme.Affiche();
            a.Add((Objets.Attribut(p, Objets.EtatAlarme, "text"), texte));
            a.Add((Objets.Attribut(p, Objets.EtatAlarme, "text_color"), Lisible(couleur)));
            a.Add((Objets.Attribut(p, Objets.DetailAlarme, "text"), clavier?.Message ?? alarme?.Detail() ?? ""));
            a.Add((Objets.Attribut(p, Objets.CodeSaisi, "text"), clavier?.Masque ?? ""));

            // Le bouton utile ressort ; l'autre reste actif : c'est le cœur qui accepte ou refuse, pas la platine.
            var desarmee = alarme?.Etat is null or "disarmed";
            a.Add((Objets.Attribut(p, Objets.Armer, "bg_color"), desarmee ? PlanningActif : Inactif));
            a.Add((Objets.Attribut(p, Objets.Armer, "text_color"), desarmee ? TexteActif : TexteInactif));
            a.Add((Objets.Attribut(p, Objets.Desarmer, "bg_color"), desarmee ? Inactif : "#43A047"));
            a.Add((Objets.Attribut(p, Objets.Desarmer, "text_color"), desarmee ? TexteInactif : TexteActif));
        }

        return a;
    }

    /// <summary>
    /// Faut-il amener le digicode sous les yeux ? À l'entrée dans le décompte d'entrée ou au déclenchement, pas à
    /// chaque publication (le décompte change chaque seconde), ni au premier état reçu : un plugin qui redémarre
    /// pendant une alarme déclenchée depuis des heures n'a pas à repasser toutes les platines sur le clavier.
    /// </summary>
    public static bool AppelleLeClavier(string? avant, string? apres) =>
        avant is not null && apres is "entry_delay" or "triggered" && avant != apres;

    /// <summary>« Jeudi 22 oct. - dehors 12.4° » : date et extérieur sur une ligne, faute de place sous l'horloge.</summary>
    internal static string LigneDate(DateTime jour, double? exterieure)
    {
        var date = jour.ToString("dddd d MMM", Francais);
        date = char.ToUpper(date[0], Francais) + date[1..];
        return exterieure is { } t ? $"{date} - dehors {Degres(t)}" : date;
    }

    internal static string Degres(double? t) =>
        t is { } v ? $"{v.ToString("0.0", CultureInfo.InvariantCulture)}°" : "--.-°";

    /// <summary>Les libellés de la platine, en ASCII pour le « - » : le « − » du cœur n'est pas dans sa police.</summary>
    internal static string LibelleMode(string? mode) =>
        Objets.Modes.FirstOrDefault(m => m.Mode == mode).Libelle ?? mode ?? "--";

    private static string CouleurTemperature(double t) => t switch
    {
        < 10 => "#1565C0",
        < 16 => "#42A5F5",
        < 19 => "#66BB6A",
        < 22 => "#FF8C00",
        _ => "#FF5722",
    };

    /// <summary>Interrupteur : un booléen ; lumière : <c>{"on": …}</c>. Null si on ne sait pas.</summary>
    internal static bool? EstAllume(EntityStateUpdate? etat) => etat is { Available: true, Value: { } v }
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object when v.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False => on.GetBoolean(),
            _ => null,
        }
        : null;
}

/// <summary>L'état de l'entité d'alarme du cœur, réduit à ce qu'affiche le digicode.</summary>
public sealed record EtatAlarme(string? Etat, int Decompte, bool Sonne, string? Capteur, string? Defauts)
{
    public static EtatAlarme? Lire(EntityStateUpdate? etat)
    {
        if (etat is not { Available: true, Value: { ValueKind: JsonValueKind.Object } v }) return null;
        string? Texte(string nom) => v.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new EtatAlarme(Texte("state"),
            v.TryGetProperty("countdown", out var d) && d.TryGetInt32(out var n) ? n : 0,
            v.TryGetProperty("ringing", out var s) && s.ValueKind == JsonValueKind.True,
            Texte("sensor"), Texte("faults"));
    }

    public (string Texte, string Couleur) Affiche() => Etat switch
    {
        "disarmed" => ("Désarmée", "#66BB6A"),
        "exit_delay" => ($"Sortie : {Decompte} s", "#FFA726"),
        "armed" => ("Armée", "#42A5F5"),
        "entry_delay" => ($"Entrée : {Decompte} s", "#FFA726"),
        "triggered" => (Sonne ? "ALARME - sirène" : "ALARME", "#EF5350"),
        _ => (Etat ?? "--", "#888888"),
    };

    /// <summary>Le capteur en cause pendant une entrée ou un déclenchement ; sinon un défaut, s'il y en a.</summary>
    public string? Detail() => Etat is "entry_delay" or "triggered" && Capteur is { Length: > 0 } c
        ? c
        : Defauts is { Length: > 0 } f && f != "OK" ? $"Défaut : {f}" : null;
}

/// <summary>L'état d'une entité <c>climate</c> du cœur, réduit à ce qu'affiche une platine.</summary>
public sealed record Climat(
    double? Temperature, double? Consigne, string? Mode, bool Ajustee, bool Planning, bool Chauffe, string? Statut,
    string? LibelleStatut, (DateTime A, string Mode)? Prochaine)
{
    /// <summary>Null si l'entité est indisponible ou n'a jamais publié : « indisponible » vaut mieux qu'un état inventé.</summary>
    public static Climat? Lire(EntityStateUpdate? etat)
    {
        if (etat is not { Available: true, Value: { ValueKind: JsonValueKind.Object } v }) return null;

        (DateTime, string)? prochaine = null;
        if (v.TryGetProperty("next_change", out var n) && n.ValueKind == JsonValueKind.Object
            && Texte(n, "at") is { } at && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var quand)
            && Texte(n, "mode") is { } mode)
            prochaine = (quand, mode);

        return new Climat(Nombre(v, "temperature"), Nombre(v, "target"), Texte(v, "mode"), Vrai(v, "target_adjusted"),
            Vrai(v, "schedule_active"), Vrai(v, "heating"), Texte(v, "status"), Texte(v, "status_label"), prochaine);
    }

    private static double? Nombre(JsonElement o, string nom) =>
        o.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static string? Texte(JsonElement o, string nom) =>
        o.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Vrai(JsonElement o, string nom) => o.TryGetProperty(nom, out var p) && p.ValueKind == JsonValueKind.True;
}
