# Getting help

Three things settle almost every report. Please try them before opening an issue — not to
keep you out, but because each one has been the answer more than once.

## 1. Check the build stamp

Every report page and markdown export ends with `Written by mtga-pbp <version>+<commit>`,
and `watch` prints it on startup.

A `watch` left running from an older copy keeps rewriting the whole report with *that*
copy's code, so the files on disk can be stale even though you downloaded a newer version
and nothing on screen says so. That has been the answer twice, and cost a full morning
each time.

If the stamp is not the version you expect, close `watch`, start it again from the new
copy, and look at the output afresh.

## 2. Nothing came out at all

In order of how often it is the cause:

- **Detailed Logs are off.** Arena → Settings → Account → *Detailed Logs (Plugin
  Support)*. Without it the log contains nothing to transcribe, and the tool has nothing
  to fail on — it just finds no matches. Turning it on affects games played *after* it is
  on; it cannot recover matches already played.
- **Arena is installed somewhere unusual.** The tool says so plainly — `error: no Arena
  log found` followed by the paths it looked in — and `mtga-pbp.json` takes a `LogPaths`
  list if yours are elsewhere.
- **The window closed before you could read it.** Double-clicking the exe runs a single
  build and exits. The released zip ships a `mtga-pbp.json` with `"OpenAfterBuild": true`
  so the report opens by itself; if you are running your own build, either add that
  setting, run it from a terminal, or use `watch`, which stays open.

Arena does not need to be running to build a report, and the tool makes no network
requests. If a match you played is missing, it is a log question rather than a
connectivity one.

## 3. The vault panel is empty or shows no history

Expected, and it cannot be fixed retroactively. Arena writes your gold, gems, vault and
wildcard totals into the log, but the tool discarded them at capture time for most of its
life and the logs holding the old ones are gone. The ledger starts the first time you
capture with a version that records it, and the change column appears once there are two
readings to compare.

It also cannot tell you which cards you own or crafted. Arena removed the API that listed
them in 2021 and the field that would carry the deltas is present and empty in every
snapshot. The log can prove thirteen uncommon wildcards were spent and name none of them.

## Still stuck, or found something wrong

- **The transcript says something that did not happen** — this is the most valuable kind
  of report. [Open a bug report](https://github.com/jmeyer1980/mtga-play-by-play/issues/new?template=bug_report.yml).
  It asks for the build stamp and the lines; `mtga-pbp stats` output helps when something
  is missing entirely.
- **A detail is missing rather than wrong** — there is a
  [separate template](https://github.com/jmeyer1980/mtga-play-by-play/issues/new?template=missing_detail.yml)
  for that.
- **A question, or something that fits neither** — a blank issue is fine.
- **A security or privacy problem** — please report it
  [privately](https://github.com/jmeyer1980/mtga-play-by-play/security/advisories/new)
  rather than in a public issue. See [SECURITY.md](SECURITY.md).
- **You use a screen reader** — findings on
  [issue #1](https://github.com/jmeyer1980/mtga-play-by-play/issues/1) are especially
  welcome. Heading navigation and list semantics are confirmed with NVDA and Narrator;
  most other controls are not.

**Whatever you paste, scrub it first.** Arena logs carry both players' real screen names,
session ids, and paths like `C:\Users\yourname\`. This is a public repository.

## Not what you wanted?

If you want a live overlay that tracks while you play, [Arena
Tutor](https://draftsim.com/arenatutor/) is easier to install and does that well. This
tool is for static files you own — an offline archive, one page per game, nothing running
while you play. No hard feelings.
