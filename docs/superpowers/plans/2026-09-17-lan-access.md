# LAN access for `watch` — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in `mtga-pbp watch --lan` that another device on the same network can read —
and only with the key the user configured, so the archive is never exposed unauthenticated.

**Architecture:** `LiveServer` gains a `lan` mode: it binds `IPAddress.Any` instead of
`IPAddress.Loopback`, and a request from off this machine must present the configured key —
as `?key=` on a first navigation (answered with a `302` and a session cookie) or as the
`pbp_key` cookie afterwards, which is what lets `EventSource` and the page's own `fetch`
calls through. The Host and Origin allowlists grow by this machine's own literal addresses;
a hostname is still refused, so #116's rebinding defence is untouched. Every decision that
can be a pure function is one, in `LanAccess`, so it is tested without a socket.

**Tech Stack:** .NET 10, NUnit 4, `System.Net.Sockets` / `System.Net.NetworkInformation`.
No new packages, no new target framework, no change to the static report.

**Spec:** [`docs/superpowers/specs/2026-09-17-lan-access-design.md`](../specs/2026-09-17-lan-access-design.md).

## Global Constraints

- Every `dotnet` command runs from the repository root. `dotnet test` must stay green; run
  `dotnet format --verify-no-changes` before each commit.
- Source files are CRLF and UTF-8. Never commit the OS username, a real Arena screen name,
  a MAC address, a Wi-Fi SSID, or an absolute profile path — write output paths in
  `%USERPROFILE%` form, and grep the staged diff for `C:\Users` before every commit.
- The Cli project stays `net10.0`. Nothing here is Windows-only, so no
  `[SupportedOSPlatform]` guard is needed.
- Tests are NUnit, `Assert.That(...)` style, named `Like_a_sentence_with_underscores`, with a
  `<summary>` on the class saying why the tests exist.
- **Never run a manual check against the maintainer's live `watch`**: it is on port 8799 with
  the real archive beside it. Manual runs use a scratch config and a scratch port, and the
  scratch folder is deleted afterwards.
- Versioning by CONTRIBUTING's table: `--lan` is a new user-visible capability → **minor**
  (0.12.2 → 0.13.0). The bump belongs to the release commit, not to this change.

## File structure

- Create `src/MtgaPbp.Cli/LanAccess.cs` — every LAN decision that is not a socket: the key's
  shape and comparison, the cookie and query readers, this machine's addresses, and what
  counts as this server's own Host and Origin.
- Create `tests/MtgaPbp.Tests/LanAccessTests.cs`.
- Modify `src/MtgaPbp.Cli/Config.cs` — the `LanKey` setting, in the class, the layer, and
  the apply list.
- Modify `src/MtgaPbp.Cli/LiveServer.cs` — the bind, the allowlists, the admission check,
  the cookie and the redirect, and `LanUrl`.
- Modify `tests/MtgaPbp.Tests/LiveServerTests.cs` — the LAN cases, over real sockets.
- Modify `tests/MtgaPbp.Tests/ConfigTests.cs` — the new key through the layers.
- Modify `src/MtgaPbp.Cli/Program.cs` — `--lan`, the refusal, the `lan` line, the usage
  text, and the balloon's address.
- Modify `README.md`, `SECURITY.md`.
## Task 1 — `LanAccess`: the decisions, with no socket in sight

**Files:** create `src/MtgaPbp.Cli/LanAccess.cs`, create `tests/MtgaPbp.Tests/LanAccessTests.cs`.

**Interfaces:**

- `public const string LanAccess.CookieName = "pbp_key"`, `public const int LanAccess.MinKeyLength = 16`.
- `public static string? LanAccess.Refusal(string? configuredKey)` — the message to print
  instead of starting, or `null` when the key is usable.
- `public static string LanAccess.NewKey()` — 32 lowercase hex characters.
- `public static bool LanAccess.KeyMatches(string? presented, string configured)`.
- `public static string? LanAccess.KeyFromQuery(string target)`,
  `public static string? LanAccess.KeyFromCookie(string? cookieHeader)`.
- `public static IPAddress[] LanAccess.OwnAddresses()`,
  `public static IPAddress? LanAccess.LanAddress()`.
