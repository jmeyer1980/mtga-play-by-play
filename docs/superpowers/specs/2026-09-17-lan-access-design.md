# Investigation — reading the live report from another device

**Date:** 2026-09-17
**For:** asked for directly by the maintainer; no issue filed
**Version investigated:** 0.12.2 (`bb60dd9`)
**Measured on:** Windows 11 with the report served by `mtga-pbp.exe watch 8799 --tray --open`
from `dist/`, over a home Wi-Fi LAN.

## The short version

`watch` binds `127.0.0.1` and only `127.0.0.1` (`LiveServer.cs:31`), so nothing off this
machine can reach it and *no firewall rule can change that* — Windows Filtering Platform
never sees a loopback-only listener. That is the whole reason the report could not be read
on an iPad, and the reason an evening went into a firewall rule that was already correct.

Add an opt-in `--lan` flag to `watch`: it binds every IPv4 interface, and it requires a key
from a new `"LanKey"` setting in `mtga-pbp.json`. With `--lan` and no key, `watch` refuses
to start and prints one to paste — so "exposed" and "unauthenticated" cannot happen
together. The existing Host and Origin checks survive the change unchanged in spirit: a
hostname is still refused, and only this machine's own literal addresses join the allowlist,
which keeps the DNS-rebinding defence of #116 intact.

## What runs today

- `LiveServer` is hand-rolled on `TcpListener`, bound to `IPAddress.Loopback` in the field
  initializer. `Url` is `http://127.0.0.1:{port}/` and everything that says where the report
  is — the console's `serving` line, the scoreboard's footer, the tray balloon, `--open` —
  is fed from it.
- Two headers are load-bearing and the rest are discarded: the `Host` (the anti-rebinding
  check, `IsLoopbackHost`) and the `Origin` (the anti-cross-site-POST check, `IsOwnOrigin`).
  Both accept `127.0.0.1` or `localhost` with this server's own port, and nothing else.
- `README.md:94` says it plainly — "It listens on loopback only, so nothing outside your
  machine can reach it" — and `SECURITY.md:24` says it twice.

## The wrong first suspect

The report was reachable on this machine and not from an iPad, so the firewall was blamed
and a rule was written for it: inbound, allow, TCP, local port 8799, remote `LocalSubnet`,
profile Private. Every part of that is correct — it names the port the watch was actually
running on, and the network had been moved from Public to Private so the profile would
match. It could not have worked, and here is the measurement that says so:

| asked | answered |
|---|---|
| `netstat -ano` for the watch's pid | `TCP 127.0.0.1:8799 0.0.0.0:0 LISTENING` |
| `Test-NetConnection <this machine's LAN address> -Port 8799`, from this machine | `TcpTestSucceeded: False` |

The machine cannot reach its own LAN address on that port. A rule on an interface that never
receives a packet is decoration; the traffic was refused before the firewall was consulted.

**The same class of check, aimed at the other suspect.** A T-Mobile home gateway sits in
front of that LAN and its stock firmware has an isolation setting on some models, so the
gateway was asked rather than assumed: its web UI is a React app whose complete bundle set
was read out of `http://<gateway>/static/js/` — the webpack chunk map at `n.u` names all
three lazily-loaded chunks, so "the whole app" is a claim with a method behind it. Across
all four files the strings `isolat`, `communicat`, `Guest`, `Advanced`, `Firewall`, `UPnP`,
`Port Forward` and `DHCP` occur **zero** times — this firmware has no such switch to look
for. Isolation is off besides: two other clients on that LAN answered unicast ICMP echo, and
one of them showed `Reachable` in the neighbour cache, a state that requires its unicast ARP
reply to have crossed client-to-client at the access point. Under isolation neither happens.
The network is exonerated, and the loopback bind is the only blocker left.
## What the code does to a foreign caller

Confirmed against the running server with raw sockets, since it is the second thing that
would have bitten immediately after a wider bind:

| request | answered |
|---|---|
| `Host: 127.0.0.1:8799` | `HTTP/1.1 200 OK` |
| `Host: <this machine's LAN address>:8799` | `HTTP/1.1 404 Not Found` |

So `netsh interface portproxy` and every other "forward it from the outside" workaround
would have delivered a 404 to the iPad and a 403 to the ★ button, because the page's own
`fetch` would carry a LAN `Origin`. Widening the bind is necessary and not sufficient.

## Measurements that decided the design

