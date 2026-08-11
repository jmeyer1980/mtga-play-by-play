# MTGA Play-by-Play

[![CI](https://github.com/jmeyer1980/mtga-play-by-play/actions/workflows/ci.yml/badge.svg)](https://github.com/jmeyer1980/mtga-play-by-play/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Turns Magic: The Gathering Arena match logs into readable, searchable, shareable
text transcripts — plain files on your disk that you can read end to end, search
across your whole history, and paste into a chat.

[Arena Tutor](https://draftsim.com/arenatutor/) also produces a game log, tracks
live while you play, and is easier to install — it is a good tool and worth a look.

This exists for a narrower case: **static files you own**. An HTML index across your
whole match history, a self-contained page per game, and a markdown export — no
application running, no account, no network access, MIT licensed. If that is not what
you are after, the tracker is the better tool.

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

**Double-clicking `mtga-pbp.exe` is the simplest way** — the shipped `mtga-pbp.json`
sets `"OpenAfterBuild": true`, so it captures, builds, and opens the report. The
console window closes too fast to read, which is exactly why that setting exists.

From a terminal, `cd` to the folder the exe is in first. **PowerShell will not run a
program from the current directory without the `.\` prefix:**

```powershell
cd C:\path\to\mtga-pbp
.\mtga-pbp.exe --open
```

`mtga-pbp` on its own only works once the folder is on your `PATH` — see below. In
`cmd.exe` the `.\` is optional; in PowerShell it is not.

| Command | Does |
|---|---|
| `.\mtga-pbp.exe` | capture new matches, then rebuild the site |
| `.\mtga-pbp.exe --open` | ... and open the report in your browser |
| `.\mtga-pbp.exe watch` | serve the report and keep it live (see below) |
| `.\mtga-pbp.exe capture` | capture only |
| `.\mtga-pbp.exe build` | re-derive the whole site from the archive |
| `.\mtga-pbp.exe stats` | unhandled annotation types and unresolved cards |
| `.\mtga-pbp.exe keep <matchId>` | never prune this match |
| `.\mtga-pbp.exe unkeep <matchId>` | allow it to be pruned again |

### Live mode

```powershell
.\mtga-pbp.exe watch
```

Leave that running and the report updates itself as you play. It opens
`http://127.0.0.1:8787/`, polls the log, re-captures when a match ends, and pushes a
refresh to the open page. Your scroll position and whatever is in the search box both
survive the update — it swaps the rows rather than reloading.

Pass a different port if 8787 is taken: `.\mtga-pbp.exe watch 9000`. It listens on
loopback only, so nothing outside your machine can reach it.

This is also the only mode where the ★ buttons work. Opened from disk the page is
static — browsers block `fetch` on `file://`, which is deliberate in the design — so
the stars show which matches are kept but cannot change them. Use `keep`/`unkeep`
from the command line, or run `watch`.

### Keeping the archive from growing forever

Set `"MaxArchivedMatches": 60` in `mtga-pbp.json` and the oldest match is dropped
whenever a new one arrives, deleting its archive, page and markdown together.

**Starred matches never count against the cap and are never deleted** — a cap of 60
with 70 kept matches keeps all 70. The cap defaults to `0`, meaning no limit, so
nothing is ever deleted unless you ask for it.

### Running it from anywhere

To type `mtga-pbp` from any directory, add its folder to your `PATH` once:

```powershell
[Environment]::SetEnvironmentVariable(
    'Path',
    [Environment]::GetEnvironmentVariable('Path', 'User') + ';C:\path\to\mtga-pbp',
    'User')
```

Open a new terminal afterwards for it to take effect. A winget install would do this
for you, but the package is not submitted yet — see
[`packaging/winget/`](packaging/winget/).

There is no safe `cmd.exe` one-liner for this — `setx PATH "%PATH%;..."` folds the
system `PATH` into your user `PATH` and truncates at 1024 characters. From `cmd.exe`,
either run the PowerShell line above via `powershell -NoProfile -Command "..."` or use
Windows' *Edit environment variables for your account* dialog.

Output lands in `$env:USERPROFILE\MTGA_PlayByPlay` (`%USERPROFILE%\MTGA_PlayByPlay` in
`cmd.exe`):

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

### Run it whenever, including mid-session

**You do not need to quit Arena.** Finish a match, alt-tab, run `mtga-pbp`, and read
it. The log is opened in a way that tolerates Arena's own write handle, so the game
can keep running and keep logging while the tool reads.

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

**It does not have** the opponent's hand or library, or their decklist beyond the
cards they actually played.

Everything else you would want is there, for both players: what each spell targeted
(`Opponent casts Bitter Triumph, targeting Ghostly Dancers`), what caused each effect
(`Deadly Cover-Up exiles Toby, Beastie Befriender`), where scried cards went, which
abilities triggered, and how the match really ended — a concede and a timeout no
longer look the same as losing on board.

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

Every command below is shown in PowerShell, and every one of them is byte-identical in
`cmd.exe` and in Git Bash — the `dotnet` and `git` CLIs take the same arguments in all
three, and forward slashes in paths are fine on Windows. Only two things in this README
are shell-specific: the `.\` prefix when running the exe, and `$env:VAR` versus
`%VAR%`.

```powershell
git clone https://github.com/jmeyer1980/mtga-play-by-play.git
cd mtga-play-by-play
dotnet test
```

To produce the same single-file executable the releases ship:

```powershell
dotnet publish src/MtgaPbp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

That writes `dist/mtga-pbp.exe` — around 72 MB, because it bundles the .NET runtime
so the machine running it needs nothing installed.

Swapping `--self-contained` for `--no-self-contained` gives a 158 KB exe, but it
needs .NET 10 present on the target machine and ships beside ~2.5 MB of dependency
DLLs. Keep `-p:PublishSingleFile=true` and that folder collapses back into one file.
The releases use the self-contained build so downloads just work.

During development you can skip publishing entirely:

```powershell
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

```powershell
dotnet test --filter "FullyQualifiedName~CardNameFixtureGenerator" -- NUnit.Explicit=true
```

Design and implementation notes: [`docs/superpowers/specs/`](docs/superpowers/specs/).
