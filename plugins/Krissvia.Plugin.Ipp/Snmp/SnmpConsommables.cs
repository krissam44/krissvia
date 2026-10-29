using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Krissvia.Plugin.Ipp.Snmp;

/// <summary>Une ligne de <c>prtMarkerSuppliesTable</c> (Printer MIB, RFC 3805).</summary>
/// <param name="Type">prtMarkerSuppliesType : 3 toner, 9 tambour (opc), 15 four, 20 unité de transfert, 1 autre…</param>
/// <param name="Capacite">prtMarkerSuppliesMaxCapacity, dans l'unité de la ligne (pages, impressions…) ; ≤ 0 : inconnue.</param>
/// <param name="Niveau">prtMarkerSuppliesLevel, même échelle ; -1, -2, -3 comme en IPP.</param>
public sealed record LigneSnmp(string Index, string? Description, int? Type, long? Capacite, long? Niveau);

/// <summary>Lit la table des consommables d'une imprimante, ou rend null si elle ne répond pas en SNMP.</summary>
public interface ISnmpConsommables
{
    Task<IReadOnlyList<LigneSnmp>?> LireAsync(string hote, CancellationToken ct);
}

/// <summary>
/// La table <c>prtMarkerSupplies</c> par <c>GetNextRequest</c>, quatre colonnes à la fois : une ligne de la table par
/// aller-retour, cinq échanges pour une laser à quatre consommables. Vers l'adresse de l'imprimante seulement, port
/// 161, communauté réglable (« public »).
/// <para>
/// <b>v2c d'abord, v1 ensuite</b> : la Samsung M2020 de l'utilisateur ne répond qu'en v2c (essai du 24/10/2026), quand
/// l'intégration Gladys ne parle que v1. La version qui a répondu est retenue par imprimante.
/// </para>
/// </summary>
public sealed class SnmpConsommables(string communaute = "public", int port = 161, TimeSpan? delai = null) : ISnmpConsommables
{
    public const string Table = "1.3.6.1.2.1.43.11.1.1";
    public const string ColonneType = Table + ".5";
    public const string ColonneDescription = Table + ".6";
    public const string ColonneCapacite = Table + ".8";
    public const string ColonneNiveau = Table + ".9";
    public static readonly IReadOnlyList<string> Colonnes = [ColonneType, ColonneDescription, ColonneCapacite, ColonneNiveau];

    /// <summary>
    /// Une question restée sans réponse est reposée, jusqu'à ce nombre de fois. La Samsung M2020 de l'utilisateur perd
    /// les premières questions qui la tirent de sa veille, puis répond en 15 ms (mesuré le 24/10/2026) : sans
    /// répétition, une imprimante qui dort passait pour « sans SNMP » une fois sur deux.
    /// </summary>
    public const int Tentatives = 3;

    /// <summary>Une imprimante n'a pas cent consommables : au-delà, l'agent tourne en rond.</summary>
    public const int LignesMaximum = 32;

    private readonly TimeSpan _delai = delai ?? TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, SnmpVersion> _versions = new(StringComparer.Ordinal);
    private int _requete;

    public async Task<IReadOnlyList<LigneSnmp>?> LireAsync(string hote, CancellationToken ct)
    {
        var essais = _versions.TryGetValue(hote, out var connue) ? [connue] : new[] { SnmpVersion.V2c, SnmpVersion.V1 };
        foreach (var version in essais)
        {
            var lignes = await ParcourirAsync(hote, version, ct);
            if (lignes is null) continue;
            _versions[hote] = version;
            return lignes;
        }

        _versions.TryRemove(hote, out _);   // la prochaine fois, on réessaie les deux
        return null;
    }

    /// <summary>Le parcours de la table ; null si une question reste sans réponse malgré ses répétitions.</summary>
    private async Task<List<LigneSnmp>?> ParcourirAsync(string hote, SnmpVersion version, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var adresse = IPAddress.TryParse(hote, out var ip) ? ip : (await Dns.GetHostAddressesAsync(hote, AddressFamily.InterNetwork, ct)).First();
        udp.Connect(adresse, port);

        var lignes = new List<LigneSnmp>();
        var courants = Colonnes.ToArray();
        for (var n = 0; n < LignesMaximum; n++)
        {
            var reponse = await EchangerAsync(udp, version, courants, ct);
            // Muette, même en cours de route : une table à moitié lue n'est pas une mesure. La rendre ferait disparaître
            // les jauges des lignes manquantes, puis réapparaître au parcours suivant ; le plugin garde la dernière complète.
            if (reponse is null) return null;
            if (reponse.ErrorStatus != 0 || reponse.Varbinds.Count != Colonnes.Count) return lignes;   // v1 : noSuchName en fin de table

            var niveau = reponse.Varbinds[3];
            // Fin de la colonne des niveaux (on est passé dans la table suivante), ou un agent qui n'avance plus.
            if (niveau.FinDeVue || !SnmpCodec.Sous(niveau.Oid, ColonneNiveau) || niveau.Oid == courants[3]) return lignes;

            lignes.Add(Ligne(reponse));
            courants = [.. reponse.Varbinds.Select(v => v.Oid)];
        }

        return lignes;
    }

    /// <summary>
    /// La ligne d'une réponse à quatre colonnes, repérée par l'index du niveau. Une colonne plus courte que celle des
    /// niveaux (une description absente) a déjà sauté à la ligne suivante : sa valeur n'est pas de cette ligne, elle
    /// est laissée vide plutôt que prêtée au voisin.
    /// </summary>
    internal static LigneSnmp Ligne(SnmpResponse reponse)
    {
        var index = reponse.Varbinds[3].Oid[(ColonneNiveau.Length + 1)..];
        object? Champ(int i) => reponse.Varbinds[i].Oid == $"{Colonnes[i]}.{index}" ? reponse.Varbinds[i].Value : null;
        return new LigneSnmp(index, Champ(1) is byte[] d ? Texte(d) : null, Champ(0) is long t ? (int)t : null, Champ(2) as long?, reponse.Varbinds[3].Value as long?);
    }

    private async Task<SnmpResponse?> EchangerAsync(UdpClient udp, SnmpVersion version, string[] oids, CancellationToken ct)
    {
        // Le même identifiant à chaque répétition : la réponse tardive à une répétition précédente est la bonne.
        var id = Interlocked.Increment(ref _requete) & 0x7FFFFFFF;
        var question = SnmpCodec.EncodeGetNext(version, communaute, id, oids);
        for (var tentative = 0; tentative < Tentatives; tentative++)
        {
            await udp.SendAsync(question, ct);
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(_delai);
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(limite.Token);
                    try
                    {
                        var reponse = SnmpCodec.Decode(r.Buffer);
                        if (reponse.RequestId == id) return reponse;   // une réponse en retard à la question précédente est ignorée
                    }
                    catch (SnmpFormatException)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Pas de réponse dans le délai : on repose la question.
            }
            catch (SocketException)
            {
                return null;   // port fermé (ICMP « injoignable ») : pas de SNMP sur cette imprimante
            }
        }

        return null;
    }

    /// <summary>Texte d'une description : UTF-8, sans les NUL de remplissage de certains micrologiciels.</summary>
    public static string Texte(byte[] octets) => Encoding.UTF8.GetString(octets).TrimEnd('\0').Trim();
}
