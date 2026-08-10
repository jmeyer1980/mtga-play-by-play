# MTGA Play-by-Play

[![CI](https://github.com/jmeyer1980/mtga-play-by-play/actions/workflows/ci.yml/badge.svg)](https://github.com/jmeyer1980/mtga-play-by-play/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Turns Magic: The Gathering Arena match logs into readable, searchable, shareable
text transcripts — plain files on your disk that you can read end to end, search
across your whole history, and paste into a chat.

Arena trackers already exist, and this is not filling an empty space —
[Arena Tutor](https://draftsim.com/arenatutor/), for one, advertises "a complete text
log of the game" (how that reads in practice, I haven't checked).

What this produces is **standalone output you own**: a static HTML index of every
archived match, a self-contained page per game, and a markdown export — files on your
disk rather than a view inside a running application, with no account and no network
access at all.

```
## Turn 7 — Opponent
- Opponent casts Leonin Vanguard
- Leonin Vanguard resolves
- Opponent gains 1 life
- Ajani's Pridemate gets 1 counter
- Opponent attacks with Ajani's Pridemate
- Opponent attacks with Rabbit
- Rabbit blocks Ajani's Pridemate
- Ajani's Pridemate deals 4 damage to Rabbit
- Rabbit deals 1 damage to You
- You lose 3 life
```

## Requirements

- Windows, MTG Arena installed
- **Detailed Logs (Plugin Support)** enabled: Arena → Settings → Account →
  *Detailed Logs (Plugin Support)*. Without it the log contains nothing to transcribe.
- .NET 10 SDK, only if you build from source

Everything resolves locally against Arena's own card database. The tool makes no
network requests.

## Use

```bash
mtga-pbp
```

That captures any new matches and rebuilds the site. Run it whenever you feel like
it — after a session, at the end of the day.

| Command | Does |
|---|---|
| `mtga-pbp` | capture new matches, then rebuild the site |
| `mtga-pbp --open` | ... and open the report in your browser |
| `mtga-pbp capture` | capture only |
| `mtga-pbp build` | re-derive the whole site from the archive |
| `mtga-pbp stats` | unhandled annotation types and unresolved cards |

If you launch by double-clicking the exe, the console window closes before you can
read anything — set `"OpenAfterBuild": true` in `mtga-pbp.json` and the report opens
every time, no flag needed.

Output lands in `%USERPROFILE%\MTGA_PlayByPlay`:

```
archive/raw/<matchId>.json.gz    durable source of truth, ~80 KB per match
out/index.html                   all games, most recent first, searchable
out/games/<matchId>.html         one self-contained page per game
out/text/<matchId>.md            markdown, for pasting into chat
```

Open `out/index.html` in any browser. Search filters on opponent, event, result,
date, and every card that appeared.

Each game page has two buttons:

- **Show verbose** swaps the readable beats for the full stream — named phases and
  steps, mana payments, and any annotation the parser did not recognise.
- **Copy transcript** puts the game on your clipboard as markdown, matching whichever
  density is currently on screen. The buttons themselves are never included.

### Run it after every session

`Player.log` is a rolling buffer — Arena truncates it on restart, and only the
previous session survives in `Player-prev.log`. If Arena restarts twice before you
run the tool, those matches are gone. Capture is idempotent, so running it often
costs nothing.

## Configuration

Optional `mtga-pbp.json` beside the executable:

```json
{
  "LogPaths": ["C:\\path\\to\\Player.log", "C:\\path\\to\\Player-prev.log"],
  "CardDbPath": "C:\\path\\to\\Raw_CardDatabase_xxx.mtga",
  "ArchiveDir": "C:\\path\\to\\archive",
  "OutputDir": "C:\\path\\to\\out",
  "OpenAfterBuild": true
}
```

Paths are discovered automatically for a Steam install; set these only if Arena
lives somewhere unusual.

## What it can and cannot tell you

The log records only what your client was told, so a transcript is fog-of-war by
nature — a game annotated by one player, not a god's-eye replay.

**It has** both players' plays, resolutions, damage, life, counters, tokens, combat
(who attacked, who blocked what), your hand and draws, and any opponent card that
became visible.

**It does not have** the opponent's hand or library, their decklist beyond what they
actually played, or **declared targets** — Arena sends target choices only to the
player making them. Interactions are therefore reported as observed effects
("Lightning Bolt resolves — Llanowar Elves destroyed") rather than declared intent.
That is symmetric across both players and records what happened rather than what was
announced.

## Install

Grab the latest `mtga-pbp-vX.Y.Z-win-x64.zip` from
[Releases](https://github.com/jmeyer1980/mtga-play-by-play/releases), unzip it
anywhere, and run `mtga-pbp.exe`. The build is self-contained — no .NET install
needed. Each release ships a `.sha256` next to the zip if you want to verify it.

Windows will show a SmartScreen prompt the first time, because the executable is not
code-signed. Verifying the checksum against the published `.sha256` is the way to
confirm you have the real thing.

WinGet manifests live in [`packaging/winget/`](packaging/winget/) and are validated,
but not yet submitted to the community repository — so `winget install` does not
resolve this package yet.

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
nothing else. Arena does not need to be installed to build or test — only to run.

```bash
git clone https://github.com/jmeyer1980/mtga-play-by-play.git
cd mtga-play-by-play
dotnet test
```

To produce the same single-file executable the releases ship:

```bash
dotnet publish src/MtgaPbp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

That writes `dist/mtga-pbp.exe` — around 72 MB, because it bundles the .NET runtime
so the machine running it needs nothing installed.

Swapping `--self-contained` for `--no-self-contained` gives a 158 KB exe, but it
needs .NET 10 present on the target machine and ships beside ~2.5 MB of dependency
DLLs. Keep `-p:PublishSingleFile=true` and that folder collapses back into one file.
The releases use the self-contained build so downloads just work.

During development you can skip publishing entirely:

```bash
dotnet run --project src/MtgaPbp.Cli -- --open
```

### Tests and CI

CI runs on Windows and checks formatting, builds with warnings as errors, runs the
tests, and audits dependencies for known vulnerabilities. Releases are cut by pushing
a `v*` tag.

The end-to-end transcript test replays a real anonymized match through the whole
pipeline. It resolves card names from a small checked-in fixture
(`tests/MtgaPbp.Tests/Fixtures/card-names.json`, ~2 KB) rather than Arena's 237 MB
card database, so it runs everywhere including CI. `CardDbIntegrationTests` covers
the real database and is the only thing that skips on a runner — including a check
that the name fixture still agrees with what Arena actually returns.

If you change the sample match, regenerate the name fixture on a machine with Arena
installed:

```bash
dotnet test --filter "FullyQualifiedName~CardNameFixtureGenerator" -- NUnit.Explicit=true
```

Design and implementation notes: [`docs/superpowers/specs/`](docs/superpowers/specs/).
