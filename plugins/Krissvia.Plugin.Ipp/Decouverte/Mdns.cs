using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Krissvia.Plugin.Ipp.Decouverte;

/// <summary>Un enregistrement DNS utile à la découverte : PTR, SRV, TXT, A.</summary>
public sealed record DnsRecord(string Name, ushort Type, string? Target = null, ushort Port = 0,
    IReadOnlyDictionary<string, string>? Txt = null, IPAddress? Address = null)
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;
}

/// <summary>Une imprimante annoncée sur le réseau (DNS-SD, RFC 6763).</summary>
public sealed record ServiceIpp(string Instance, Uri Url, string? Uuid, string? Modele);

/// <summary>Le format des messages DNS (RFC 1035), réduit à la question PTR et à la lecture des réponses.</summary>
public static class DnsMessage
{
    public const string ServiceIpp = "_ipp._tcp.local";
    public const string ServiceIpps = "_ipps._tcp.local";

    /// <summary>Une question PTR par service, classe IN.</summary>
    public static byte[] Question(ushort id, params string[] services)
    {
        using var f = new MemoryStream();
        var u16 = new byte[2];
        void U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(u16, v); f.Write(u16); }

        U16(id);
        U16(0);                       // requête standard
        U16((ushort)services.Length);
        U16(0); U16(0); U16(0);
        foreach (var s in services)
        {
            foreach (var label in s.Split('.'))
            {
                var o = Encoding.UTF8.GetBytes(label);
                f.WriteByte((byte)o.Length);
                f.Write(o);
            }

            f.WriteByte(0);
            U16(DnsRecord.TypePtr);
            U16(1);
        }

