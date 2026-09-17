using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MtgaPbp.Cli;

/// <summary>
/// Everything about reaching the report from another device that is a decision rather
/// than a socket: what the key is, whether one arrived, and which addresses are this
/// machine's own.
/// </summary>
/// <remarks>
/// Split out of <see cref="LiveServer"/> because a socket is a poor place to ask a
/// question. The rules that decide whether a stranger is admitted are pure functions of
/// their arguments, so they are asked directly in tests rather than through a request.
/// </remarks>
public static class LanAccess
{
    /// <summary>The cookie the key is remembered in once a navigation has carried it.</summary>
    public const string CookieName = "pbp_key";

    /// <summary>The shortest key <c>--lan</c> will start with.</summary>
    public const int MinKeyLength = 16;

    /// <summary>Why <c>--lan</c> cannot start, or null when it can.</summary>
    /// <remarks>
    /// The message names the file to fix and hands over a key, because the alternative —
    /// starting anyway — is the one outcome worth refusing outright: an archive of every
    /// match the user has played, naming the people they played, readable by anything on
    /// the network.
    /// <para>
    /// A short key is refused rather than padded or stretched. Padding would turn "abc"
    /// into something that looks like a secret while staying exactly as guessable as "abc".
    /// </para>
    /// <para>
    /// The key is printed into a URL and set as a cookie, so it is held to characters that
    /// survive both: letters, digits, hyphens and underscores. Anything else — <c>&amp;</c>,
    /// <c>=</c>, a space, a control character — can truncate the cookie, break the link, or
    /// worse, so it is refused and a fresh generated key is suggested instead.
    /// </para>
    /// </remarks>
    public static string? Refusal(string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            return $"--lan needs a key: add \"LanKey\": \"{NewKey()}\" to {Config.UserFile} " +
                   "beside the exe and run it again. Without one, anything on this network " +
                   "could read your match history.";
        if (configuredKey.Length < MinKeyLength)
            return $"--lan needs a \"LanKey\" of at least {MinKeyLength} characters, and this " +
                   $"one is {configuredKey.Length}. Try \"LanKey\": \"{NewKey()}\".";
        if (configuredKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return "--lan needs a \"LanKey\" made only of letters, digits, hyphens and underscores, " +
                   "because it is printed into a URL and a cookie, and anything else can break " +
                   $"both. Try \"LanKey\": \"{NewKey()}\".";
        return null;
    }

    /// <summary>A fresh key of the shape <see cref="Refusal"/> suggests.</summary>
    public static string NewKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>True when what arrived is the key that was configured.</summary>
    /// <remarks>
    /// Compared as digests through <see cref="CryptographicOperations.FixedTimeEquals"/>,
    /// which answers in the same time whatever the two strings share. A comparison that
    /// stops at the first character that differs tells an attacker how much of a guess was
    /// right, and a key is exactly the thing worth walking one character at a time.
    /// </remarks>
    public static bool KeyMatches(string? presented, string configured) =>
        presented is not null && configured.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Sha(presented), Sha(configured));

