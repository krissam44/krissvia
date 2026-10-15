using System.Text.Json;
using System.Text.Json.Nodes;
using Krissvia.Contracts;

namespace Krissvia.Plugin.Zigbee2Mqtt.Z2m;

/// <summary>Ordre à publier sur <c>{appareil}/set</c>, ou la raison pour laquelle il n'existe pas.</summary>
internal sealed record OrdreZ2m(JsonObject? Charge, string? Erreur)
{
    public static OrdreZ2m Ok(JsonObject charge) => new(charge, null);
    public static OrdreZ2m Refus(string erreur) => new(null, erreur);
}

/// <summary>
/// Lien entre une entité du contrat et les propriétés Zigbee2MQTT qui la portent : comment lire sa valeur dans
/// un message d'état, comment traduire un ordre en charge <c>/set</c>.
/// </summary>
internal abstract class EntityBinding(string uniqueId, string? nom)
{
    public string UniqueId => uniqueId;
    protected string? Nom => nom;

    public abstract EntityDescriptor Descriptor { get; }

    /// <summary>Valeur de l'entité dans ce message, ou null s'il ne la contient pas.</summary>
    /// <param name="precedente">Dernière valeur connue : une lumière ou un volet se reconstitue par morceaux.</param>
    public abstract JsonElement? Lire(JsonElement etat, JsonElement? precedente);

    /// <summary>
    /// Un événement (appui de bouton) n'est jamais rejoué : ni gardé en cache, ni renvoyé à la resynchronisation,
    /// sinon chaque reconnexion au cœur « rappuierait » sur le bouton.
    /// </summary>
    public virtual bool EstEvenement => false;

    public virtual OrdreZ2m Commander(string commande, JsonElement? args) =>
        OrdreZ2m.Refus($"commande « {commande} » non prise en charge par cette entité");

    protected static JsonElement Element<T>(T valeur) => JsonSerializer.SerializeToElement(valeur);

    protected static bool Egal(JsonElement a, JsonElement? b) => b is { } v && JsonElement.DeepEquals(a, v);

    protected static JsonNode? Noeud(JsonElement? e) => e is { } v ? JsonNode.Parse(v.GetRawText()) : null;
}

/// <summary>Mesure numérique (température, puissance…), convertie dans l'unité du contrat.</summary>
internal sealed class MesureBinding(
    string uniqueId, string? nom, string propriete, string? classe, string? unite, StateClass classeEtat, double facteur)
    : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new()
    {
        UniqueId = UniqueId,
        Kind = EntityKind.Sensor,
        Name = Nom,
        DeviceClass = classe,
        Unit = unite,
        StateClass = classeEtat,
    };

    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente)
    {
        if (!etat.TryGetProperty(propriete, out var v) || !v.TryGetDouble(out var nombre)) return null;
        return Element(facteur == 1 ? nombre : Math.Round(nombre * facteur, 6));
    }
}

/// <summary>
/// Capteur tout-ou-rien, true = état actif (ouvert, présence, fuite…). Zigbee2MQTT le dit lui-même par
/// <c>value_on</c> — pour un contact, <c>value_on: false</c>, car « contact » vaut true quand c'est FERMÉ. Ce n'est
/// qu'en l'absence de <c>value_on</c> que la convention connue s'applique, <paramref name="inverse"/> compris :
/// inverser les deux fois retournerait toutes les portes.
/// </summary>
internal sealed class EtatBinaireBinding(
    string uniqueId, string? nom, string propriete, string? classe, JsonElement? valeurVrai, bool inverse)
    : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new()
    {
        UniqueId = UniqueId,
        Kind = EntityKind.BinarySensor,
        Name = Nom,
        DeviceClass = classe,
    };

    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente)
    {
        if (!etat.TryGetProperty(propriete, out var v) || v.ValueKind == JsonValueKind.Null) return null;

        var actif = valeurVrai is { } on
            ? JsonElement.DeepEquals(v, on)
            : (v.ValueKind == JsonValueKind.True) != inverse;
        return Element(actif);
    }
}

/// <summary>Relais : valeur booléenne, ordres marche / arrêt / bascule.</summary>
internal sealed class InterrupteurBinding(
    string uniqueId, string? nom, string propriete, JsonElement? valeurOn, JsonElement? valeurOff, JsonElement? valeurBascule)
    : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new()
    {
        UniqueId = UniqueId,
        Kind = EntityKind.Switch,
        Name = Nom,
        Commands = [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle],
    };

    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente) =>
        etat.TryGetProperty(propriete, out var v) && v.ValueKind != JsonValueKind.Null
            ? Element(Egal(v, valeurOn ?? Element("ON")))
            : null;

    public override OrdreZ2m Commander(string commande, JsonElement? args) => commande switch
    {
        StandardCommands.TurnOn => OrdreZ2m.Ok(new JsonObject { [propriete] = Noeud(valeurOn) ?? "ON" }),
        StandardCommands.TurnOff => OrdreZ2m.Ok(new JsonObject { [propriete] = Noeud(valeurOff) ?? "OFF" }),
        StandardCommands.Toggle => OrdreZ2m.Ok(new JsonObject { [propriete] = Noeud(valeurBascule) ?? "TOGGLE" }),
        _ => base.Commander(commande, args),
    };
}

