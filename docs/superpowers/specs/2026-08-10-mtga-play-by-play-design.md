# MTGA Play-by-Play — Design

**Date:** 2026-08-10
**Status:** Approved (architecture, format, runtime, output layout)

## Purpose

Turn Magic: The Gathering Arena match logs into readable, chess-transcription-style
text. Existing tools replay a game visually; none produce a transcript you can read,
search, or paste into a chat.

Three uses drive the design, all confirmed with the user:

1. **Review my own play** — find the turn a game went wrong.
2. **Search my history** — "every game against mono-red", "every game with this card".
3. **Share or discuss** — paste a readable game into Discord.

Archival alone is explicitly *not* the goal, so search infrastructure and a
standalone text export are in scope rather than optional.

## Source data

Verified against the user's actual machine on 2026-08-10.

| Item | Value |
|---|---|
| Log | `%USERPROFILE%\AppData\LocalLow\Wizards Of The Coast\MTGA\Player.log` (+ `Player-prev.log`) |
| Detailed Logs (Plugin Support) | Already enabled |
| Log size | 49 MB, 24 matches, ~97k lines (5,180 JSON, 92,198 non-JSON) |
| GRE payload | 1.5 MB per match raw; **19:1** gzip → **~80 KB per match** |
| Card DB | `…\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw\Raw_CardDatabase_*.mtga` — SQLite, 237 MB, read-only |
| Runtime | C# / .NET 10.0.302 |

Each JSON line is self-contained: `{ transactionId, requestId, timestamp,
greToClientEvent: { greToClientMessages: [...] } }`. `timestamp` is epoch
milliseconds as a **string**.

Extrapolated archive cost: **~78 MB per 1,000 matches** compressed. Raw archival is
cheap, which is what makes the two-stage design below affordable.

## Architecture

Approach A, approved: **capture raw, derive everything else.**

```
capture   Player.log + Player-prev.log
            → slice into matches, dedupe by matchId
            → archive/raw/<matchId>.json.gz          (~80 KB each)

build     archive/raw/*.gz
            → parse into a normalized event stream
            → out/index.html
              out/games/<matchId>.html
              out/text/<matchId>.md
```

The raw slice is the durable source of truth. The parser will be incomplete at
first — MTG has thousands of cards and the GRE emits annotation types that will not
all be handled in v1. Keeping raw means every parser improvement can be applied
retroactively to the entire history via `--rebuild`. Storing only normalized events
would freeze old games at v1 quality, and at 80 KB/match the disk saving buys
nothing.

### Components

Each has one job, a narrow interface, and is testable alone.

| Component | Responsibility | In → Out |
|---|---|---|
| `LogScanner` | Read a log file, yield parsed JSON envelopes; skip non-JSON | path → `IEnumerable<LogEnvelope>` |
| `MatchSlicer` | Group envelopes into matches by `matchId` | envelopes → `MatchSlice[]` |
| `RawArchive` | Idempotent gzip write + dedupe ledger | `MatchSlice` → file |
| `CardDb` | `grpId` → card info; `locId` → string | int → `CardInfo` |
| `GameStateTracker` | Apply Full/Diff states; track objects, zones, life, turn, ID aliasing | messages → state |
| `EventExtractor` | Walk annotations in order, emit typed events | state + annotations → `GameEvent[]` |
| `Narrator` | Render events to beats and verbose lines | `GameEvent[]` → text |
| `Renderer` | Emit index, per-game pages, markdown | events → files |

Boundary rule: `EventExtractor` is the only component that knows GRE annotation
shapes. `Narrator` and `Renderer` see only `GameEvent`. This means a GRE format
change touches exactly one component, and the renderers can be tested against
hand-built event lists with no log fixtures.

### Project layout

```
src/MtgaPbp.Cli/        entry point, command parsing, config
src/MtgaPbp.Core/       model, scanner, slicer, archive, card db, tracker, extractor
src/MtgaPbp.Render/     narrator, html + markdown renderers
tests/MtgaPbp.Tests/    unit + golden-file tests
```

