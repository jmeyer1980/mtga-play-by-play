# Development History, Retrieved from Memory

This document is two things at once: a history of how MTGA Play-by-Play was built, and a
demonstration of *where that history came from*. Nothing below was reconstructed from git
archaeology alone — it was retrieved from the `neurodivergent-memory` MCP server, a
persistent memory graph that every agent session on this machine reads at clock-in and
writes at clock-out. The git log confirms the story; the memory tells it.

Written 2026-08-25 by a Claude session demonstrating the retrieval workflow end to end.

---

## Part 1 — How the retrieval actually went

The memory server organizes memories into **districts** (logical_analysis,
practical_execution, vigilant_monitoring, emotional_processing, creative_synthesis), tags
them with a **project id**, and tracks an **epistemic status** (draft → validated →
outdated). Memories link to each other, forming a walkable graph. This is the exact
sequence of calls this session made, with real results:

### 1. Clock in

```
server_handshake()
→ neurodivergent-memory v0.3.9, transport: stdio
```

### 2. Survey the city

```
memory_stats()
→ 2,129 memories total, 1,359 connections, across ~24 projects
→ this repo: project_id "mtga_play-by" (103 memories)
            plus an older id "mtga-play-by-play" (6 memories)
```

The stats call also exposed a hygiene lesson worth recording: this repo's memories live
under **two** project ids because early sessions typed the id inconsistently. The six
strays under `mtga-play-by-play` cover issues #9 and #11 and are only findable if you
know to look. One id per project, spelled one way, matters.

### 3. Search — and the failure that teaches the method

The first search was run **unscoped**:

```
search_memories(query="MTGA play-by-play parser current state architecture")
→ ERROR: result was 275,182 characters — too large to return
```

Scoped to the project, the same intent returns a ranked, readable set:

```
search_memories(query="current state architecture handoff",
                project_id="mtga_play-by", min_score=0.2)
→ 46 memories, BM25-ranked:
   [1.000] memory_2276  HANDOFF 2026-08-13 late evening
   [0.931] memory_2273  HANDOFF 2026-08-13 evening
   [0.813] memory_2258  HANDOFF 2026-08-11 (v0.2.0, superseded)
   [0.561] memory_2253  ARCHITECTURE — mtga_play-by        ← the anchor
   [0.567] memory_2255  GOTCHAS — mtga_play-by
   [0.390] memory_2351  DIRECTION 2026-08-20 (Jerry's own)
   ... 40 more
```

The lesson generalizes: **always scope by project id first.** The unscoped corpus holds
two thousand memories across two dozen projects; relevance ranking can't save a query
that spans all of them.

### 4. Retrieve the load-bearing memories in full

`retrieve_memory` on the top hits pulled the full text of the architecture state, the
gotchas ledger, the earliest surviving handoff, and the latest one. Their content forms
Part 2 below.

### 5. Walk the graph

```
traverse_from(memory_id="memory_2253", depth=1)
→ 8 connected memories: the v0.2.0 handoff, the gotchas ledger,
  three issue plans (#3, #5, #14, #23), a verified fact about
  ability-text stability, and the embedded-version decision
```

The architecture memory is deliberately the hub: issue plans link back to it, so any
agent that finds *one* thread can walk to the rest.

### 6. Verify against reality before trusting

