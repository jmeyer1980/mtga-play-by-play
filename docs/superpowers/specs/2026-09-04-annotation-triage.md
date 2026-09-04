# Annotation triage

**Date:** 2026-09-04
**For:** condition 2 of [What 1.0.0 means](2026-09-04-1.0.0-definition-design.md)
**Measured on:** 0.7.0, 1,412 archived matches — 16 unhandled types, 246 occurrences

Condition 2 asks that every annotation type Arena emits is either narrated or written into
CONTRIBUTING's "Things deliberately not done" with evidence. This is the triage that sorts
them.

## Method

Each type was sampled from the archive and its payload read — not its name. A type is
worth narrating only if it carries something a reader would miss, and several carry
nothing at all.

## The classification

| type | count | verdict |
|---|---|---|
| `ReplacementEffectApplied` | 111 | **narrate** — own issue |
| `AbilityWordActive` | 40 | **narrate** — own issue |
| `ReplacementEffect` | 21 | **narrate** — same issue as above |
| `LayeredEffect` | 12 | deliberate |
| `CoinFlip` | 11 | **narrate** — own issue |
| `RemainingSelections` | 11 | deliberate |
| `TurnPermanent` | 9 | deliberate |
| `CastingTimeOption` | 7 | deliberate |
| `HighlightReason` | 6 | deliberate |
| `DieRoll` | 6 | deliberate — already reported by another path |
| `ModifiedPower` | 3 | deliberate — already reported as a counter |
| `RemoveAttachment` | 3 | deliberate |
| `PermanentRegenerated` | 2 | **narrate** — own issue |
| `SelectNDecoration` | 2 | deliberate |
| `AddAbility` | 1 | deliberate — the other 5,700 are mined |
| `ModifiedColor` | 1 | deliberate |

Five types are worth narrating and eleven are not. The five become four issues —
`ReplacementEffect` and `ReplacementEffectApplied` share one — rather than landing here,
because each needs a sentence designed and a test, and one needs an investigation first.

5 + 11 = 16, which is every unhandled type.

## Why each deliberate one is deliberate

**`LayeredEffect`** (12) and **`RemainingSelections`** (11) both carry an empty `details`
object, and neither name says what happened on its own — a "layered effect" and a count of
"remaining selections" are machinery, not events. There is nothing in them to say.

(`PermanentRegenerated` also has an empty payload but is narrated rather than dropped,
because unlike those two its name *is* the fact. See below.)

**`TurnPermanent`** (9) carries only `turned: 0`. Whether that is a face-down flip, a
tapped state or something else cannot be told from a single bare integer, and guessing
would be inventing.

**`CastingTimeOption`** (7) carries `castAbilityGrpId` and a `type` ordinal. The
transcript already says what was cast; which of a spell's casting options was chosen would
need the ability text and a table for `type`, for seven occurrences.

**`HighlightReason`** (6) carries strings like `SacrificeResourceIsTargetOfEffect`. This is
client UI state — what Arena highlighted on screen — not a game event.

**`DieRoll`** (6) carries a real roll: `Result`, `NaturalResult`, `Faces`, `Ignored`. It is
nonetheless redundant. The opening already reports the roll, read from
`GREMessageType_DieRollResultsResp` — a *message*, not an annotation
(`src/MtgaPbp.Core/EventExtractor.cs:684`). These six are the same rolls arriving on a second surface, and
narrating them would say the die roll twice.

**`ModifiedPower`** (3) carries `count` and `counter_type`, which is a counter, and
counters are already narrated through `CounterAdded`. Same fact, second surface.

**`RemoveAttachment`** (3) carries `invalidating_grpid` — the ability that made the
attachment illegal. The aura or equipment leaving is already visible in the board state,
and three occurrences do not justify a sentence that would need the invalidating ability
resolved and named to be worth reading.

**`SelectNDecoration`** (2) carries `affected_objects` and nothing about what the selection
was for. UI decoration, as the name says.

**`AddAbility`** (1) is a straggler, not a gap. `AnnotationType_AddAbility` *is* mined — it
is how ability grants are narrated as of #5, and the archive carries 5,771 of them on the
persistent-annotation surface. This single one arrives on the ordinary annotation surface
carrying six `UniqueAbilityId`s at once, which the grant path does not read. One
occurrence.

**`ModifiedColor`** (1) carries `color: 2, modificationType: "Set"`. A colour change is a
real game fact, but one occurrence across all 1,412 matches, and the transcript never states a
permanent's colour in the first place — so there is nothing for the change to modify on
the page.

## The four worth narrating

### `ReplacementEffectApplied` + `ReplacementEffect` — 132 occurrences, needs investigating first

The largest gap by a wide margin, and the one that is genuinely unclear.

Payload: `grpid` and `IsDamageReplacement`. The `grpid` resolves through the card
database's `Abilities` table — the sampled one is **185, "Protection from white"** — so the
annotation names the ability that did the replacing.

What is not yet clear is what to *say*. The sampled occurrence sits in the same message as
`DamageDealt` and `CounterRemoved`, so damage was dealt rather than prevented. That rules
out the obvious sentence ("the damage is prevented") and leaves prevention, redirection,
reduction and shield counters indistinguishable without more work.

**This wants its own investigation before its own implementation.** Guessing a verb here
would be exactly the failure the project's own rules are written against.

### `AbilityWordActive` — 40 occurrences

Payload: `AbilityWordName`, `AbilityGrpId`, and a `value`. The sampled one is
`ValueOfX` with `value: 3` — Arena telling us **what X was**.

The transcript currently says a spell was cast and never says what X was chosen as, which
for an X spell is most of what happened. Worth a line.

### `CoinFlip` — 11 occurrences

Payload: `CoinFlipResult`. A coin flip is a game event with a winner and a loser and no
ambiguity about what to say.

### `PermanentRegenerated` — 2 occurrences

Payload is empty, but the annotation's own name is the whole fact: the permanent
regenerated. A creature that should have died and did not is exactly the kind of silence
condition 2 exists to close, and unlike the empty-payload types above, nothing further is
needed to write the sentence.

## Outcome

Eleven types go into CONTRIBUTING as deliberate, with the evidence above.

Four become issues. Condition 2 is met when those four are narrated and the CI guard that
condition 2 asks for is green — the guard being written after the four land, so it starts
life passing rather than needing an allowance list on its first day.