/// <summary>Lumière : valeur <c>{"on": bool, "brightness": 0..100}</c>. Zigbee2MQTT compte la luminosité de 0 à 254.</summary>
internal sealed class LumiereBinding(
    string uniqueId, string? nom, string proprieteEtat, JsonElement? valeurOn, string? proprieteLuminosite, double luminositeMax)
    : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new()
    {
        UniqueId = UniqueId,
        Kind = EntityKind.Light,
        Name = Nom,
        Commands = proprieteLuminosite is null
            ? [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle]
            : [StandardCommands.TurnOn, StandardCommands.TurnOff, StandardCommands.Toggle, StandardCommands.SetBrightness],
    };

    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente)
    {
        var aEtat = etat.TryGetProperty(proprieteEtat, out var e) && e.ValueKind != JsonValueKind.Null;
        var aLuminosite = proprieteLuminosite is not null &&
                          etat.TryGetProperty(proprieteLuminosite, out var l) && l.TryGetDouble(out _);
        if (!aEtat && !aLuminosite) return null;

        var valeur = new JsonObject();
        if (precedente is { ValueKind: JsonValueKind.Object } p)
            foreach (var champ in p.EnumerateObject()) valeur[champ.Name] = JsonNode.Parse(champ.Value.GetRawText());

        if (aEtat) valeur["on"] = Egal(e, valeurOn ?? Element("ON"));
        if (aLuminosite)
            valeur["brightness"] = (int)Math.Round(etat.GetProperty(proprieteLuminosite!).GetDouble() * 100 / luminositeMax);

        return Element(valeur);
    }

    public override OrdreZ2m Commander(string commande, JsonElement? args)
    {
        switch (commande)
        {
            case StandardCommands.TurnOn: return OrdreZ2m.Ok(new JsonObject { [proprieteEtat] = "ON" });
            case StandardCommands.TurnOff: return OrdreZ2m.Ok(new JsonObject { [proprieteEtat] = "OFF" });
            case StandardCommands.Toggle: return OrdreZ2m.Ok(new JsonObject { [proprieteEtat] = "TOGGLE" });
            case StandardCommands.SetBrightness when proprieteLuminosite is not null:
                if (args is not { ValueKind: JsonValueKind.Object } a ||
                    !a.TryGetProperty("brightness", out var b) || !b.TryGetDouble(out var pourcent) ||
                    pourcent is < 0 or > 100)
                    return OrdreZ2m.Refus("set_brightness attend {\"brightness\": 0..100}");

                // 0 % éteint : envoyer une luminosité nulle laisse certaines ampoules allumées au minimum.
                return pourcent == 0
                    ? OrdreZ2m.Ok(new JsonObject { [proprieteEtat] = "OFF" })
                    : OrdreZ2m.Ok(new JsonObject
                    {
                        [proprieteEtat] = "ON",
                        [proprieteLuminosite] = (int)Math.Max(1, Math.Round(pourcent * luminositeMax / 100)),
                    });
            default:
                return base.Commander(commande, args);
        }
    }
}

/// <summary>Volet : valeur <c>{"state": "open"|"closed"|…, "position": 0..100}</c>.</summary>
internal sealed class VoletBinding(string uniqueId, string? nom, string? proprieteEtat, string? proprietePosition)
    : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new()
    {
        UniqueId = UniqueId,
        Kind = EntityKind.Cover,
        Name = Nom,
        Commands = proprietePosition is null
            ? [StandardCommands.Open, StandardCommands.Close, StandardCommands.Stop]
            : [StandardCommands.Open, StandardCommands.Close, StandardCommands.Stop, StandardCommands.SetPosition],
    };

    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente)
    {
        var aEtat = proprieteEtat is not null && etat.TryGetProperty(proprieteEtat, out var e) && e.ValueKind == JsonValueKind.String;
        var aPosition = proprietePosition is not null && etat.TryGetProperty(proprietePosition, out var p) && p.TryGetDouble(out _);
        if (!aEtat && !aPosition) return null;

        var valeur = new JsonObject();
        if (precedente is { ValueKind: JsonValueKind.Object } avant)
            foreach (var champ in avant.EnumerateObject()) valeur[champ.Name] = JsonNode.Parse(champ.Value.GetRawText());

        if (aEtat) valeur["state"] = etat.GetProperty(proprieteEtat!).GetString()!.ToLowerInvariant();
        if (aPosition) valeur["position"] = etat.GetProperty(proprietePosition!).GetDouble();

        return Element(valeur);
    }

    public override OrdreZ2m Commander(string commande, JsonElement? args)
    {
        if (commande == StandardCommands.SetPosition && proprietePosition is not null)
        {
            return args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("position", out var p) &&
                   p.TryGetDouble(out var position) && position is >= 0 and <= 100
                ? OrdreZ2m.Ok(new JsonObject { [proprietePosition] = position })
                : OrdreZ2m.Refus("set_position attend {\"position\": 0..100}");
        }

        var ordre = commande switch
        {
            StandardCommands.Open => "OPEN",
            StandardCommands.Close => "CLOSE",
            StandardCommands.Stop => "STOP",
            _ => null,
        };

        return ordre is not null && proprieteEtat is not null
            ? OrdreZ2m.Ok(new JsonObject { [proprieteEtat] = ordre })
            : base.Commander(commande, args);
    }
}

/// <summary>Action d'une télécommande ou d'un bouton (« single », « double », « hold »…).</summary>
internal sealed class EvenementBinding(string uniqueId, string? nom, string propriete) : EntityBinding(uniqueId, nom)
{
    public override EntityDescriptor Descriptor => new() { UniqueId = UniqueId, Kind = EntityKind.Event, Name = Nom };

    public override bool EstEvenement => true;

    // Zigbee2MQTT publie parfois une action vide pour « remettre à zéro » : ce n'est pas un appui.
    public override JsonElement? Lire(JsonElement etat, JsonElement? precedente) =>
        etat.TryGetProperty(propriete, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } action
            ? Element(action)
            : null;
}
