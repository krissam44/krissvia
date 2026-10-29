using System.Text;

namespace Krissvia.Plugin.Ipp.Snmp;

/// <summary>Les étiquettes BER (X.690) et SNMP dont le plugin a besoin.</summary>
public static class BerTag
{
    public const byte Integer = 0x02;
    public const byte OctetString = 0x04;
    public const byte Null = 0x05;
    public const byte Oid = 0x06;
    public const byte Sequence = 0x30;
    public const byte Counter32 = 0x41;
    public const byte Gauge32 = 0x42;
    public const byte TimeTicks = 0x43;
    public const byte Counter64 = 0x46;
    public const byte GetNextRequest = 0xA1;
    public const byte GetResponse = 0xA2;
    public const byte NoSuchObject = 0x80;
    public const byte NoSuchInstance = 0x81;
    public const byte EndOfMibView = 0x82;
}

/// <summary>Versions SNMP, telles qu'écrites dans le message : 0 = v1, 1 = v2c.</summary>
public enum SnmpVersion { V1 = 0, V2c = 1 }

/// <summary>
/// Une valeur : <c>long</c> (entiers, compteurs, jauges), <c>byte[]</c> (chaîne d'octets), <c>string</c> (OID), ou
/// <c>null</c> (NULL, noSuchObject, noSuchInstance, endOfMibView — <see cref="Tag"/> dit lequel).
/// </summary>
public sealed record SnmpVarbind(string Oid, byte Tag, object? Value)
{
    public bool FinDeVue => Tag is BerTag.NoSuchObject or BerTag.NoSuchInstance or BerTag.EndOfMibView;
}

public sealed record SnmpResponse(SnmpVersion Version, int RequestId, int ErrorStatus, IReadOnlyList<SnmpVarbind> Varbinds);

/// <summary>Une trame SNMP illisible.</summary>
public sealed class SnmpFormatException(string message) : Exception(message);

/// <summary>
/// La part de BER et de SNMP (RFC 1157, RFC 3416) dont le plugin a besoin : ÉCRIRE un <c>GetNextRequest</c> — la seule
/// requête qu'il envoie, une lecture — et LIRE une réponse. Même raison que pour IPP : une bibliothèque SNMP sait aussi
/// faire des <c>Set</c>, précisément ce que le plugin ne doit jamais envoyer.
/// </summary>
public static class SnmpCodec
{
    /// <summary>Un <c>GetNextRequest</c> à plusieurs variables : une ligne de chaque colonne d'une table en un aller-retour.</summary>
    public static byte[] EncodeGetNext(SnmpVersion version, string community, int requestId, IReadOnlyList<string> oids)
    {
        var varbinds = oids.Select(o => Tlv(BerTag.Sequence, [.. Tlv(BerTag.Oid, EncoderOid(o)), .. Tlv(BerTag.Null, [])]));
        var pdu = Tlv(BerTag.GetNextRequest,
        [
            .. Tlv(BerTag.Integer, EncoderEntier(requestId)),
            .. Tlv(BerTag.Integer, [0]),   // error-status
            .. Tlv(BerTag.Integer, [0]),   // error-index
            .. Tlv(BerTag.Sequence, [.. varbinds.SelectMany(v => v)]),
        ]);
        return Tlv(BerTag.Sequence, [.. Tlv(BerTag.Integer, [(byte)version]), .. Tlv(BerTag.OctetString, Encoding.UTF8.GetBytes(community)), .. pdu]);
    }

    public static SnmpResponse Decode(ReadOnlySpan<byte> trame)
    {
        var message = Lire(trame, 0, BerTag.Sequence, out _);
        var version = Lire(message, 0, BerTag.Integer, out var p);
        Lire(message, p, BerTag.OctetString, out p);
        var pdu = Lire(message, p, BerTag.GetResponse, out _);

        var requete = Lire(pdu, 0, BerTag.Integer, out p);
        var erreur = Lire(pdu, p, BerTag.Integer, out p);
        Lire(pdu, p, BerTag.Integer, out p);
        var liste = Lire(pdu, p, BerTag.Sequence, out _);

        var varbinds = new List<SnmpVarbind>();
        for (var q = 0; q < liste.Length;)
        {
            var vb = Lire(liste, q, BerTag.Sequence, out q);
            var oid = DecoderOid(Lire(vb, 0, BerTag.Oid, out var r));
            var tag = vb.Length > r ? vb[r] : throw new SnmpFormatException("variable sans valeur");
            var valeur = Lire(vb, r, tag, out _);
            varbinds.Add(new SnmpVarbind(oid, tag, tag switch
            {
                BerTag.Integer or BerTag.Counter32 or BerTag.Gauge32 or BerTag.TimeTicks or BerTag.Counter64 => DecoderEntier(valeur),
                BerTag.OctetString => valeur.ToArray(),
                BerTag.Oid => DecoderOid(valeur),
                _ => null,
            }));
        }

        return new SnmpResponse((SnmpVersion)DecoderEntier(version), (int)DecoderEntier(requete), (int)DecoderEntier(erreur), varbinds);
    }

