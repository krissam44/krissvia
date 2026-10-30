using System.Text.Json;

namespace Krissvia.Mobile.Client;

/// <summary>
/// L'alarme telle que le serveur la décrit (<c>GET /api/alarm</c>, ALARME.md §6). L'état (<c>disarmed</c>,
/// <c>exit_delay</c>…), le décompte, le capteur en cause sont ceux de la centrale du cœur ; l'appli ne fait que les
/// afficher.
/// </summary>
/// <param name="Activee">Faux : l'alarme est désactivée dans ses réglages, aucun code n'y fera rien.</param>
/// <param name="Decompte">Secondes restantes d'une sortie ou d'une entrée.</param>
/// <param name="DernierRefus">Pourquoi le dernier armement a échoué (porte ouverte, capteur muet) ; null sinon.</param>
/// <param name="DefautsMaintenant">Ce qui empêcherait d'armer en ce moment : à voir AVANT de taper son code.</param>
/// <param name="BloqueSecondes">Clavier bloqué après trois codes faux : secondes restantes, 0 sinon.</param>
public sealed record EtatAlarme(
    bool Activee,
    string Etat,
    int Decompte,
    bool Sonne,
    string? Capteur,
    DateTimeOffset? DeclencheeA,
    string? DernierRefus,
    IReadOnlyList<string> DefautsMaintenant,
    int BloqueSecondes)
{
    /// <summary>Désarmée : le bouton arme. Dans tout autre état (sortie, armée, entrée, déclenchée), il désarme.</summary>
    public bool Desarmee => Etat == "disarmed";

    /// <summary>Un décompte court : l'écran se relit chaque seconde pour le suivre.</summary>
    public bool EnDecompte => Etat is "exit_delay" or "entry_delay";

    public string Libelle => Etat switch
    {
        "disarmed" => "Désarmée",
        "exit_delay" => "Sortie en cours",
        "armed" => "Armée",
        "entry_delay" => "Entrée en cours",
        "triggered" => "Déclenchée",
        _ => Etat,
    };

    public static EtatAlarme? Lire(JsonElement json)
    {
        var dto = json.Deserialize<ReponseDto>(Thermostat.Json);
        if (dto is null) return null;
        var etat = dto.State is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<EtatDto>(Thermostat.Json) : null;

        return new EtatAlarme(
            dto.Enabled,
            etat?.State ?? "disarmed",
            etat?.Countdown ?? 0,
            etat?.Ringing ?? false,
            etat?.Sensor is { Length: > 0 } capteur ? capteur : null,
            etat?.TriggeredAt,
            // « OK » est la valeur de la centrale quand rien n'a été refusé.
            etat?.Faults is { Length: > 0 } f && f != "OK" ? f : null,
            dto.FaultsNow ?? [],
            dto.LockedSeconds);
    }

    private sealed record ReponseDto(bool Enabled, JsonElement? State, List<string>? FaultsNow, int LockedSeconds);

    private sealed record EtatDto(string? State, int Countdown, bool Ringing, string? Sensor, DateTimeOffset? TriggeredAt, string? Faults);
}

/// <summary>Le résultat d'un code tapé : <see cref="Message"/> s'affiche tel quel (nom reconnu, ou motif du refus).</summary>
public sealed record ResultatCode(bool Ok, string Message);
