using System.Buffers.Binary;
using System.Text;

namespace Krissvia.Plugin.Ipp.Protocole;

/// <summary>Les étiquettes d'IPP (RFC 8010 §3.5) : délimiteurs de groupes (0x00-0x0F) et types de valeurs.</summary>
public static class IppTag
{
    public const byte OperationAttributes = 0x01;
    public const byte JobAttributes = 0x02;
    public const byte EndOfAttributes = 0x03;
    public const byte PrinterAttributes = 0x04;
    public const byte UnsupportedAttributes = 0x05;

    public const byte Unsupported = 0x10;
    public const byte Unknown = 0x12;
    public const byte NoValue = 0x13;
    public const byte Integer = 0x21;
    public const byte Boolean = 0x22;
    public const byte Enum = 0x23;
    public const byte OctetString = 0x30;
    public const byte DateTime = 0x31;
    public const byte Resolution = 0x32;
    public const byte RangeOfInteger = 0x33;
    public const byte BegCollection = 0x34;
    public const byte TextWithLanguage = 0x35;
    public const byte NameWithLanguage = 0x36;
    public const byte EndCollection = 0x37;
    public const byte TextWithoutLanguage = 0x41;
    public const byte NameWithoutLanguage = 0x42;
    public const byte Keyword = 0x44;
    public const byte Uri = 0x45;
    public const byte UriScheme = 0x46;
    public const byte Charset = 0x47;
    public const byte NaturalLanguage = 0x48;
    public const byte MimeMediaType = 0x49;
    public const byte MemberAttrName = 0x4A;
    public const byte Extension = 0x7F;
}

public sealed record IppResolution(int CrossFeed, int Feed, byte Units);

public sealed record IppRange(int Lower, int Upper);

/// <summary>Une valeur de type <c>collection</c> : ses membres, chacun avec ses valeurs.</summary>
public sealed class IppCollection : Dictionary<string, List<object?>>
{
    public IppCollection() : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>
/// Un attribut et ses valeurs, déjà converties : <c>int</c> (integer, enum), <c>bool</c>, <c>string</c> (textes, mots-clés,
/// URI…), <see cref="DateTimeOffset"/>, <see cref="IppResolution"/>, <see cref="IppRange"/>, <see cref="IppCollection"/>,
/// <c>byte[]</c> (octetString, extensions), <c>null</c> (unsupported, unknown, no-value).
/// </summary>
public sealed record IppAttribute(string Name, byte Tag, IReadOnlyList<object?> Values);

public sealed record IppGroup(byte Tag, IReadOnlyList<IppAttribute> Attributes);

/// <summary>Une réponse IPP décodée.</summary>
public sealed record IppResponse(byte Major, byte Minor, ushort StatusCode, int RequestId, IReadOnlyList<IppGroup> Groups)
{
    /// <summary>0x0000 à 0x00FF : « successful-ok » et ses variantes (attributs ignorés ou substitués).</summary>
    public bool Success => StatusCode < 0x0100;

    /// <summary>Le premier attribut de ce nom dans les groupes « printer-attributes ».</summary>
    public IppAttribute? Printer(string name) => Groups
        .Where(g => g.Tag == IppTag.PrinterAttributes)
        .SelectMany(g => g.Attributes)
        .FirstOrDefault(a => a.Name == name);
}

/// <summary>Une trame IPP illisible : tronquée, longueur incohérente, valeur sans attribut.</summary>
public sealed class IppFormatException(string message) : Exception(message);

/// <summary>
/// Le protocole binaire d'IPP (RFC 8010), réduit à ce dont le plugin a besoin : ÉCRIRE une requête
/// <c>Get-Printer-Attributes</c> — la seule opération qu'il envoie, une lecture — et LIRE n'importe quelle réponse.
/// Une centaine de lignes sans dépendance, contre une bibliothèque qui sait aussi imprimer : c'est précisément ce que
/// le plugin ne doit jamais faire.
/// </summary>
public static class IppCodec
{
    public const ushort GetPrinterAttributes = 0x000B;