    /// <summary>Lit un TLV de l'étiquette attendue à la position <paramref name="p"/> ; <paramref name="suite"/> = position d'après.</summary>
    private static ReadOnlySpan<byte> Lire(ReadOnlySpan<byte> d, int p, byte attendue, out int suite)
    {
        if (p + 2 > d.Length) throw new SnmpFormatException($"trame tronquée à la position {p}");
        if (d[p] != attendue) throw new SnmpFormatException($"étiquette 0x{d[p]:X2} au lieu de 0x{attendue:X2} à la position {p}");
        long longueur = d[p + 1];
        var debut = p + 2;
        if ((longueur & 0x80) != 0)
        {
            var n = (int)(longueur & 0x7F);
            if (n is 0 or > 4 || debut + n > d.Length) throw new SnmpFormatException($"longueur invalide à la position {p}");
            longueur = 0;
            for (var i = 0; i < n; i++) longueur = (longueur << 8) | d[debut + i];
            debut += n;
        }

        if (debut + longueur > d.Length) throw new SnmpFormatException($"trame tronquée : {longueur} octet(s) attendu(s) à la position {debut}");
        suite = debut + (int)longueur;
        return d.Slice(debut, (int)longueur);
    }

    private static byte[] Tlv(byte tag, byte[] valeur)
    {
        var n = valeur.Length;
        byte[] longueur = n < 0x80 ? [(byte)n]
            : n < 0x100 ? [0x81, (byte)n]
            : [0x82, (byte)(n >> 8), (byte)n];
        return [tag, .. longueur, .. valeur];
    }

    /// <summary>Entier signé en complément à deux, sur le moins d'octets possible.</summary>
    private static byte[] EncoderEntier(long v)
    {
        var octets = new List<byte>();
        while (true)
        {
            octets.Insert(0, (byte)v);
            v >>= 8;
            if ((v == 0 && (octets[0] & 0x80) == 0) || (v == -1 && (octets[0] & 0x80) != 0)) return [.. octets];
        }
    }

    /// <summary>Signé, sauf les compteurs et jauges qui débordent d'un octet de tête nul : la règle BER suffit dans les deux cas.</summary>
    private static long DecoderEntier(ReadOnlySpan<byte> v)
    {
        if (v.Length is 0 or > 9) throw new SnmpFormatException($"entier de {v.Length} octet(s)");
        long r = (v[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in v) r = (r << 8) | b;
        return r;
    }

    private static byte[] EncoderOid(string oid)
    {
        var p = oid.Split('.').Select(uint.Parse).ToArray();
        var octets = new List<byte> { (byte)(p[0] * 40 + p[1]) };
        foreach (var x in p.Skip(2))
        {
            var groupe = new List<byte> { (byte)(x & 0x7F) };
            for (var reste = x >> 7; reste > 0; reste >>= 7) groupe.Insert(0, (byte)(0x80 | (reste & 0x7F)));
            octets.AddRange(groupe);
        }

        return [.. octets];
    }

    private static string DecoderOid(ReadOnlySpan<byte> v)
    {
        if (v.Length == 0) throw new SnmpFormatException("OID vide");
        var parties = new List<ulong> { (ulong)(v[0] / 40), (ulong)(v[0] % 40) };
        ulong x = 0;
        foreach (var b in v[1..])
        {
            x = (x << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                parties.Add(x);
                x = 0;
            }
        }

        return string.Join('.', parties);
    }

    /// <summary>Vrai si <paramref name="oid"/> est sous <paramref name="racine"/> (pas seulement préfixé : 1.2.30 n'est pas sous 1.2.3).</summary>
    public static bool Sous(string oid, string racine) => oid.StartsWith(racine + ".", StringComparison.Ordinal);
}