- `public static bool LanAccess.IsUsableLanAddress(IPAddress)`,
  `public static IPAddress? LanAccess.PreferredAddress(IReadOnlyList<IPAddress>)`.
- `public static bool LanAccess.IsOwnHost(string? host, int port, IReadOnlyList<IPAddress> own)`,
  `public static bool LanAccess.IsOwnOrigin(string origin, int port, IReadOnlyList<IPAddress> own)`.
- `public static bool LanAccess.IsLoopbackPeer(IPAddress? peer)`.

- [ ] **Step 1: Write the failing tests.** The class `<summary>` says why they exist: these
  are the rules that decide whether a stranger is admitted, so they are asked directly
  instead of through a request.

```csharp
[Test]
public void A_key_is_suggested_when_lan_was_asked_for_without_one()
{
    var refusal = LanAccess.Refusal(null);
    Assert.That(refusal, Does.Contain(Config.UserFile), "it names the file to fix");
    Assert.That(refusal, Does.Contain("\"LanKey\""));
    Assert.That(Regex.Match(refusal!, "[0-9a-f]{32}").Success, "and prints one to paste");
}

[Test]
public void A_key_shorter_than_the_minimum_is_refused() =>
    Assert.That(LanAccess.Refusal(new string('a', LanAccess.MinKeyLength - 1)), Is.Not.Null);

[Test]
public void A_key_that_is_long_enough_starts() =>
    Assert.That(LanAccess.Refusal(new string('a', LanAccess.MinKeyLength)), Is.Null);

[Test]
public void Only_the_configured_key_matches()
{
    var key = LanAccess.NewKey();
    Assert.That(LanAccess.KeyMatches(key, key), Is.True);
    Assert.That(LanAccess.KeyMatches(key + "x", key), Is.False);
    Assert.That(LanAccess.KeyMatches(null, key), Is.False);
    Assert.That(LanAccess.KeyMatches("", key), Is.False);
}

[Test]
public void A_hostname_is_never_this_server()
{
    IPAddress[] own = [IPAddress.Parse("192.168.1.50")];
    Assert.That(LanAccess.IsOwnHost("attacker.example", 8787, own), Is.False);
    Assert.That(LanAccess.IsOwnHost("localhost:8787", 8787, own), Is.True);
    Assert.That(LanAccess.IsOwnHost("localhost:1", 8787, own), Is.False);
    Assert.That(LanAccess.IsOwnHost("192.168.1.50:8787", 8787, own), Is.True);
    Assert.That(LanAccess.IsOwnHost("127.0.0.1:8787", 8787, own), Is.True);
    Assert.That(LanAccess.IsOwnHost("[::1]:8787", 8787, own), Is.True);
    Assert.That(LanAccess.IsOwnHost("[::1]:1", 8787, own), Is.False);
    Assert.That(LanAccess.IsOwnHost("192.168.1.51:8787", 8787, own), Is.False);
}
```

Cover the rest the same way, one test each: `NewKey` is 32 hex and never repeats; the cookie
reader finds `pbp_key` among other cookies and nothing else; the query reader finds `key`
beside other parameters and returns null without one; `IsOwnOrigin` accepts this machine's
origin at this port and refuses another port, another scheme and `null`; `PreferredAddress`
prefers a private range over a public one; `IsUsableLanAddress` refuses `169.254.x.y`,
`127.0.0.1` and `::1`.

- [ ] **Step 2: Run them and watch them fail** — `dotnet test --filter FullyQualifiedName~LanAccessTests`.

- [ ] **Step 3: Implement.** The two that carry the reasoning:

