# Security

## Reporting

Email **jmeyer1980@gmail.com**, or open a
[private security advisory](https://github.com/jmeyer1980/mtga-play-by-play/security/advisories/new).

Please do not open a public issue for anything that could expose someone's data before
there is a fix. I am one person and this is a hobby project — expect a reply in days, not
hours, and expect me to be grateful rather than defensive.

## What this program actually does

Knowing the shape of it makes it much easier to judge whether something is a real
finding:

- **Reads** MTG Arena's `Player.log` and `Player-prev.log`, and Arena's card database
  (read-only, opened so the game can keep writing).
- **Writes** a gzip archive, HTML pages and markdown files under
  `%USERPROFILE%\MTGA_PlayByPlay`.
- **Makes no outbound network requests of any kind.** No telemetry, no update check, no
  account, no card-image fetching. Card names are resolved from the local database that
  ships with Arena.
- **In `watch` mode only**, binds a TCP listener on `127.0.0.1` (default port 8787) to
  serve the report to your own browser and push refreshes.

## The parts most worth attacking

If you are looking for something real, these are the honest soft spots:

1. **The `watch` HTTP server.** Hand-rolled on `TcpListener`, not a hardened stack. It
   binds loopback only, but anything reachable by other local processes is worth
   scrutiny — path traversal out of the output directory, request smuggling, or a
   malformed request crashing or hanging the listener.
2. **Log parsing.** Input is a file another program writes. Malformed or hostile JSON
   should never do worse than skip a line. Unbounded memory growth on a crafted log
   counts as a bug.
3. **HTML generation.** Card names, player names and event text come from the log and
   are escaped on the way into pages. A name that escapes its context and executes is a
   genuine finding, even though the page is local.
4. **Path handling.** Match ids become filenames. An id that escaped its directory would
   matter.

## Out of scope

- **The SmartScreen warning on first run.** The executable is not code-signed. The
  published `.sha256` beside each release is the way to verify a download.
- **Anything requiring an attacker who can already write to your Arena log or your
  output directory** — at that point they are already running code as you.
- **The transcripts containing opponent screen names.** That is the log's content and
  the point of the tool. They are yours and are never transmitted anywhere. Do scrub
  them before pasting a transcript into a public issue.

## Supported versions

Latest release only. This is a single-maintainer hobby project; fixes ship forward
rather than being backported.
