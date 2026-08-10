# MTGA Play-by-Play

Turns Magic: The Gathering Arena match logs into readable, searchable, shareable
text transcripts. Plenty of tools replay a game visually; none produce something you
can read, search, or paste into Discord.

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
| `mtga-pbp capture` | capture only |
| `mtga-pbp build` | re-derive the whole site from the archive |
| `mtga-pbp stats` | unhandled annotation types and unresolved cards |

Output lands in `%USERPROFILE%\MTGA_PlayByPlay`:

```
archive/raw/<matchId>.json.gz    durable source of truth, ~80 KB per match
out/index.html                   all games, most recent first, searchable
out/games/<matchId>.html         one self-contained page per game
out/text/<matchId>.md            markdown, for pasting into chat
```

Open `out/index.html` in any browser. Search filters on opponent, event, result,
date, and every card that appeared. Each game page has a **Show verbose** toggle that
swaps the readable beats for the full stream including phases, mana payments, and any
annotation the parser did not recognise.

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
  "OutputDir": "C:\\path\\to\\out"
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

## Build from source

```bash
dotnet test
dotnet publish src/MtgaPbp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

Design and implementation notes: [`docs/superpowers/specs/`](docs/superpowers/specs/).