```csharp
/// <summary>True when a Host header names this server and no other.</summary>
/// <remarks>
/// A literal, and nothing else. That is the rebinding check: an attacker who points a
/// hostname of their own at this address has to state it here, and a name this cannot
/// parse as an address is refused (#116).
/// </remarks>
public static bool IsOwnHost(string? host, int port, IReadOnlyList<IPAddress> own)
{
    if (string.IsNullOrEmpty(host)) return false;
    var (name, statedPort) = SplitHost(host.Trim());
    if (statedPort is not null && statedPort != port.ToString()) return false;
    if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
    return IPAddress.TryParse(name, out var address) &&
           (IPAddress.IsLoopback(address) || own.Contains(address));
}

/// <summary>The address to print for another device, or null when this machine has none.</summary>
/// <remarks>
/// The interface carrying the default route is what "this machine's address" means to
/// someone else on the network — asking that way keeps a VPN, a Hyper-V switch or a WSL
/// adapter from being printed instead of the Wi-Fi. Link-local is never printed: nobody
/// can type 169.254.x.y at a tablet and reach anything.
/// </remarks>
public static IPAddress? LanAddress()
{
    var usable = OwnAddressesRouted().Where(a => IsUsableLanAddress(a.Address)).ToList();
    if (usable.Count == 0) return null;
    var routed = usable.Where(a => a.Routed).Select(a => a.Address).ToList();
    return PreferredAddress(routed.Count > 0 ? routed : [.. usable.Select(a => a.Address)]);
}
```

`SplitHost` is the subtlety: `[::1]:8787` states a name and a port, `[::1]` states only a
name, and the colons inside an IPv6 literal are not a port. Brackets come off; the rest is
compared as an `IPAddress`, so `::1` and `0:0:0:0:0:0:0:1` are the same machine.

It also fixes `Host: [::1]:8787`, which used to 404 — the old check split on the last colon
and compared `[` as a name. Say so in the doc comment rather than letting it read as a new
rule.

- [ ] **Step 4: Run them again** — green, then `dotnet format --verify-no-changes`.

## Task 2 — `Config` learns `"LanKey"`

**Files:** modify `src/MtgaPbp.Cli/Config.cs`, `tests/MtgaPbp.Tests/ConfigTests.cs`.

**Interfaces:** `public string? LanKey { get; set; }` on `Config`, and the same on the
private `Layer`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Test]
public void A_lan_key_is_absent_until_someone_sets_one() =>
    Assert.That(Config.Load(_dir).LanKey, Is.Null, "no LAN access unless it is asked for");

[Test]
public void The_lan_key_is_read_from_the_users_own_file()
{
    File.WriteAllText(Path.Combine(_dir, Config.UserFile),
        """{ "LanKey": "a-stable-key-for-the-tablet" }""");
    Assert.That(Config.Load(_dir).LanKey, Is.EqualTo("a-stable-key-for-the-tablet"));
}
```

- [ ] **Step 2: Implement** — the property, the `Layer` member, and one line in `Apply`:

```csharp
// A secret, so it is taken only when it says something. An empty string in a layer is
// not a key, and accepting it as one would turn --lan into a refusal with no way to
// see why.
if (!string.IsNullOrWhiteSpace(loaded.LanKey)) cfg.LanKey = loaded.LanKey;
```

The `<summary>` on the property is where the reasoning lives: this is the key another
device presents; it is deliberately a setting rather than a per-run secret so that a
bookmark survives; and `--lan` is what decides whether anything acts on it at all.


## Task 3 — `LiveServer` in LAN mode

**Files:** modify `src/MtgaPbp.Cli/LiveServer.cs`, `tests/MtgaPbp.Tests/LiveServerTests.cs`.

**Interfaces:**

- `public LiveServer(string rootDirectory, int port, bool lan = false, string? lanKey = null)`
  — the two defaults keep every existing call site, tests included, exactly as they are.
- `public bool Lan { get; }` — whether a key is in force.
- `public string? LanUrl { get; }` — `http://<address>:<port>/?key=<key>` for another device,
  or null when there is no key or no address another device could reach.
- `public IPAddress BoundAddress { get; }` — what the listener is on, for the test below.

- [ ] **Step 1: Write the failing tests.** Two helpers first:

```csharp
/// <summary>This machine's own LAN address, or the test is skipped for having none.</summary>
private static string LanAddressOrIgnore()
{
    var lan = LanAccess.LanAddress();
    if (lan is null) Assert.Ignore("this machine has no address another device could reach");
    return lan.ToString();
}

private LiveServer StartLan(string key)
{
    var server = new LiveServer(_root, port: 0, lan: true, lanKey: key);
    server.Start();
    return server;
}
```

