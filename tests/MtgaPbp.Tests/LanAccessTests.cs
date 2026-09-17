using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The rules that decide whether another device may read the live report, asked directly
/// instead of through a request.
/// </summary>
/// <remarks>
/// Every case here is one a socket cannot be talked into producing: a peer that is not this
/// machine, a Host naming a hostname somebody else controls, a key that is wrong in its last
/// character. They live in <see cref="LanAccess"/> precisely so the questions can be put to
/// them without a listener, and so that the answers do not depend on the network the suite
/// happens to run on.
/// </remarks>
public class LanAccessTests
{
    private static readonly IPAddress[] Own = [IPAddress.Parse("192.168.1.50")];

    [TestCase(NetworkInterfaceType.Tunnel, true)]
    [TestCase(NetworkInterfaceType.Ppp, true)]
    [TestCase(NetworkInterfaceType.Ethernet, false)]
    [TestCase(NetworkInterfaceType.Wireless80211, false)]
    public void Tunnel_and_ppp_types_are_excluded_from_lan_advertisement(
        NetworkInterfaceType type, bool excluded) =>
        Assert.That(LanAccess.IsTunneled(type), Is.EqualTo(excluded));

    [Test]
    public void A_key_is_suggested_when_lan_is_asked_for_without_one()
    {
        var refusal = LanAccess.Refusal(null);

        Assert.That(refusal, Does.Contain(Config.UserFile), "it names the file to fix");
        Assert.That(refusal, Does.Contain("\"LanKey\""));
        Assert.That(Regex.Match(refusal ?? "", "[0-9a-f]{32}").Success,
                    "and hands over a key to paste");
    }

    [Test]
    public void A_key_shorter_than_the_minimum_is_refused() =>
        Assert.That(LanAccess.Refusal(new string('a', LanAccess.MinKeyLength - 1)), Is.Not.Null);

    [Test]
    public void A_key_that_is_long_enough_starts() =>
        Assert.That(LanAccess.Refusal(new string('a', LanAccess.MinKeyLength)), Is.Null);

    [Test]
    public void A_key_longer_than_the_maximum_is_refused() =>
        Assert.That(LanAccess.Refusal(new string('a', LanAccess.MaxKeyLength + 1)), Is.Not.Null,
            "an unbounded key is a memory question a config file should not get to ask");

    [Test]
    public void The_address_cache_expires_and_then_is_remembered_again()
    {
        var then = DateTimeOffset.UtcNow;
        var now = then.Add(LanAccess.AddressCacheLifetime);

        Assert.That(LanAccess.AddressCacheIsFresh([IPAddress.Loopback], then, now), Is.False,
            "at the lifetime's end the snapshot is stale");
        Assert.That(LanAccess.AddressCacheIsFresh([IPAddress.Loopback], now, now), Is.True,
            "a just-taken snapshot is fresh");
        Assert.That(LanAccess.AddressCacheIsFresh(null, now, now), Is.False,
            "there is nothing fresh about never having asked");
    }

    [Test]
    public void A_key_with_characters_that_cannot_ride_a_url_is_refused()
    {
        Assert.That(LanAccess.Refusal("key&with=separators!16"), Is.Not.Null,
            "the key is printed into a URL and set as a cookie, so it must survive both");
        Assert.That(LanAccess.Refusal("with-hyphen_and_under15"), Is.Null,
            "hyphens and underscores are fine");
    }

    [Test]
    public void An_empty_key_counts_as_no_key_at_all() =>
        Assert.That(LanAccess.Refusal("   "), Is.Not.Null);