        return f.ToArray();
    }

    /// <summary>
    /// Toutes les réponses d'un message (réponses, autorité, additionnelles : les répondeurs mDNS y mettent le SRV, le
    /// TXT et l'adresse). Une trame illisible rend une liste vide : un appareil bavard du réseau ne doit pas arrêter
    /// la découverte des autres.
    /// </summary>
    public static IReadOnlyList<DnsRecord> Lire(ReadOnlySpan<byte> m)
    {
        var enregistrements = new List<DnsRecord>();
        try
        {
            // Seules les réponses comptent : sur le groupe passent aussi les questions des autres machines.
            if (m.Length < 12 || (m[2] & 0x80) == 0) return [];
            var questions = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
            var total = BinaryPrimitives.ReadUInt16BigEndian(m[6..]) + BinaryPrimitives.ReadUInt16BigEndian(m[8..]) + BinaryPrimitives.ReadUInt16BigEndian(m[10..]);
            var p = 12;
            for (var i = 0; i < questions; i++)
            {
                Nom(m, ref p);
                p += 4;
            }

            for (var i = 0; i < total; i++)
            {
                var nom = Nom(m, ref p);
                var type = BinaryPrimitives.ReadUInt16BigEndian(m[p..]);
                var longueur = BinaryPrimitives.ReadUInt16BigEndian(m[(p + 8)..]);
                p += 10;
                if (p + longueur > m.Length) break;
                var donnees = p;
                p += longueur;
                switch (type)
                {
                    case DnsRecord.TypePtr:
                        enregistrements.Add(new DnsRecord(nom, type, Target: Nom(m, ref donnees)));
                        break;
                    case DnsRecord.TypeSrv when longueur >= 7:
                        var port = BinaryPrimitives.ReadUInt16BigEndian(m[(donnees + 4)..]);
                        donnees += 6;
                        enregistrements.Add(new DnsRecord(nom, type, Target: Nom(m, ref donnees), Port: port));
                        break;
                    case DnsRecord.TypeTxt:
                        enregistrements.Add(new DnsRecord(nom, type, Txt: Txt(m.Slice(donnees, longueur))));
                        break;
                    case DnsRecord.TypeA when longueur == 4:
                        enregistrements.Add(new DnsRecord(nom, type, Address: new IPAddress(m.Slice(donnees, 4))));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            // Trame tronquée : on garde ce qui a été lu avant.
        }

        return enregistrements;
    }

    /// <summary>Un nom, pointeurs de compression compris (RFC 1035 §4.1.4), avec garde contre les boucles.</summary>
    private static string Nom(ReadOnlySpan<byte> m, ref int p)
    {
        var labels = new List<string>();
        var position = p;
        var saute = false;
        for (var sauts = 0; ; )
        {
            var n = m[position];
            if (n == 0)
            {
                position++;
                break;
            }

            if ((n & 0xC0) == 0xC0)
            {
                if (++sauts > 16) throw new FormatException("boucle de compression");
                var cible = ((n & 0x3F) << 8) | m[position + 1];
                if (!saute) p = position + 2;
                saute = true;
                position = cible;
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(m.Slice(position + 1, n)));
            position += n + 1;
        }

        if (!saute) p = position;
        return string.Join('.', labels);
    }

    private static Dictionary<string, string> Txt(ReadOnlySpan<byte> d)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < d.Length; )
        {
            var n = d[i];
            var entree = Encoding.UTF8.GetString(d.Slice(i + 1, Math.Min(n, d.Length - i - 1)));
            i += n + 1;
            var egal = entree.IndexOf('=');
            if (egal > 0) txt.TryAdd(entree[..egal], entree[(egal + 1)..]);
            else if (entree.Length > 0) txt.TryAdd(entree, "");
        }

        return txt;
    }

    /// <summary>
    /// Assemble les imprimantes : PTR (le service) → SRV (hôte, port) + TXT (<c>rp</c> = chemin, <c>UUID</c>,
    /// <c>ty</c> = modèle) → A (l'adresse). L'adresse IP est préférée au nom <c>.local</c>, que Linux ne résout pas
    /// toujours (nss-mdns absent).
    /// </summary>
    public static IReadOnlyList<ServiceIpp> Assembler(IEnumerable<DnsRecord> tous)
    {
        var liste = tous.ToList();
        var resultat = new List<ServiceIpp>();
        foreach (var ptr in liste.Where(r => r.Type == DnsRecord.TypePtr && r.Target is not null
                     && (Egal(r.Name, ServiceIpp) || Egal(r.Name, ServiceIpps))).DistinctBy(r => r.Target!.ToLowerInvariant()))
        {
            var srv = liste.FirstOrDefault(r => r.Type == DnsRecord.TypeSrv && Egal(r.Name, ptr.Target!));
            if (srv?.Target is not { } hote || srv.Port == 0) continue;
            var txt = liste.FirstOrDefault(r => r.Type == DnsRecord.TypeTxt && Egal(r.Name, ptr.Target!))?.Txt;
            var ip = liste.FirstOrDefault(r => r.Type == DnsRecord.TypeA && Egal(r.Name, hote))?.Address;
            var chemin = txt?.GetValueOrDefault("rp") is { Length: > 0 } rp ? rp.TrimStart('/') : "ipp/print";
            var schema = Egal(ptr.Name, ServiceIpps) ? "ipps" : "ipp";
            var url = new UriBuilder(schema, ip?.ToString() ?? hote.TrimEnd('.'), srv.Port, "/" + chemin).Uri;
            var instance = ptr.Target!.EndsWith("." + ptr.Name, StringComparison.OrdinalIgnoreCase) ? ptr.Target![..^(ptr.Name.Length + 1)] : ptr.Target!;
            resultat.Add(new ServiceIpp(instance, url, txt?.GetValueOrDefault("UUID")?.ToLowerInvariant(), txt?.GetValueOrDefault("ty")));
        }

        // _ipp d'abord : la lecture en HTTP simple évite un certificat auto-signé quand l'imprimante offre les deux.
        return [.. resultat.OrderBy(s => s.Url.Scheme == "ipps")];
    }

    private static bool Egal(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cherche les imprimantes du réseau local.</summary>
public interface IDecouverteIpp
{
    Task<IReadOnlyList<ServiceIpp>> ChercherAsync(TimeSpan duree, CancellationToken ct);
}

/// <summary>
/// La question mDNS part de deux façons, en même temps, et les réponses des deux sont réunies :
/// <list type="bullet">
/// <item><b>mDNS ordinaire</b> : depuis le port 5353, partagé avec le répondeur du système (Avahi, Windows) ; les
/// imprimantes répondent au groupe 224.0.0.251, qu'on écoute sur chaque interface IPv4. C'est la seule qui ait obtenu
/// une réponse de la Samsung M2020 de l'utilisateur, le 24/10/2026.</item>
/// <item><b>Monodiffusion à l'ancienne</b> (RFC 6762 §6.7), depuis un port quelconque : les répondeurs devraient
/// répondre directement à ce port. Sur le poste Windows de l'essai, personne n'a répondu ainsi (pare-feu ou
/// répondeurs) ; gardée en secours, pour une machine où le 5353 ne se partage pas.</item>
/// </list>
/// </summary>
public sealed class DecouverteMdns : IDecouverteIpp
{
    private static readonly IPAddress Groupe = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint Destination = new(Groupe, 5353);

    public async Task<IReadOnlyList<ServiceIpp>> ChercherAsync(TimeSpan duree, CancellationToken ct)
    {
        var adresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.SupportsMulticast)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        if (adresses.Count == 0) adresses.Add(IPAddress.Any);

        var lus = await Task.WhenAll([MulticastAsync(adresses, duree, ct), .. adresses.Select(a => MonodiffusionAsync(a, duree, ct))]);
        return DnsMessage.Assembler(lus.SelectMany(l => l));
    }

    private static async Task<List<DnsRecord>> MulticastAsync(IReadOnlyList<IPAddress> adresses, TimeSpan duree, CancellationToken ct)
    {
        var recus = new List<DnsRecord>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var a in adresses)
            {
                try
                {
                    udp.JoinMulticastGroup(Groupe, a);
                }
                catch (SocketException)
                {
                    // Interface sans multidiffusion (VPN, Hyper-V…) : les autres suffisent.
                }
            }

            // Identifiant 0 : celui d'une question mDNS ordinaire (RFC 6762 §18.1).
            var question = DnsMessage.Question(0, DnsMessage.ServiceIpp, DnsMessage.ServiceIpps);
            foreach (var a in adresses.Where(a => !a.Equals(IPAddress.Any)))
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                    await udp.SendAsync(question, Destination, ct);
                }
                catch (SocketException)
                {
                }
            }

            if (adresses is [var seule] && seule.Equals(IPAddress.Any)) await udp.SendAsync(question, Destination, ct);
            await EcouterAsync(udp, recus, duree, ct);
        }
        catch (SocketException)
        {
            // 5353 réservé en exclusivité par un autre programme : la monodiffusion reste.
        }

        return recus;
    }

    private static async Task<List<DnsRecord>> MonodiffusionAsync(IPAddress locale, TimeSpan duree, CancellationToken ct)
    {
        var recus = new List<DnsRecord>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(locale, 0));
            if (!locale.Equals(IPAddress.Any))
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, locale.GetAddressBytes());
            var question = DnsMessage.Question((ushort)Random.Shared.Next(1, ushort.MaxValue), DnsMessage.ServiceIpp, DnsMessage.ServiceIpps);
            await udp.SendAsync(question, Destination, ct);
            await EcouterAsync(udp, recus, duree, ct);
        }
        catch (SocketException)
        {
            // Interface sans multidiffusion (VPN, Hyper-V…) : les autres répondent.
        }

        return recus;
    }

    private static async Task EcouterAsync(UdpClient udp, List<DnsRecord> recus, TimeSpan duree, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(duree);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(limite.Token);
                recus.AddRange(DnsMessage.Lire(r.Buffer));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fin de la fenêtre d'écoute.
        }
    }
}