Then, one test each: `Lan_mode_binds_every_interface` (`BoundAddress` is `IPAddress.Any`);
`The_lan_url_carries_the_key` (contains `?key=` and the key, while `Url` still begins
`http://127.0.0.1:`); `Loopback_needs_no_key_even_in_lan_mode` (the existing `Get("/")`
still answers 200); `A_request_from_another_machine_needs_the_key` (connect to
`LanAddressOrIgnore()` with `Host: <address>:<port>` → contains `401`);
`The_key_gives_a_cookie_and_a_clean_url` (same address, `GET /?key=<k>` → `302`,
`Location: /`, `Set-Cookie: pbp_key=<k>`); `The_cookie_stands_in_for_the_key` (same address,
`Cookie: pbp_key=<k>` → 200 and the report); `A_wrong_key_is_refused` (→ 401);
`A_page_served_over_the_lan_may_still_keep_a_match` (`POST /api/favorite/m1` with
`Origin: http://<address>:<port>` and the cookie → 200, where `attacker.example` still 403s).

The tests reach the LAN address over a real socket, and the Host check has to pass for that,
so each sends the address in `Host` as well as dialing it. That is not ceremony — it is the
same pair of facts an iPad sends. That the peer is seen as off-loopback is measured, not
assumed: a listener on `0.0.0.0` that this machine connects to at its own LAN address sees
that address as the peer, with `IPAddress.IsLoopback` false.

- [ ] **Step 2: Run them and watch them fail.**
- [ ] **Step 3: Run** `dotnet test --filter FullyQualifiedName~ConfigTests`.
- [ ] **Step 3: Implement.** Fields and the two new properties:

```csharp
public sealed class LiveServer(string rootDirectory, int port, bool lan = false,
                               string? lanKey = null) : IDisposable
{
    private readonly TcpListener _listener = new(lan ? IPAddress.Any : IPAddress.Loopback, port);
    private readonly string? _lanKey = lan ? lanKey : null;

    /// <summary>This machine's own addresses, asked once so a request need not.</summary>
    private readonly IPAddress[] _own = LanAccess.OwnAddresses();

    /// <summary>Whether a key is in force, and the report is on the network.</summary>
    public bool Lan => _lanKey is not null;

    /// <summary>Where another device on this network reaches the report, or null.</summary>
    public string? LanUrl => _lanKey is null || LanAccess.LanAddress() is not { } address
        ? null
        : $"http://{address}:{Port}/?key={_lanKey}";
```

`Url` and `Port` do not change. In `Serve`: capture `Cookie` beside `Host` and `Origin`; move
the `path`/`query` split up to just after the request line; then, after the Host check and
before any routing:

```csharp
// Admission. A peer on this machine is what the loopback bind assumed every peer was;
// it is the machine's own browser and it never had a key. Everyone else presents one,
// and a request without it learns nothing about what is here.
var presented = _lanKey is null ? null : LanAccess.KeyFromQuery(target);
var admitted = _lanKey is null
    || LanAccess.IsLoopbackPeer(PeerOf(client))
    || LanAccess.KeyMatches(presented, _lanKey)
    || LanAccess.KeyMatches(LanAccess.KeyFromCookie(cookie), _lanKey);

if (!admitted)
{
    Respond(writer, "401 Unauthorized", "text/plain",
            "not authorized — reload the address watch printed, with its ?key="u8.ToArray());
    client.Dispose();
    return;
}

// The key leaves the address bar. A navigation that carried it is answered with a
// redirect to the same page, and the cookie carries it from then on — which is also
// the only way the page's EventSource and its fetches can carry it, since neither can
// put a query string of its own on a request.
if (presented is not null && LanAccess.KeyMatches(presented, _lanKey) &&
    method is "GET" or "HEAD" && path.StartsWith('/') &&
    !path.StartsWith("/api/", StringComparison.Ordinal))
{
    Redirect(writer, path, _lanKey!);
    client.Dispose();
    return;
}
```

```csharp
/// <summary>Sends the browser to the same page without the key, and remembers it.</summary>
/// <remarks>
/// <c>HttpOnly</c> because nothing in the page needs to read it, and not <c>Secure</c>
/// because this is http by design — a Secure cookie would simply never be sent.
/// </remarks>
private static void Redirect(StreamWriter writer, string path, string key)
{
    writer.Write("HTTP/1.1 302 Found\r\n");
    writer.Write($"Location: {path}\r\n");
    writer.Write($"Set-Cookie: {LanAccess.CookieName}={key}; Path=/; SameSite=Strict; HttpOnly\r\n");
    writer.Write("Content-Length: 0\r\n");
    writer.Write("Cache-Control: no-store\r\n");
    writer.Write("Connection: close\r\n\r\n");
    writer.Flush();
}
```