    private static byte[] Sha(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    /// <summary>The key out of a request target's query, or null when it carries none.</summary>
    public static string? KeyFromQuery(string target)
    {
        var start = target.IndexOf('?');
        if (start < 0) return null;
        foreach (var pair in target[(start + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "key") return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>The key out of a Cookie header, or null when it is not there.</summary>
    public static string? KeyFromCookie(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var pair in header.Split(';'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var name = pair[..eq].Trim();
            if (name.Equals(CookieName, StringComparison.Ordinal))
                return pair[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>True when a peer is on this machine's own loopback.</summary>
    public static bool IsLoopbackPeer(IPAddress? peer) =>
        peer is not null && IPAddress.IsLoopback(peer);

    /// <summary>Every unicast address this machine currently holds.</summary>
    public static IPAddress[] OwnAddresses() =>
        [.. OwnAddressesWithRoute().Select(a => a.Address)];

    /// <summary>True when an address is usable for printing to another device.</summary>
    public static bool IsUsableLanAddress(IPAddress addr)
    {
        if (addr == null) return false;
        if (IPAddress.IsLoopback(addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // Exclude 127.0.0.0/8, link-local 169.254.0.0/16, and other non-routeable
        if (bytes[0] == 127) return false;
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        return true;
    }

    /// <summary>From a list of candidates, prefer a private-range address.</summary>
    public static IPAddress? PreferredAddress(IReadOnlyList<IPAddress> candidates)
    {
        if (candidates.Count == 0) return null;
        foreach (var addr in candidates)
        {
            if (addr == null) continue;
            var bytes = addr.GetAddressBytes();
            if (bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 192 && bytes[1] == 168))
                return addr;
        }
        return candidates.FirstOrDefault();
    }

    /// <summary>This machine's addresses, each marked with whether the interface carrying it holds the default route.</summary>
    private record struct AddressWithRoute(IPAddress Address, bool Routed);

    private static List<AddressWithRoute> OwnAddressesWithRoute()
    {
        var addressesByNic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(nic => (Nic: nic, Addresses: nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address).ToList()))
            .Where(entry => entry.Addresses.Count > 0)
            .ToList();

        var routedIndex = addressesByNic.FirstOrDefault(entry => entry.Nic.GetIPProperties()
            .GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)).Nic?.Id;

        return addressesByNic
            .SelectMany(entry => entry.Addresses,
                (entry, address) => new AddressWithRoute(address, routedIndex != null && entry.Nic.Id == routedIndex))
            .ToList();
    }

    /// <summary>The address to print for another device, or null when this machine has none.</summary>
    /// <remarks>
    /// The interface carrying the default route is what "this machine's address" means to
    /// someone else on the network. Asking for it that way rather than taking the first
    /// private address is what keeps a VPN, a Hyper-V switch or a WSL adapter — all of
    /// which hold addresses no tablet can reach — from being the one printed.
    /// </remarks>
    public static IPAddress? LanAddress()
    {
        var usable = OwnAddressesWithRoute()
            .Where(a => IsUsableLanAddress(a.Address))
            .ToList();
        if (usable.Count == 0) return null;
        var routed = usable.Where(a => a.Routed).Select(a => a.Address).ToList();
        return PreferredAddress(routed.Count > 0 ? routed : [.. usable.Select(a => a.Address)]);
    }

    /// <summary>True when a Host header names this server and no other.</summary>
    /// <remarks>
    /// Loopback names, and this machine's own addresses as literals — nothing else. That
    /// is the rebinding check: to reach this server an attacker's page has to state its own
    /// hostname here, and a name that will not parse as an address is refused (#116). A
    /// stated port, when there is one, must be this server's.
    /// </remarks>
    public static bool IsOwnHost(string? host, int port, IReadOnlyList<IPAddress> own)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var (name, statedPort) = SplitHost(host.Trim());
        if (statedPort is not null && statedPort != port.ToString()) return false;
        return NamesThisMachine(name, own);
    }

    /// <summary>True when an Origin header is one of this server's own pages.</summary>
    /// <remarks>
    /// Same-origin includes the port: a page served by some other local application is
    /// another site, however local it is. An opaque <c>Origin: null</c> (sandboxed frames,
    /// some redirects) fails on purpose — it proves nothing about who is asking.
    /// </remarks>
    public static bool IsOwnOrigin(string origin, int port, IReadOnlyList<IPAddress> own)
    {
        if (!origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = origin["http://".Length..].TrimEnd('/');
        var (name, statedPort) = SplitHost(rest);
        if ((statedPort ?? "80") != port.ToString()) return false;
        return NamesThisMachine(name, own);
    }

    private static bool NamesThisMachine(string name, IReadOnlyList<IPAddress> own) =>
        name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(name, out var address) &&
         (IPAddress.IsLoopback(address) || own.Contains(address)));

    /// <summary>Splits a Host header into the name it states and the port it states.</summary>
    /// <remarks>
    /// Brackets matter: <c>[::1]:8787</c> states a name and a port, <c>[::1]</c> states only
    /// a name, and the colons inside an IPv6 literal are not a port. Until this existed the
    /// check split on the last colon and compared "[" as a name, so a bracketed loopback
    /// host answered 404.
    /// </remarks>
    private static (string Name, string? Port) SplitHost(string host)
    {
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0) return (host, null);
            var rest = host[(end + 1)..];
            return (host[1..end].Trim(), rest.StartsWith(':') ? rest[1..].Trim() : null);
        }
        var colon = host.LastIndexOf(':');
        return colon < 0 ? (host, null) : (host[..colon].Trim(), host[(colon + 1)..].Trim());
    }
}
