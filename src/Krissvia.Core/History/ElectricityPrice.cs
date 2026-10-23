using System.Globalization;
using Krissvia.Core.Configuration;

namespace Krissvia.Core.History;

/// <summary>
/// Le prix du kWh, pour le « coût du jour » de l'écran Électricité (document <c>prix</c> de la collection
/// <c>electricite</c>). Un seul prix, TTC : un tarif heures pleines / creuses ou Tempo viendra avec le besoin.
/// </summary>
public sealed class ElectricityPrice(IConfigurationStore store)
{
    public const string Collection = "electricite";
    private const string DocumentId = "prix";

    private double? _prix;
    private bool _lu;

    /// <summary>Le prix en euros par kWh, ou null s'il n'est pas renseigné (pas de coût affiché).</summary>
    public async Task<double?> GetAsync(CancellationToken ct = default)
    {
        if (_lu) return _prix;
        var document = (await store.ListAsync(Collection, ct)).FirstOrDefault(d => d.Id == DocumentId);
        _prix = document is not null && double.TryParse(document.Json, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : null;
        _lu = true;
        return _prix;
    }

    public async Task SetAsync(double? prix, CancellationToken ct = default)
    {
        if (prix is { } p && (double.IsNaN(p) || p < 0 || p > 10))
            throw new ArgumentOutOfRangeException(nameof(prix), prix, "Un prix du kWh entre 0 et 10 €.");
        var valeur = prix is > 0 ? Math.Round(prix.Value, 5) : (double?)null;
        if (valeur is null) await store.DeleteAsync(Collection, DocumentId, ct);
        else await store.SaveAsync(Collection, new ConfigurationDocument(DocumentId, valeur.Value.ToString(CultureInfo.InvariantCulture)), ct);
        _prix = valeur;
        _lu = true;
    }
}