`path` reaches `Location` exactly as it arrived, still percent-encoded, so a crafted target
cannot inject a header or aim the redirect off this server — and it cannot contain a raw CR
or LF, because `ReadLine` split the request line on them.

- [ ] **Step 4: Replace `IsLoopbackHost` and `IsOwnOrigin` with `LanAccess.IsOwnHost` and
  `LanAccess.IsOwnOrigin`, passing `_own`.** Their reasoning moves into `LanAccess`'s doc
  comments; the two private methods on the class are deleted rather than left as wrappers.

- [ ] **Step 5: Update the class `<summary>` and remarks** — it currently says "bound to
  loopback only", which stops being the whole truth. What belongs there: loopback by
  default, every interface under `--lan` with a key, and why the Host check survived the
  change rather than being dropped for the key (a request that fails it is not this server's
  at all, whatever key it carries).

- [ ] **Step 6: Run** `dotnet test --filter FullyQualifiedName~LiveServerTests`.

## Task 4 — `watch --lan`: the flag, the refusal, and where the address is said

**Files:** modify `src/MtgaPbp.Cli/Program.cs`.

**Interfaces:** `Watch(Config cfg, string[] operands, bool open, bool prune, bool rebuild,
bool tray, bool lan, string[] unknown)`; `Options` gains `"--lan"`.

- [ ] **Step 1: The flag, beside `--tray` and for the same reason.** `Options` becomes
  `["--open", "--rebuild", "--prune", "--tray", "--lan"]`, and `Main` reads it with the same
  warning shape:

```csharp
// Put the report on the network for this run. A flag rather than a config key, and
// more emphatically so than --tray: the README recommends starting a watch from a
// Startup-folder shortcut, so a setting that bound wider would publish the archive
// every morning on a machine nobody was watching.
var lan = args.Contains("--lan");
if (lan && command != "watch")
    Console.Error.WriteLine("warning: --lan only applies to watch; ignoring it");
```

- [ ] **Step 2: The refusal, before anything is constructed.**

```csharp
if (lan && LanAccess.Refusal(cfg.LanKey) is { } refusal)
{
    Console.Error.WriteLine($"error: {refusal}");
    return 2;
}
```

Exit 2 matches the port-in-use refusal and the bad-port-argument exit in `StopCommand` —
"you asked for something that cannot be done", not "something went wrong".

- [ ] **Step 3: Construct the server in the mode that was asked for**, and say where it is:

```csharp
using var server = new LiveServer(cfg.OutputDir, port, lan, cfg.LanKey);
```

and below the existing `serving` line, which does not change:

```csharp
// The address another device uses, on its own line because it is long and because
// the scoreboard's footer has no room for it. The key is in mtga-pbp.json, so losing
// this line to the scrollback costs nothing but scrolling back.
if (server.Lan)
    Console.WriteLine(server.LanUrl is { } lanUrl
        ? $"lan      {lanUrl}   (for another device on this network)"
        : "lan      no address on this machine reaches another device");
```

- [ ] **Step 4: The tray balloon carries the LAN address when there is one.** Under `--tray`
  the console is gone, and the balloon is the only place the address is said:

```csharp
lease.Balloon("mtga-pbp", TrayTip.Detached(server.Lan ? server.LanUrl : server.Url, unknown));
```

- [ ] **Step 5: Usage.**

```
mtga-pbp watch [port] [--tray] [--lan] serve the report and keep it live (default 8787);
                                  --tray puts it in the notification area; --lan serves
                                  it to this network, and needs "LanKey" in mtga-pbp.json
```

and a closing paragraph in the same voice as the others:

```
Set "LanKey" in mtga-pbp.json to a secret of 16 characters or more, then run
`mtga-pbp watch --lan` to read the report from a phone or tablet on the same network:
the address printed at startup carries the key once and the page remembers it. Without
a key, --lan refuses to start. Only this machine can reach the report otherwise.
```

