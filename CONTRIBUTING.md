# Contributing

Thanks for looking. Bug reports, parser fixes and new annotation coverage are all
welcome, and so is telling me the output said something that did not happen — that is
the failure mode that matters most here.

## The one rule that is not negotiable

**Never commit a real screen name, a real user id, or a Windows profile path.**

This repository is public and has already had one privacy incident: an opponent's Arena
handle reached a committed fixture and the history had to be rewritten across five
commits to remove it. Log files are full of both players' names, session ids and paths
like `C:\Users\yourname\`.

If you add a fixture, scrub it and add an assertion that it stays scrubbed. There is an
existing example in `MultiGameTests.The_bo3_fixture_carries_no_trace_of_either_player` —
it checks the fixture rather than trusting whoever made it, because a fixture is exactly
the kind of file someone regenerates later without knowing the rule.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). MTG Arena
does **not** need to be installed to build or test — only to run.

Run everything from the repository root, the folder holding `MtgaPbp.slnx`:

```powershell
dotnet build
dotnet test
dotnet format --verify-no-changes
```

CI runs all three plus a dependency vulnerability audit, and builds with warnings as
errors. `dotnet format` is the one people forget; run it before pushing.

## How the pipeline is shaped

Capture and rendering are deliberately separate:

```
Player.log  →  archive/raw/<matchId>.json.gz  →  out/*.html, out/text/*.md
```

The archive is the source of truth and is never edited. Everything downstream is
re-derived from it by `mtga-pbp build`, which is what makes a parser fix apply to every
match you have ever played rather than only to new ones. **If you are tempted to fix
something by editing rendered output or archived JSON, the fix belongs in the extractor
or the narrator instead.**

## Claims about the log need evidence

This is the project's most expensive recurring mistake, made six times before it was
written down: concluding "Arena does not report X" from a search that could not have
found X.

Before stating that something is absent, **count it**. The archive is line-delimited
JSON inside gzip and takes about ten lines of Python to walk. Two real examples:

- A truncation marker was reported as never occurring. It is not JSON, so the slicer had
  never archived it — searching the archive for it was blind by construction. It was in
  `Player-prev.log` twice.
- Spell targets were believed to be unsent for months. They were in `persistentAnnotations`,
  an array the extractor did not read.

If a comment or a test says the log lacks something, it should say how many times that
was checked and against what.

## Regenerating the checked-in fixtures

The end-to-end tests run against a small card-name fixture rather than Arena's 237 MB
database, so they work on CI. If you change the sample match, regenerate it on a machine
with Arena installed:

```powershell
dotnet test --filter "FullyQualifiedName~CardNameFixtureGenerator" -- NUnit.Explicit=true
```

The golden markdown file regenerates by deleting
`tests/MtgaPbp.Tests/Fixtures/sample-match.expected.md` and running the tests twice: the
first run writes it and fails on purpose so you have to look at it, the second passes.
**Read the diff.** A golden file that is regenerated without being read stops being a
test.

## Pull requests

- One concern per pull request.
- Add a test that fails without your change. For a parser fix, the useful test is
  usually a small hand-built `gameStateMessage` rather than a whole match.
- Explain *why* in the commit message, not just what. If you dropped an approach, say
  what the evidence was — the next person will otherwise re-investigate it.
- If behaviour changed, say how many lines it moves across a real archive. "This
  rephrases 153 lines and removes one" is a much better review than "improves wording".

CI must be green. There is no separate review gate beyond that and my own reading.

## Things deliberately not done

Please check before building these; each was investigated and dropped with evidence, and
the reasoning is in the commit history:

- `PowerToughnessModCreated` — 754 of 903 arrive with a counter already reported
- Designations, `Attachment`, `LossOfGame`, `AbilityExhausted`, `TemporaryPermanent`,
  `ModifiedType`
- `TimerStateMessage` think-time — unreported on a third of turns, and about half the
  wall clock when present, so the transcript reports wall clock instead

Still unmined and genuinely open: `AddAbility`, which appears 461 times and is visible in
`mtga-pbp stats`.

## Reporting a bug

Use the issue template — it asks for the build stamp, which is the single most useful
line. Every page, every markdown export and the `watch` banner carry
`Written by mtga-pbp <version>+<commit>`.

This matters more than it sounds. A `watch` left running from an older build keeps
rewriting the whole report with that build's code, so the output on disk and the current
source disagree with nothing on screen to explain why. That has cost a full morning,
twice. Check the stamp first.
