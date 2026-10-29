namespace Krissvia.Plugin.OpenHasp;

/// <summary>Les réglages de la page Services (plugin.json), liés depuis la configuration .NET.</summary>
public sealed class Reglages
{
    public List<Platine> Platines { get; set; } = [];
    public List<SondeAffichee> Sondes { get; set; } = [];
    public Affichage Affichage { get; set; } = new();
    public Alarme Alarme { get; set; } = new();

    /// <summary>Au-delà, la page n'a plus la place : 3 interrupteurs sous l'horloge, 6 arcs en 2 rangées de 3.</summary>
    public const int MaxInterrupteurs = 3;
    public const int MaxSondes = 6;

    /// <summary>Toutes les entités à suivre, sans doublon : ce qu'on demande au cœur (contrat 0.3).</summary>
    public IReadOnlyList<string> Entites() =>
    [
        .. Platines.SelectMany(p => p.InterrupteursAffiches().Select(i => i.Entite).Prepend(p.Thermostat)
                .Concat(p.APagePerso ? p.AffichagesPerso.Select(a => a.Entite).Concat(p.BoutonsPerso.Select(b => b.Entite)) : []))
            .Concat(SondesAffichees().Select(s => s.Entite))
            .Append(Affichage.Exterieure)
            .Append(Alarme.Entite)
            .OfType<string>()
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal),
    ];

    public IEnumerable<SondeAffichee> SondesAffichees() => Sondes.Where(s => !string.IsNullOrEmpty(s.Entite)).Take(MaxSondes);
}

public sealed class Platine
{
    /// <summary>Nom de nœud openHASP : <c>hasp/{Noeud}/…</c>.</summary>
    public string Noeud { get; set; } = "";
    public string? Piece { get; set; }
    public string? Thermostat { get; set; }
    public List<Interrupteur> Interrupteurs { get; set; } = [];

    /// <summary>Page 4, le digicode : sur toutes les platines par défaut, dès qu'une alarme est choisie.</summary>
    public bool Digicode { get; set; } = true;

    /// <summary>Page 5, écrite par l'utilisateur en JSONL (<see cref="OpenHasp.PagePerso"/>). Vide : pas de page perso.</summary>
    public string? PagePerso { get; set; }

    /// <summary>Le libellé de la page perso dans la barre du bas ; vide : « Perso ».</summary>
    public string? NomPagePerso { get; set; }

    public List<AffichagePerso> AffichagesPerso { get; set; } = [];
    public List<BoutonPerso> BoutonsPerso { get; set; } = [];

    public bool AThermostat => !string.IsNullOrEmpty(Thermostat);

    public bool APagePerso => !string.IsNullOrWhiteSpace(PagePerso);

    public bool ADigicode(Reglages reglages) => Digicode && !string.IsNullOrEmpty(reglages.Alarme.Entite);

    public IReadOnlyList<Interrupteur> InterrupteursAffiches() =>
        [.. Interrupteurs.Where(i => !string.IsNullOrEmpty(i.Entite)).Take(Reglages.MaxInterrupteurs)];
}

public sealed class Interrupteur
{
    public string Entite { get; set; } = "";
    public string? Nom { get; set; }
}

public sealed class SondeAffichee
{
    public string Entite { get; set; } = "";
    public string? Nom { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 35;
}

public sealed class Alarme
{
    /// <summary>L'entité d'alarme du cœur (<c>sensor.maison_alarme</c>), commandée par <c>arm</c> / <c>disarm</c> avec code.</summary>
    public string? Entite { get; set; }
}

public sealed class Affichage
{
    public string? Exterieure { get; set; }
    public string CouleurFond { get; set; } = "#1C1C1E";
    public double ContrasteMinimum { get; set; } = 3;
    public int LuminositeDefaut { get; set; } = 200;
    public bool EteindreEnVeille { get; set; } = true;
}