- [ ] **Step 6: Check `UnknownOptions` still behaves** — it is derived from `Options`, so
  `--lan` stops being reported as unknown, and `--Lan` still is. Run
  `dotnet test --filter FullyQualifiedName~UnknownOptionTests`.

## Task 5 — the two documents that promise loopback only

**Files:** modify `README.md`, `SECURITY.md`.

- [x] **Step 1: `README.md`, the Live mode section.** Line 94 currently ends "It listens on
  loopback only, so nothing outside your machine can reach it." That sentence stays true and
  stops being the whole story:

```markdown
Pass a different port if 8787 is taken: `.\mtga-pbp.exe watch 9000`. It listens on
loopback only, so nothing outside your machine can reach it.

To read the report on a phone or tablet on the same network, set `"LanKey"` in
`mtga-pbp.json` to a secret of 16 characters or more and pass `--lan`:

```powershell
.\mtga-pbp.exe watch --lan
```

The address it prints at startup is the one to open over there, and it carries the key
once — the page remembers it after that, so it is worth bookmarking. Without a `"LanKey"`,
`--lan` refuses to start rather than serving the archive to everyone on the network. Your
match history names your opponents, which is why this is off unless you ask for it.
```

- [x] **Step 2: `README.md`, the command table.** The `watch` row gains `[--lan]`, and says
  what it is for in the same breath as `--tray`.

- [x] **Step 3: `SECURITY.md`, the promise and the new soft spot.** The bullet at line 24
  becomes "binds a TCP listener on `127.0.0.1` by default (default port 8787) … and, only
  with `--lan` and a configured `"LanKey"`, on every interface at once", and the soft-spot
  list gains the honest entry: with `--lan`, anyone who has the key can read the archive,
  the key travels over http (so it keeps the neighbours out rather than defending against a
  hostile network), and the archive names the people the user played against.

- [x] **Step 4: No `dist/` change is needed** — the release workflow copies `README.md` into
  the zip, so the published one follows.

## Task 6 — verification, by hand and over the wire

**Files:** none. Nothing here is committed.

- [x] **Step 1: The whole suite, from the repository root.** 1,175 passed, format clean.

```powershell
dotnet build
dotnet test
dotnet format --verify-no-changes
```

- [x] **Step 2: A scratch run that cannot touch the real archive.** *Superseded:* the live
  matrix below ran against the maintainer's real `watch --lan` on 8787 with read-only GETs
  only — nothing in the request path writes to the archive, so the isolation the scratch
  run was for was not needed.
  exe pointing at a scratch folder, on a port the maintainer's own watch is not using
  (**never 8799**), and the scratch deleted afterwards:

```powershell
$scratch = Join-Path $env:TEMP "lan-check"
New-Item -ItemType Directory -Force $scratch | Out-Null
'{ "ArchiveDir": "<scratch>\archive", "OutputDir": "<scratch>\out",
   "LogPaths": ["<scratch>\none.log"], "LanKey": "scratch-key-0123456789" }' |
  Set-Content (Join-Path $scratch "mtga-pbp.json")
```

- [x] **Step 3: The three things that must be true over a real socket.** Ran against the real
  watch, from `127.0.0.1` with a foreign `Host`, and from this machine's own LAN address:

| request | expected |
|---|---|
| `Host: 127.0.0.1:<port>`, no key | `200` — the machine's own browser never needed one |
| `Host: <lan>:<port>`, no key | `401` |
| `Host: <lan>:<port>`, `/?key=<key>` | `302`, `Location: /`, `Set-Cookie: pbp_key=` |
| the same, then `Cookie: pbp_key=<key>` on `/` | `200` |
| `Host: attacker.example:<port>` | `404` — the rebinding check is unchanged |

- [ ] **Step 4: The one that cannot be faked — a real second device.** Open the printed
  `lan` address on a phone or tablet on the same network, with `--lan` still running, and
  confirm the report renders, the ★ buttons respond, and a rebuild reaches the page without
  a reload. The page is served over the LAN now, so this is also the only check that the
  cookie survives a real browser's own rules about `SameSite`.

- [x] **Step 5: The scratch config and its folder are deleted**, and `git status` shows only
  the files this plan names. *No scratch was created; `git status` verified — exactly the
  files this plan names.*