| question | measured |
|---|---|
| What does a listener on `0.0.0.0` see as the peer when the same machine connects to its own LAN address? | The LAN address — `192.168.1.50:50080`, `IPAddress.IsLoopback` **False** — not `127.0.0.1`. So the key path is exercised by real off-loopback traffic and can be tested in-process; an iPad is genuinely a foreign peer. |
| Same question through `IPv6Any` with a connect to `::1` | `[::1]:50078` — loopback. IPv6 needs no special case in the peer test. |
| Does the served page make requests that cannot carry a query string? | Yes, three: `new EventSource('/api/events')`, `fetch('/')` on the change stream, and `POST /api/favorite/<id>`. All same-origin and relative, so a cookie reaches them and a `?key=` in the address bar does not. |
| Does any page link off-origin, where a `?key=` in the URL could ride out in a `Referer`? | Yes — card faces link to Scryfall (`GamePageRenderer.cs:293`, `target="_blank" rel="noopener"`). Cross-origin referrers default to origin-only, so the query does not travel today. That is a default, not a decision, and it is why a keyed URL is redirected to a clean one rather than left in place. |
## The design

**1. A flag for the exposure, a setting for the secret.** `--lan` is per-run, like `--tray`
and for the same reason: `README.md` recommends running `watch` from a Startup-folder
shortcut, so a config key that *bound wider* would put the archive on the network every
morning, silently, on a machine nobody was watching. The key is the other half — it has to
outlive the run or the iPad's bookmark breaks on every restart, and a secret that is stable
belongs in the file the user owns, not on a command line that lands in a shortcut and a
shell history.

**2. `--lan` without a usable `"LanKey"` refuses to start** (exit 2), naming the file and
printing a fresh 32-character key to paste. This is the rule recorded for the memory
bridge's own LAN bind, and it is worth keeping: the refusal is what makes an exposed report
never an unauthenticated one. A minimum of 16 characters, refused below that rather than
truncated or hashed into something that only looks strong.

**3. The key arrives as a query string, and stays as a cookie.** A first navigation to
`http://<lan>:8799/?key=<key>` is answered with `302` to the same path without the query and
a `Set-Cookie: pbp_key=<key>; Max-Age=…; Path=/; HttpOnly; SameSite=Strict` cookie — a
persistent credential the browser remembers, not a session cookie, so a tablet that slept
overnight stays admitted when it wakes. Everything the page
does afterwards — the three requests above, and every link the reader clicks — carries the
cookie, which is what a query string alone cannot do. The redirect exists so the key does
not sit in the address bar or in history; it fires on every keyed document navigation,
cookie or no cookie, and the page still works if a reader opens a keyed URL
twice.

**4. Loopback peers are exempt, and nothing about today's behaviour changes.** A browser on
this machine keeps working with no key at all, which is what every existing test and every
existing user expects. The key guards the network, not the machine.

**5. The Host and Origin allowlist grows by this machine's own addresses — literally.**
`IsLoopbackHost` becomes `IsOwnHost` and admits `127.0.0.1`, `localhost`, and any unicast
address assigned to one of this machine's up interfaces, each with this server's own port;
`IsOwnOrigin` admits the same set. A *hostname* is still refused, which is the point: DNS
rebinding needs one, and there is no hostname an attacker can point at this server that the
check will take. IPv6 literals are compared with their brackets stripped, so `[::1]:8799`
works where it used to 404 — a fix that falls out of doing this properly, not a behaviour
anyone relied on.

**6. Authorization is asked before routing and after the Host check.** A foreign request
with no key gets `401`, a body that says how to get one and nothing about what is here, and
its socket closed. Order matters: a rebinding page still gets the `404` it gets today, so
the check that predates this feature is not weakened by the one that joins it. Comparison is
constant-time over SHA-256 of both keys, so a wrong key cannot be walked one character at a
time.

## What this costs

- `SECURITY.md` has to change: it promises loopback-only binding in two places, and after
  this it promises loopback-only *by default*, with the new mode's threat model stated —
  anyone who has the key can read the archive, so the key is the whole of the access
  control, and the LAN is assumed to be a home one.
- A secret at rest in `mtga-pbp.json`. It is the user's own file, next to an unencrypted
  archive of every match they have played; the key protects a network, not a filesystem.
- The archive holds real opponent names, which is what makes sharing it a decision the user
  makes rather than a default the tool takes.

## What I would like confirmed, one line each

1. **The key is a `"LanKey"` setting, not printed freshly per run.** Stable bookmarks and a
   URL that can be re-read at any time, at the cost of a secret in the config file.
2. **`--lan` refuses to start without it** rather than generating one and printing it.
3. **IPv4 only** for now.
4. **No warning when `"LanKey"` is set but `--lan` was not passed.** It is benign, and a line
   on every run is the kind of noise this tool has been cutting, not adding.
**7. The URL the tool prints does not change meaning.** `Url` stays
`http://127.0.0.1:{port}/` and keeps feeding the scoreboard footer and `--open`. A new
`LanUrl` carries the keyed address; `watch` prints it on its own line under `serving`, and
gives it to the tray balloon — under `--tray` there is no console to read it from. The key
being stable is what makes losing that line survivable: it is in `mtga-pbp.json`.

**8. IPv4 only.** The flag binds `IPAddress.Any`. The address printed is IPv4, the LAN it is
for is IPv4, and dual-stack binding buys a second address family to explain and test for no
reader who needs it. The peer test above shows IPv6 peers would work if it were added.