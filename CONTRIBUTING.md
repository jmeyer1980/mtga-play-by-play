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
match you have ever played rather than only to new ones.

`build` skips matches nothing has happened to, using `out/.build-cache.json`. That cache
is keyed on the build's own version, so **a parser fix still reaches every match**: your
change produces a different `BuildInfo.Version`, the cache is discarded, and the next
build is a full one. There is no constant to remember to bump. If you are ever unsure,
`mtga-pbp build --rebuild` ignores the cache entirely. **If you are tempted to fix
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

## Cutting a release

The release workflow fires on any tag matching `v*` and takes the version from the tag
itself. Nothing else triggers it.

```powershell
# 1. bump the version a working-copy build reports
#    Directory.Build.props:  <Version>0.5.1</Version>
git commit -am "chore: 0.5.1"
git push origin main

# 2. tag that commit and push the tag
git tag -a v0.5.1 -m "v0.5.1"
git push origin v0.5.1
```

Both halves matter. The workflow overrides the version with `-p:Version` from the tag, so
skipping the bump still produces a correct release — but every build from a working copy
then goes on reporting the old number, and the whole point of the stamp is that the
report and the repository agree about which build wrote it.

Release notes are generated from the merged pull request titles, so a good PR title is
the release note. There is no hand-maintained changelog and there should not be: it would
be the same list, kept by hand, drifting.

### Which number to bump

| Change | Bump |
| --- | --- |
| A new user-visible capability — a panel, a command, a new file in the archive | **minor** (0.5.1 → 0.6.0) |
| Output wording, bug fixes, performance — anything that changes what is *said* about the same data | **patch** (0.5.1 → 0.5.2) |
| A breaking change to config keys, the archive layout, or CLI arguments | **minor** while pre-1.0; **major** after |

Most parser work is a patch even when it moves hundreds of lines: rewording 96 resolutions
across 74 transcripts changes what the tool says about data it already had, and nothing a
reader does differently. Adding the vault panel changed what the tool can do, and was a
minor by this table — it shipped as 0.5.1 anyway, which is the mistake this table exists
to stop repeating.

**The `+hash` is not part of the version.** `0.5.1+5becf5ea` is semver build metadata —
the commit the build came from — and it is ignored when versions are compared. Two builds
of `0.5.1` from different commits are the same version wearing different stamps.

### After the release, if you run `watch`

Rebuilding `dist/` while `watch` is running fails: Windows locks a running executable.

```powershell
Get-Process mtga-pbp | Stop-Process
dotnet publish src/MtgaPbp.Cli -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
Start-Process dist/mtga-pbp.exe watch
```

The port answers immediately, serving the previous run's report; the page refreshes
itself once the startup capture and build finish, which at a large archive can take
half a minute. A genuinely first run has no previous report — the port answers 404
until the first build lands, and the browser is opened only then, so a 404 in that
window is the expected state and not a regression. (`watch` used to bind only after that build, and the refused connection
in the gap was mistaken for a crash more than once — that is why this sentence exists.)
`dist/` is gitignored and local; the released zip is built by CI from the tag, not from
whatever is in your working copy.

## Things deliberately not done

Please check before building these; each was investigated and dropped with evidence, and
the reasoning is in the commit history:

- `PowerToughnessModCreated` — 754 of 903 arrive with a counter already reported
- Designations, `Attachment`, `LossOfGame`, `AbilityExhausted`, `TemporaryPermanent`,
  `ModifiedType`
- `TimerStateMessage` think-time — unreported on a third of turns, and about half the
  wall clock when present, so the transcript reports wall clock instead

`AddAbility` is mined as of issue #5: grants render as "Enter the Avatar State gives
Llanowar Elves 2/2 first strike", via the card database's `Abilities` table. Grants that
ride on a counter or restate a Class level line are deliberately left to those lines.

Grant wear-offs render as of issue #7 — "Battlesong Berserker loses menace" — but they
are *not* read from the annotation disappearing. That surface is sampled: across the
archive an AddAbility id goes missing and returns under the same id 115 times, up to 86
messages later, with the creature in play the whole while. The wear-off is read from the
object's own `uniqueAbilities` losing a granted grpid, the same channel statline
wear-offs use, and only for a permanent still on the battlefield — a creature that died
did not "lose trample".

## Reporting a bug

Use the issue template — it asks for the build stamp, which is the single most useful
line. Every page, every markdown export and the `watch` banner carry
`Written by mtga-pbp <version>+<commit>`.

This matters more than it sounds. A `watch` left running from an older build keeps
rewriting the whole report with that build's code, so the output on disk and the current
source disagree with nothing on screen to explain why. That has cost a full morning,
twice. Check the stamp first.