Two libraries rather than one per component: the components are small, and the
meaningful boundary is *parsing* versus *presentation*.

## Identifying the local player

Needed to say "you" instead of a screen name, and to know which hidden zones are
observable.

The GRE sends decision requests only to the seat that must decide.
`GREMessageType_MulliganReq` carries `systemSeatIds: [N]` where N is the local seat.
It appears once per *game*, not per match — the sample log shows 24 across 24
matches only because those are all Bo1 Ladder games. A Bo3 match emits one per game,
all with the same seat, so taking the first is correct either way.

Resolution order:

1. First `MulliganReq.systemSeatIds` in the match
2. First `ActionsAvailableReq.systemSeatIds` (fallback)
3. `localPlayerUserId` in config (manual override)

Seat numbering is per-match, not global: the user is seat 1 in one sampled match and
seat 2 in another. Resolve the seat per match, never carry it across.

The `userId` and screen name then come from
`matchGameRoomStateChangedEvent.gameRoomConfig.reservedPlayers[]`, matched on
`systemSeatId`.

## Card name resolution

Two paths, in order:

1. **`gameObject.name`** — a LocId on the object itself. Correct for tokens,
   adventure halves, and double-faced rooms (`92060` →
   `"Dollmaker's Shop // Porcelain Gallery"`). Covers 214 of 309 grpIds observed.
2. **`Cards.TitleId`** → `Localizations_enUS` — for objects seen without a `name`.
   Covers 5 more.

Both resolve through `Localizations_enUS(LocId, Formatted, Loc)`. **Card titles are
stored at `Formatted = 1`, not `0`** — querying `Formatted = 0` returns NULL for
titles and silently fails. Query with `ORDER BY Formatted LIMIT 1`.

The remaining 90 grpIds are **abilities**, not cards — they have no row in `Cards`.
These resolve via the ability object's `objectSourceGrpId` → the source card's name,
rendered as `"<Card>'s ability"`.

Unresolvable grpIds render as `Card #<grpId>` and are written to
`out/unresolved.txt` so gaps are discoverable rather than silent.

## The event model

`EventExtractor` emits an ordered list of typed events. Every event carries
sequence, timestamp, game number, turn, active seat, phase, and step, so any event
can be located and any prefix can be replayed.

Event kinds, all confirmed present in the sample log:

| Kind | Source |
|---|---|
| `GameStart` | `gameInfo`, `DieRollResultsResp` |
| `Mulligan` | `MulliganReq` + hand size |
| `TurnStart` | `AnnotationType_NewTurnStarted` (290) |
| `PhaseChange` | `AnnotationType_PhaseOrStepModified` (3,282) — verbose only |
| `LandPlayed` | `ZoneTransfer` category `PlayLand` (227) |
| `SpellCast` | `ZoneTransfer` category `CastSpell` (263) |
| `Resolved` | `ZoneTransfer` category `Resolve` (256), `ResolutionStart` (738) |
| `Countered` | `ZoneTransfer` category `Countered` |
| `Drew` | `ZoneTransfer` category `Draw` (341) |
| `Discarded` | `ZoneTransfer` category `Discard` (22) |
| `Destroyed` / `Sacrificed` / `Exiled` / `Returned` | `ZoneTransfer` categories `Destroy` (30), `Sacrifice` (14), `Exile` (10), `Return` (3) |
| `StateBasedAction` | `ZoneTransfer` categories `SBA_*` — `SBA_Damage` (92), `SBA_UnattachedAura`, `SBA_LegendRule`, `SBA_ZeroLoyalty`, `SBA_ZeroToughness`, `SBA_Deathtouch` |
| `ZoneMove` | any other `ZoneTransfer` category (e.g. `Put`, 11) — generic fallback so no movement is dropped |
| `Damage` | `AnnotationType_DamageDealt` (314) — `affectorId` = source |
| `LifeChanged` | `AnnotationType_ModifiedLife` (209) |
| `TokenCreated` | `AnnotationType_TokenCreated` (220) |
| `CounterChanged` | `CounterAdded` (190) / `CounterRemoved` (23) |
| `Scry` | `AnnotationType_Scry` (46) |
| `Revealed` | `AnnotationType_RevealedCardCreated` (23) |
| `ManaPaid` | `AnnotationType_ManaPaid` (752) — verbose only |
| `Attack` / `Block` | `gameObject.attackInfo` / `blockInfo` |
| `GameEnd` | `finalMatchResult.resultList` |
| `Unknown` | any unhandled annotation type — verbose only, counted |