    /// <summary>
    /// La requête <c>Get-Printer-Attributes</c>, en IPP/1.1 : la version que toute imprimante IPP comprend (IPP
    /// Everywhere en 2.0 l'accepte aussi). Les attributs d'opération dans l'ordre imposé par la RFC 8011 §4.1.4 :
    /// jeu de caractères, langue, puis l'imprimante visée.
    /// </summary>
    public static byte[] EncodeGetPrinterAttributes(string printerUri, int requestId, IReadOnlyList<string> requested, string user = "krissvia")
    {
        using var flux = new MemoryStream();
        Span<byte> entete = stackalloc byte[8];
        entete[0] = 1;
        entete[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(entete[2..], GetPrinterAttributes);
        BinaryPrimitives.WriteInt32BigEndian(entete[4..], requestId);
        flux.Write(entete);

        flux.WriteByte(IppTag.OperationAttributes);
        Ecrire(flux, IppTag.Charset, "attributes-charset", "utf-8");
        Ecrire(flux, IppTag.NaturalLanguage, "attributes-natural-language", "en");
        Ecrire(flux, IppTag.Uri, "printer-uri", printerUri);
        Ecrire(flux, IppTag.NameWithoutLanguage, "requesting-user-name", user);
        for (var i = 0; i < requested.Count; i++)
            Ecrire(flux, IppTag.Keyword, i == 0 ? "requested-attributes" : "", requested[i]);   // nom vide : valeur suivante
        flux.WriteByte(IppTag.EndOfAttributes);
        return flux.ToArray();
    }

    private static void Ecrire(Stream flux, byte tag, string nom, string valeur)
    {
        var n = Encoding.ASCII.GetBytes(nom);
        var v = Encoding.UTF8.GetBytes(valeur);
        Span<byte> longueur = stackalloc byte[2];
        flux.WriteByte(tag);
        BinaryPrimitives.WriteUInt16BigEndian(longueur, (ushort)n.Length);
        flux.Write(longueur);
        flux.Write(n);
        BinaryPrimitives.WriteUInt16BigEndian(longueur, (ushort)v.Length);
        flux.Write(longueur);
        flux.Write(v);
    }

    /// <summary>Lit une réponse. Toute incohérence lève <see cref="IppFormatException"/> : jamais une lecture hors de la trame.</summary>
    public static IppResponse Decode(ReadOnlySpan<byte> trame)
    {
        var l = new Lecteur(trame);
        var major = l.Octet();
        var minor = l.Octet();
        var statut = l.U16();
        var requete = l.I32();

        var groupes = new List<IppGroup>();
        List<IppAttribute>? attributs = null;
        byte groupe = 0;
        string? nomCourant = null;
        byte tagCourant = 0;
        List<object?>? valeurs = null;

        void Clore()
        {
            if (nomCourant is not null) attributs!.Add(new IppAttribute(nomCourant, tagCourant, valeurs!));
            nomCourant = null;
        }

        while (true)
        {
            if (l.Fin) throw new IppFormatException("trame tronquée : pas de fin d'attributs (0x03)");
            var tag = l.Octet();
            if (tag <= 0x0F)
            {
                Clore();
                if (attributs is not null) groupes.Add(new IppGroup(groupe, attributs));
                if (tag == IppTag.EndOfAttributes) break;
                groupe = tag;
                attributs = [];
                continue;
            }

            if (attributs is null) throw new IppFormatException($"valeur (0x{tag:X2}) avant tout groupe d'attributs");
            var nom = l.Texte(l.U16());
            if (nom.Length == 0)
            {
                // Valeur supplémentaire de l'attribut précédent (RFC 8010 §3.1.5).
                if (nomCourant is null) throw new IppFormatException("valeur supplémentaire sans attribut");
                valeurs!.Add(Valeur(ref l, tag));
                continue;
            }

            Clore();
            nomCourant = nom;
            tagCourant = tag;
            valeurs = [Valeur(ref l, tag)];
        }

        return new IppResponse(major, minor, statut, requete, groupes);
    }

    private static object? Valeur(ref Lecteur l, byte tag)
    {
        var octets = l.Octets(l.U16());
        if (tag == IppTag.BegCollection) return Collection(ref l);
        return Convertir(tag, octets);
    }

    /// <summary>
    /// Une collection (RFC 8010 §3.1.6) : des paires « memberAttrName » (le nom du membre, en valeur) puis sa ou ses
    /// valeurs, toutes à nom vide, jusqu'à « endCollection ». Une collection peut en contenir d'autres.
    /// </summary>
    private static IppCollection Collection(ref Lecteur l)
    {
        var collection = new IppCollection();
        List<object?>? membre = null;
        while (true)
        {
            if (l.Fin) throw new IppFormatException("collection tronquée : pas de endCollection (0x37)");
            var tag = l.Octet();
            if (l.U16() is not 0 and var n) l.Octets(n);   // les membres ont un nom vide ; un nom présent est ignoré
            if (tag == IppTag.EndCollection)
            {
                l.Octets(l.U16());
                return collection;
            }

            if (tag == IppTag.MemberAttrName)
            {
                var nom = l.Texte(l.U16());
                collection[nom] = membre = [];
                continue;
            }

            if (membre is null) throw new IppFormatException($"valeur (0x{tag:X2}) dans une collection sans nom de membre");
            membre.Add(Valeur(ref l, tag));
        }
    }

    private static object? Convertir(byte tag, ReadOnlySpan<byte> v)
    {
        switch (tag)
        {
            case IppTag.Unsupported or IppTag.Unknown or IppTag.NoValue:
                return null;
            case IppTag.Integer or IppTag.Enum:
                Longueur(tag, v, 4);
                return BinaryPrimitives.ReadInt32BigEndian(v);
            case IppTag.Boolean:
                Longueur(tag, v, 1);
                return v[0] != 0;
            case IppTag.DateTime:
                Longueur(tag, v, 11);
                return Date(v);
            case IppTag.Resolution:
                Longueur(tag, v, 9);
                return new IppResolution(BinaryPrimitives.ReadInt32BigEndian(v), BinaryPrimitives.ReadInt32BigEndian(v[4..]), v[8]);
            case IppTag.RangeOfInteger:
                Longueur(tag, v, 8);
                return new IppRange(BinaryPrimitives.ReadInt32BigEndian(v), BinaryPrimitives.ReadInt32BigEndian(v[4..]));
            case IppTag.TextWithLanguage or IppTag.NameWithLanguage:
            {
                // Langue puis texte, chacun précédé de sa longueur : on ne garde que le texte.
                var l = new Lecteur(v);
                l.Octets(l.U16());
                return l.Texte(l.U16());
            }
            case >= 0x40 and <= 0x5F:
                return Encoding.UTF8.GetString(v);   // textes, noms, mots-clés, URI… : UTF-8, qui couvre l'US-ASCII
            default:
                return v.ToArray();                  // octetString, extensions : gardés bruts
        }
    }

    private static void Longueur(byte tag, ReadOnlySpan<byte> v, int attendue)
    {
        if (v.Length != attendue) throw new IppFormatException($"valeur 0x{tag:X2} de {v.Length} octet(s) au lieu de {attendue}");
    }

    /// <summary>RFC 2579 DateAndTime : année, mois, jour, heure, minute, seconde, dixième, puis le décalage UTC.</summary>
    private static DateTimeOffset Date(ReadOnlySpan<byte> v)
    {
        var decalage = new TimeSpan(v[9], v[10], 0) * (v[8] == '-' ? -1 : 1);
        try
        {
            return new DateTimeOffset(BinaryPrimitives.ReadUInt16BigEndian(v), v[2], v[3], v[4], v[5], Math.Min((int)v[6], 59), v[7] * 100, decalage);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new IppFormatException("date IPP invalide");
        }
    }

    /// <summary>Lecture bornée : tout dépassement est une <see cref="IppFormatException"/>, jamais une exception d'index.</summary>
    private ref struct Lecteur(ReadOnlySpan<byte> octets)
    {
        private readonly ReadOnlySpan<byte> _octets = octets;
        private int _position;

        public readonly bool Fin => _position >= _octets.Length;

        public byte Octet() => Octets(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Octets(2));

        public int I32() => BinaryPrimitives.ReadInt32BigEndian(Octets(4));

        public string Texte(int longueur) => Encoding.UTF8.GetString(Octets(longueur));

        public ReadOnlySpan<byte> Octets(int longueur)
        {
            if (longueur > _octets.Length - _position)
                throw new IppFormatException($"trame tronquée : {longueur} octet(s) attendu(s) à la position {_position}, {_octets.Length - _position} restant(s)");
            var s = _octets.Slice(_position, longueur);
            _position += longueur;
            return s;
        }
    }
}
