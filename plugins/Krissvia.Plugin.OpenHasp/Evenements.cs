using System.Text.Json;
using Krissvia.Contracts;

namespace Krissvia.Plugin.OpenHasp;

/// <summary>Ce qu'un message de la platine demande de faire.</summary>
public abstract record Reaction;

/// <summary>Un ordre pour le cœur, sur une entité choisie dans les réglages (contrat 0.3).</summary>
public sealed record Ordre(string Entite, string Commande, JsonElement? Args = null) : Reaction;

/// <summary>Le curseur de luminosité a bougé (valeur déjà bornée).</summary>
public sealed record ChangerLuminosite(int Valeur) : Reaction;

/// <summary>Une touche du digicode : un chiffre, « C » ou « &lt; ». Le code est tenu par le plugin, pas par la réaction.</summary>
public sealed record ToucheClavier(string Touche) : Reaction;

/// <summary>Armer ou désarmer avec le code saisi sur la platine (<c>arm</c> / <c>disarm</c>).</summary>
public sealed record CommandeAlarme(string Commande) : Reaction;

/// <summary>La platine annonce sa veille : « short », « long » ou « off » (réveil).</summary>
public sealed record Veille(string Etat) : Reaction;

/// <summary>
/// Traduit un message <c>hasp/{nœud}/state/{objet}</c> en réaction. Fonction pure : l'état courant des entités
/// suffit à décider (allumer ou éteindre, activer ou suspendre le planning).
/// </summary>
public static class Evenements
{
    public const double PasConsigne = 0.5;

    public static Reaction? Interpreter(Platine platine, Reglages reglages, string objet, string charge, IReadOnlyDictionary<string, EntityStateUpdate> etats)
    {
        if (objet == "idle") return Luminosite.EtatVeille(charge) is { } veille ? new Veille(veille) : null;

        string? evenement;
        int? valeur = null;
        try
        {
            using var doc = JsonDocument.Parse(charge);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("event", out var e) || e.ValueKind != JsonValueKind.String) return null;
            evenement = e.GetString();
            if (doc.RootElement.TryGetProperty("val", out var v) && v.TryGetInt32(out var n)) valeur = n;
        }
        catch (JsonException)
        {
            return null;
        }

        // Le curseur émet « changed » pendant le glissement et « up » au relâché : on suit les deux, pour que la dalle
        // réagisse sous le doigt (le filtre de republication absorbe les répétitions).
        if (objet == $"p{Objets.PageHorloge}b{Objets.Curseur}")
            return valeur is { } brillance && evenement is "changed" or "up" ? new ChangerLuminosite(Luminosite.Borner(brillance)) : null;

        // Un bouton n'agit qu'au relâché : « down » puis « up » ne doivent pas faire deux ordres.
        if (evenement != "up") return null;

        if (platine.Thermostat is { Length: > 0 } thermostat)
        {
            foreach (var (mode, numero, _, _, _) in Objets.Modes)
                if (objet == Nom(Objets.PageThermostat, numero))
                    return new Ordre(thermostat, StandardCommands.SetMode, Args(new { mode }));

            if (objet == Nom(Objets.PageThermostat, Objets.Moins))
                return new Ordre(thermostat, StandardCommands.AdjustTarget, Args(new { delta = -PasConsigne }));
            if (objet == Nom(Objets.PageThermostat, Objets.Plus))
                return new Ordre(thermostat, StandardCommands.AdjustTarget, Args(new { delta = PasConsigne }));
            if (objet == Nom(Objets.PageThermostat, Objets.Planning))
            {
                var actif = Climat.Lire(etats.GetValueOrDefault(thermostat))?.Planning == true;
                return new Ordre(thermostat, StandardCommands.SetSchedule, Args(new { active = !actif }));
            }
        }

        if (platine.ADigicode(reglages))
        {
            for (var d = 0; d <= 9; d++)
                if (objet == Nom(Objets.PageAlarme, Objets.Chiffre(d))) return new ToucheClavier(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (objet == Nom(Objets.PageAlarme, Objets.Effacer)) return new ToucheClavier("C");
            if (objet == Nom(Objets.PageAlarme, Objets.Retour)) return new ToucheClavier("<");
            if (objet == Nom(Objets.PageAlarme, Objets.Armer)) return new CommandeAlarme(StandardCommands.Arm);
            if (objet == Nom(Objets.PageAlarme, Objets.Desarmer)) return new CommandeAlarme(StandardCommands.Disarm);
        }

        var interrupteurs = platine.InterrupteursAffiches();
        for (var i = 0; i < interrupteurs.Count; i++)
        {
            if (objet != Nom(Objets.PageHorloge, Objets.BoutonInterrupteur(i))) continue;
            // Pas de « toggle » : tous les appareils ne le portent pas (un relais Shelly, par exemple). Inconnu : allumer.
            var allume = Ecran.EstAllume(etats.GetValueOrDefault(interrupteurs[i].Entite)) == true;
            return new Ordre(interrupteurs[i].Entite, allume ? StandardCommands.TurnOff : StandardCommands.TurnOn);
        }

        return null;
    }

    private static string Nom(int page, int objet) => $"p{page}b{objet}";

    private static JsonElement Args(object valeur) => JsonSerializer.SerializeToElement(valeur);
}