`AnnotationType_ObjectIdChanged` (1,080) is **not** an event. It is an aliasing
instruction: an object's `instanceId` changes when it moves zones. `GameStateTracker`
maintains a union-find alias map so a card followed across cast → resolve →
battlefield → graveyard reads as one entity. Getting this wrong is the single most
likely source of nonsense transcripts, so it is tested directly.

### Density

Approved: **beats by default, verbose on toggle.** Density is a filter over the same
event list, not a second parse. Beats excludes `PhaseChange`, `ManaPaid`,
`Unknown`, and priority-level detail; verbose includes everything.

## What can and cannot be reported

Stating this plainly because it bounds the product.

**Can:** both players' plays, resolutions, damage, life, counters, tokens, combat
(who attacked, who blocked what), board state at any point (power, toughness, damage
marked, tapped, loyalty), your hand and draws, and any opponent card that becomes
visible.

**Cannot:**

- **The opponent's hand and library.** The log contains only what the client was
  told. Transcripts are fog-of-war by nature — a game annotated by one player, not a
  god's-eye replay.
- **Declared targets.** `SelectTargetsReq` lists *legal* targets and is sent only to
  the choosing player; `PlayerSubmittedTargets` carries no target IDs. Targets are
  therefore unavailable for the opponent and asymmetric for the user.
- **The opponent's decklist** — only the cards they actually played.

The targeting gap is handled by reporting **observed effects instead of declared
intent**, attributed via `DamageDealt.affectorId` and the annotations that follow
`ResolutionStart`:

```
Casts Lightning Bolt
  Llanowar Elves dealt 3 — destroyed
```

rather than `Bolt targeting Elves`. This is symmetric across both players and
records what happened rather than what was announced. Recovering the user's *own*
declared targets from `SelectTargetsReq` is possible but deliberately out of scope
for v1, because a transcript where only one player's targets appear is more
confusing than one where neither does.

## Output

Constraint: **browsers block `fetch()` on `file://`.** Anything searchable must be
embedded in the page that searches it, which rules out `index.html` + `games.json`.

- **`out/index.html`** — self-contained, sorted most-recent-first. One `<tr>` per
  game rendered **statically** in the markup, carrying date, event/queue, opponent,
  result, and turn count, plus a `data-search` attribute holding the lowercased
  haystack (those fields plus every card name that appeared). Search filters rows by
  toggling `hidden`.

  Rows are markup rather than script-built output so the page works with JavaScript
  disabled, the browser's own find-in-page sees every opponent and card name, and each
  link is a real anchor. Search is progressive enhancement, not the thing that makes
  the page exist. At ~1 KB per game, 1,000 games is a ~1 MB index.
- **`out/games/<matchId>.html`** — one static self-contained file per game. Contains
  both densities with a toggle button. No fetch. Opens by clicking a link from the
  index; also stands alone if sent to someone.
- **`out/text/<matchId>.md`** — plain markdown of the beats view, for pasting into
  Discord. No dependency on the local card database.

Supporting review, each transcript page carries:

- A per-turn anchor (`#t7`) so a specific turn can be linked or bookmarked.
- A turn header showing both life totals entering the turn.
- At the end of each turn, a one-line board summary per player: creature names with
  current power/toughness, damage marked, tapped state, and any counters — derived
  from `GameStateTracker`, not re-inferred by the renderer.

That is the whole of the review affordance for v1. No interactive board widget, no
step-through scrubber.

## Error handling

The governing rule: **never crash on a log, never fail silently.** A log is
untrusted input that changes with every Arena patch.

