using System.Globalization;
using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>Le forçage de l'absence : rien (la période programmée et la détection décident), présent, ou absent.</summary>
public enum Forcage
{
    Automatique,
    Present,
    Absent,
}

/// <summary>
/// La présence et l'absence du foyer, telles que le cœur les combine (<c>GET /api/absence</c>, PRESENCE.md) : l'absence
/// qui s'applique aux thermostats et qui en décide (forçage, période programmée, détection), et la présence détectée
/// appareil par appareil. Aucune règle refaite ici : l'appli met des mots sur les codes du serveur.
/// </summary>
/// <param name="Absente">L'absence qui s'applique aux thermostats.</param>
/// <param name="Source"><c>none</c>, <c>detection</c>, <c>scheduled</c> ou <c>manual</c>.</param>
/// <param name="Retour">Retour prévu d'une période programmée en cours.</param>
/// <param name="DetectionPerdue">Un appareil suivi sans valeur : maison tenue pour occupée par sécurité.</param>
/// <param name="Periode">La période programmée, en cours ou à venir ; null sans période.</param>
public sealed record EtatPresence(
    bool Absente,
    string Source,
    DateTimeOffset? Retour,
    Forcage Forcage,
    bool DetectionPerdue,
    IReadOnlyList<string> Presents,
    IReadOnlyList<string> Absents,
    IReadOnlyList<string> Inconnus,
    DateTimeOffset? VideDepuis,
    PeriodeAbsence? Periode)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public int Suivis => Presents.Count + Absents.Count + Inconnus.Count;

    public string Libelle => Absente
        ? Source switch { "manual" => "Absence forcée", "scheduled" => "Absence programmée", _ => "Maison vide" }
        : Forcage == Forcage.Present ? "Présence forcée"
        : DetectionPerdue ? "Présence inconnue"
        : "Maison occupée";

    /// <summary>Qui en décide, en une ligne.</summary>
    public string Explication => (Absente, Source) switch
    {
        (_, "manual") => "Forcée à la main : l'emporte sur tout, jusqu'au retour en automatique.",
        (true, "scheduled") => Retour is { } r ? $"Retour prévu {r.ToLocalTime().ToString("ddd dd/MM à HH:mm", Fr)}." : "Période en cours.",
        (true, _) => VideDepuis is { } v ? $"Détection de présence : vide depuis {v.ToLocalTime():HH:mm}." : "Détection de présence.",
        _ when DetectionPerdue => "Détection perdue : maison tenue pour occupée par sécurité.",
        _ when Suivis == 0 => "Aucun appareil suivi : choisissez-les dans les réglages du plugin Livebox.",
        _ => "Détection de présence.",
    };

    public static EtatPresence? Lire(JsonElement json)
    {
        var dto = json.Deserialize<ReponseDto>(Thermostat.Json);
        if (dto is null) return null;

        var p = dto.Presence;
        var periode = dto.Period is { } d && !d.Cancelled ? new PeriodeAbsence(d.Departure, d.Return) : null;
        return new EtatPresence(
            dto.State?.Away ?? false,
            dto.State?.Source ?? "none",
            dto.State?.Return,
            dto.Forced switch { true => Forcage.Absent, false => Forcage.Present, null => Forcage.Automatique },
            p?.DetectionLost ?? false,
            p?.Present ?? [],
            p?.Absent ?? [],
            p?.Unknown ?? [],
            p?.EmptySince,
            periode);
    }

    private sealed record ReponseDto(EtatDto? State, PeriodeDto? Period, bool? Forced, PresenceDto? Presence);

    private sealed record EtatDto(bool Away, string? Source, DateTimeOffset? Return);

    private sealed record PeriodeDto(DateTimeOffset Departure, DateTimeOffset Return, bool Cancelled);

    private sealed record PresenceDto(bool DetectionLost, List<string>? Present, List<string>? Absent, List<string>? Unknown, DateTimeOffset? EmptySince);
}

/// <summary>Une absence programmée, du départ au retour.</summary>
public sealed record PeriodeAbsence(DateTimeOffset Depart, DateTimeOffset Retour);