The latest handoff (memory_2375, written 2026-08-24) says main is at `77fde0f`, dist
stamped `0.5.1`. The actual tree today is at `4bf107f` with dist at `0.5.2+4bf107f3` —
PR [#93](https://github.com/jmeyer1980/mtga-play-by-play/pull/93) merged after that
handoff was written. Memory is a snapshot with a timestamp, not an oracle. The workflow
is: retrieve, then `git log` / check the tree, then reconcile.

---

## Part 2 — The history the memories tell

### Prologue: before the memory record (2026-08-04 → 08-10)

The memory record for this repo begins at v0.2.0. The earliest era — v0.1.0 with the
ten-module pipeline, CI/CD, and the first 142 tests — predates it and survives only in
the repo itself ([docs/superpowers/plans/2026-08-10-mtga-play-by-play.md](superpowers/plans/2026-08-10-mtga-play-by-play.md)
is the original build plan) and in a separate lightweight journal. A memory system only
knows what someone wrote down.

### The architecture, as memory holds it (memory_2253, validated)

> Turns MTG Arena's local Player.log into readable chess-style text transcripts.
> Two-stage pipeline, deliberate: **"capture raw, derive everything else."**
> capture: Player.log → slice by matchId → archive/raw/\<id\>.json.gz (~80 KB/match)
> build: archive → typed event stream → HTML index + per-game pages + markdown.
>
> Raw gzip is the durable source of truth so parser improvements apply retroactively.
> This has paid off repeatedly — every parser fix re-rendered the entire history.
>
> Boundary rule: Core knows GRE JSON shapes; Render sees only GameEvent. A GRE format
> change touches EventExtractor alone. Card names resolve offline from Arena's own
> SQLite card DB. No network, ever.

That one memory is why a cold-started agent doesn't have to rediscover the pipeline
shape, and why "re-render 800+ transcripts after every fix" appears throughout the
history below as a routine act instead of a heroic one.

### Era 1 — v0.2.0 and the handoff chain (2026-08-11 → 08-12)

The earliest surviving handoff (memory_2258) captures v0.2.0: full pipeline live, 175
tests, narration through combat/counters/tokens/scry, the `watch` live server (built on
`TcpListener` because `HttpListener` demands a URL ACL reservation), retention caps, and
a WCAG 2.2 AA markup pass — with the honest caveat, still true for weeks after, that
*no actual screen reader had ever been run*.

Notably, memory_2258 is marked **outdated** and begins: *"SUPERSEDED by memory_2260.
Kept for history."* Handoffs supersede each other in an explicit chain
(2258 → 2260 → 2261 → 2262 …), each pointing forward. Old state stays retrievable
without being mistaken for current — that's the epistemic-status system doing its job.
Memory_2261 even declares the project "DONE" — a claim its own successor overturns the
next day. The record keeps the wrong prediction *and* the correction.

### Era 2 — the issue blitz and the first principles (2026-08-13)

A single day produced five shipped issues (#3, #5, #7 among them), each with a
plan-memory linked to the architecture hub, and — more valuable — **distilled
principles** in logical_analysis. From the sibling pair that anchors the parser's
philosophy:

- *Absence is not a value* (memory_2280, from issue #9): a streamed protocol omitting a
  field is not the same as the field being false.
- *Presence is not commitment* (memory_2288, from issue #11): Arena streams every
  intermediate arrangement — each blocker click, each attack taken back. A narrator
  keyed on first appearance faithfully records abandoned intentions. **Narrate at
  confirmation, never at declaration**; withdrawn intent produces no closing message at
  all. Validated against all 328 archived matches at the time.

The same era produced the **gotchas ledger** (memory_2255, vigilant_monitoring), each
entry a real bug found only on live data: match ids that only exist on 74 of 4,774
lines and must be inherited; `TryGetInt32` throwing instead of returning false; combat
state that must be cleared on turn change because Arena never sends `AttackState_None`;
the Player.log file lock; the watch-rebuild signal; timezone pinning for golden files.

### Era 3 — the sprint (2026-08-17 → 08-19)

Handoffs land hours apart: #14 (Brawl commander), #23 (renamed permanents), #22, #24,
#27, #21, #30, #32, #13, #20, #40 — a dozen shipped issues across three days, each with
its own memory, most linking back to the hub. The guard district earned its keep here
too, with a hard-won operational gotcha (memory_2346): **deleting a base branch
auto-closes any PR stacked on it.**

### Era 4 — direction, not a task (2026-08-20)

Memory_2351 (creative_synthesis, epistemic status *draft*) records Jerry's own fork in
the road, explicitly marked **NOT DECIDED — do not treat as a task**: either fork at
1.0.0 into a GUI app that reads the collection cache the way Arena Tutor does, or add a
tray-service mode to the existing tool. The memory preserves the analysis — option B is
a weekend, option A is a second project resting on an unverified assumption about how
Arena Tutor even obtains collection data — and ends with an instruction to future
agents: *"Do not act on any of this unless Jerry asks."* A memory system that can hold
"here is a considered idea we are deliberately not doing" prevents every future session
from either re-deriving it or accidentally starting it. It also cites the earlier
research memories (2275, 2293) that already settled what the collection is *not* —
Jerry's note when the analysis started to repeat itself: "as you can see, we went
through this exhaustively."

### Era 5 — identity and versioning (2026-08-21 → 08-22)

The exe gained an embedded icon and version stamp (memory_2359, a decision memory:
the stamp is what lets anyone verify *which binary wrote the output* — a recurring
diagnostic since bug reports kept arriving from stale released exes). v0.4.0 shipped
with the scoreboard UI and 668 tests.

### Era 6 — the audit and the confessions (2026-08-23 → 08-24)

The latest handoff (memory_2375) records the largest single session: nine issues closed,
v0.5.0 and v0.5.1 released, 699 tests green — adventure-card resolution, face-down
labeling, the inventory ledger, find-in-page fixes across 1,700+ clipped spans. But its
most valuable content is self-critique, written so the next agent doesn't repeat it:

> 1. I never ran `dotnet format` before pushing, all night. CI caught it on PR #85 —
>    it is the exact check CONTRIBUTING.md calls "the one people forget."
> 2. Copilot found a real bug my own archive diff had already rendered and I had not
>    looked for … my diff was checking LINE LENGTHS, so it never asked about
>    well-formedness. When a change rewrites text, check the text is still well formed,
>    not only that it got shorter.
> 3. I twice invented a failure mode rather than asking … Check the mundane
>    explanation first.

The handoff also carries the rituals that work (release order, rebuild sequence, the
render-and-diff verification pass) and the standing constraint: **the repo is public;
opponent screen names and OS profile paths stay out of every issue, PR, and commit.**

### Today (2026-08-25)

Main is at `4bf107f` — PR #93 (deck-display trimming fix) merged after the last
handoff — dist stamped `0.5.2+4bf107f3`, 699 tests green. The only open work memory
knows of (#65, #1) needs a human driving a screen reader.

---

## Part 3 — Reproducing this

To load this project's context in a fresh session:

```
server_handshake()
search_memories(query="current state architecture handoff",
                project_id="mtga_play-by", min_score=0.2)
retrieve_memory(memory_id="memory_2253")   # architecture hub
retrieve_memory(memory_id="memory_2255")   # gotchas ledger
retrieve_memory(memory_id=<newest HANDOFF from the search>)
traverse_from(memory_id="memory_2253", depth=1)   # walk the hub's spokes
```

Then verify the newest handoff's claims against `git log` and the dist stamp before
acting on them. At session end, write a new HANDOFF into practical_execution with
`project_id="mtga_play-by"`, mark anything it supersedes as outdated, and connect it to
the hub. That contract — clock in, read, verify, work, write, clock out — is the whole
reason this document could be written from memory instead of from scratch.