    [Test]
    public void Generated_keys_are_long_and_never_repeat()
    {
        var first = LanAccess.NewKey();

        Assert.That(first, Has.Length.EqualTo(32));
        Assert.That(first, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(LanAccess.NewKey(), Is.Not.EqualTo(first));
    }

    [Test]
    public void Only_the_configured_key_matches()
    {
        var key = LanAccess.NewKey();

        Assert.That(LanAccess.KeyMatches(key, key), Is.True);
        Assert.That(LanAccess.KeyMatches(key.ToUpperInvariant(), key), Is.False,
                    "a key is not case-insensitive");
        Assert.That(LanAccess.KeyMatches(key + "x", key), Is.False);
        Assert.That(LanAccess.KeyMatches(key[..^1], key), Is.False);
        Assert.That(LanAccess.KeyMatches(null, key), Is.False);
        Assert.That(LanAccess.KeyMatches("", key), Is.False);
    }

    [Test]
    public void The_key_is_read_from_a_cookie_among_others()
    {
        Assert.That(LanAccess.KeyFromCookie($"a=1; {LanAccess.CookieName}=secret; b=2"),
                    Is.EqualTo("secret"));
        Assert.That(LanAccess.KeyFromCookie($"{LanAccess.CookieName}=secret"),
                    Is.EqualTo("secret"));
        Assert.That(LanAccess.KeyFromCookie("a=1"), Is.Null);
        Assert.That(LanAccess.KeyFromCookie(null), Is.Null);
    }

    [Test]
    public void The_key_is_read_from_a_query_among_other_parameters()
    {
        Assert.That(LanAccess.KeyFromQuery("/?key=secret"), Is.EqualTo("secret"));
        Assert.That(LanAccess.KeyFromQuery("/games/m1.html?key=secret&page=2"),
                    Is.EqualTo("secret"));
        Assert.That(LanAccess.KeyFromQuery("/api/favorite/m1?on=false&key=secret"),
                    Is.EqualTo("secret"));
        Assert.That(LanAccess.KeyFromQuery("/api/favorite/m1?on=false"), Is.Null);
        Assert.That(LanAccess.KeyFromQuery("/"), Is.Null);
    }

    [Test]
    public void A_hostname_is_never_this_server()
    {
        // DNS rebinding: a page on a hostname the attacker controls points that name at
        // this machine, and the Host header is the only place the trick shows.
        Assert.That(LanAccess.IsOwnHost("attacker.example", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnHost("attacker.example:8787", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnHost("localhost:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnHost("localhost:1", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnHost("127.0.0.1:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnHost("192.168.1.50:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnHost("192.168.1.51:8787", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnHost(null, 8787, Own), Is.False);
    }

    [Test]
    public void A_host_stating_no_port_names_no_other_server() =>
        Assert.That(LanAccess.IsOwnHost("localhost", 8787, Own), Is.True);

    [Test]
    public void An_ipv6_literal_states_its_port_outside_the_brackets()
    {
        Assert.That(LanAccess.IsOwnHost("[::1]:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnHost("[::1]", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnHost("[::1]:1", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnHost("[2001:db8::1]:8787", 8787, Own), Is.False);
    }

    [Test]
    public void An_origin_from_another_machine_is_not_this_one()
    {
        Assert.That(LanAccess.IsOwnOrigin("http://192.168.1.50:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnOrigin("http://localhost:8787", 8787, Own), Is.True);
        Assert.That(LanAccess.IsOwnOrigin("http://192.168.1.50:1", 8787, Own), Is.False,
                    "same-origin includes the port");
        Assert.That(LanAccess.IsOwnOrigin("https://192.168.1.50:8787", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnOrigin("http://attacker.example:8787", 8787, Own), Is.False);
        Assert.That(LanAccess.IsOwnOrigin("null", 8787, Own), Is.False);
    }

    [Test]
    public void The_printed_address_avoids_link_local_and_loopback()
    {
        Assert.That(LanAccess.IsUsableLanAddress(IPAddress.Parse("169.254.60.99")), Is.False,
                    "nobody can type a link-local address at a tablet and reach anything");
        Assert.That(LanAccess.IsUsableLanAddress(IPAddress.Loopback), Is.False);
        Assert.That(LanAccess.IsUsableLanAddress(IPAddress.IPv6Loopback), Is.False);
        Assert.That(LanAccess.IsUsableLanAddress(IPAddress.Parse("192.168.1.50")), Is.True);
    }

    [Test]
    public void The_printed_address_prefers_a_private_range()
    {
        IPAddress[] candidates = [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("10.0.0.5")];
        Assert.That(LanAccess.PreferredAddress(candidates),
                    Is.EqualTo(IPAddress.Parse("10.0.0.5")));

        Assert.That(LanAccess.PreferredAddress([IPAddress.Parse("8.8.8.8")]),
                    Is.EqualTo(IPAddress.Parse("8.8.8.8")),
                    "and takes what there is when there is no private one");
        Assert.That(LanAccess.PreferredAddress([]), Is.Null);
    }

    [Test]
    public void The_address_it_prints_is_an_address_it_would_accept()
    {
        // The two halves of the feature have to agree: whatever --lan tells the reader to
        // open, the Host check must take, or the page loads a 404 from this very server.
        if (LanAccess.LanAddress() is not { } found)
        {
            Assert.Ignore("this machine holds no address another device could reach");
            return;
        }
        var address = found;

        var own = LanAccess.OwnAddresses();
        Assert.That(own, Does.Contain(address));
        Assert.That(LanAccess.IsOwnHost($"{address}:8787", 8787, own), Is.True);
    }
}