| Condition | Behaviour |
|---|---|
| Non-JSON line (92k of them) | Skip, count |
| Malformed JSON line | Skip, count, record line number |
| Unknown annotation type | Emit `Unknown` event, count by type name; visible in verbose |
| Unresolvable grpId | Render `Card #<grpId>`, write to `unresolved.txt` |
| Match truncated by log rotation | Archive anyway, mark `Incomplete`, render with a banner |
| Card DB not found | Hard error naming the exact path searched |
| Archive entry already present | Skip; capture is idempotent |

`mtga-pbp stats` reports unknown annotation types and unresolved cards by frequency.
This is the discoverability mechanism: it turns "the transcript looks a bit off" into
a ranked list of what to implement next.

## Testing

- **Golden-file tests** are the primary safety net. Two or three real match slices
  are checked in as fixtures with expected rendered markdown. They exercise scanner →
  slicer → tracker → extractor → narrator in one pass, which is where regressions
  actually appear. Fixtures have opponent screen names and user IDs replaced with
  stable pseudonyms; the user's own IDs are scrubbed too.
- **Unit tests** for the parts with real logic and easy-to-get-wrong edges:
  `GameStateTracker` diff application, `ObjectIdChanged` alias chains (including
  multi-hop), `MatchSlicer` boundaries (interleaved matches, truncated tail),
  `CardDb` resolution including the `Formatted` trap and the ability fallback.
- **Renderer tests** run against hand-built `GameEvent` lists — no log fixtures
  needed, because of the component boundary above.

Per project policy, `Assert.Warn` is reserved for documented accepted limitations
and must assert the specific known condition. The targeting gap is the expected
case: a test asserts that a spell with a known target renders as an effect rather
than a target, so that if Arena ever starts emitting target IDs, the test tells us.

## CLI

```
mtga-pbp                    capture + build (default)
mtga-pbp capture            capture only
mtga-pbp build              rebuild HTML from archive
mtga-pbp build --rebuild    force re-parse of every archived match
mtga-pbp stats              unknown annotations, unresolved cards
```

Config at `mtga-pbp.json` beside the executable: log paths, card DB path, output
directory, optional `localPlayerUserId` override. Card DB is located by globbing the
Steam path and taking the newest `Raw_CardDatabase_*.mtga`; config overrides it.

Shipped as a self-contained single-file `.exe` so it can be run by double-clicking
after a play session.

## Discovered during implementation

Four things only real data revealed. All are now covered by regression tests.

**The match id is sticky.** Only `GameStateType_Full` carries `gameInfo.matchID` — 74
lines out of 4,774 in the sample log. Every `GameStateType_Diff`, which is where the
annotations live, has no match id at all and must inherit the match in progress. The
first implementation dropped 98% of the game data and produced a 168 KB archive
instead of 2.0 MB.

**`JsonElement.TryGetInt32` throws.** It returns `false` only when a number will not
fit; when the element is not a number at all it raises `InvalidOperationException`.
Arena sends nominally numeric fields as strings often enough to crash a build. All
numeric reads go through `Json` in `MtgaPbp.Core`, which checks `ValueKind` first.

**Combat has no annotation.** Attacks and blocks appear only as `attackState` /
`blockState` transitions on the game object, so they are read from tracker transition
reports rather than the annotation stream. Arena never sends `AttackState_None` — it
simply stops sending the field — so combat state is cleared on a turn change.
Without that, a creature is treated as permanently attacking after its first swing
and every later attack goes unreported.

**Hidden cards have no controller.** A card the opponent draws has no game object, so
the actor falls back to the active player. Otherwise every opponent draw and scry
reads as "Someone".

Two smaller notes: `build` never caches, so `--rebuild` is accepted and documented but
changes nothing today; and events whose subject resolves only to a bare instance id
(a token that left play before the client described it) are dropped from beats and
kept in verbose.

## Out of scope for v1

Deliberately excluded to keep the first version finishable:

- Live/background log watching (approved as manual-run; archive format leaves the
  door open for a watcher as a second entry point)
- Declared-target recovery for the user's own spells
- Deck archetype classification
- Opponent decklist inference
- Any network access — everything resolves locally
