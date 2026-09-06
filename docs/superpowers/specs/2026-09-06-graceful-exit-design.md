# Investigation — a graceful exit for a background `watch`

**Date:** 2026-09-06
**For:** issue #213; decides the scope of #214
**Version investigated:** 0.8.0 (`9fa6547`)
**Measured on:** Windows 11 Home 26200 with the default terminal left at "Let Windows
decide", which on this build means Windows Terminal. The release is a self-contained,
single-file `net10.0` console exe (34.8 MB zipped).

## The short version

Give `watch` an opt-in `--tray` flag. The existing console process puts an icon in the
notification area through `Shell_NotifyIcon` (three P/Invoke declarations' worth of
Win32, no Windows Forms, no target-framework change), drops its console window when it
owns one, and offers a two-item native menu: **Open report** and **Quit**. Quit sets the
same stop signal Ctrl+C sets today, so everything after the poll loop — "stopped.", the
server's dispose, exit code 0 — runs unchanged.

Ship first, and separately, a `stop` verb: `mtga-pbp stop` ends a running `watch` from
any terminal or script through a named event. It needs no desktop, tests in CI, and it is
what ends the `watch` that is running hidden on the maintainer's machine right now.

Left click opens the report. Double-click does nothing more than the single click it
contains. Right-click, Shift+F10 or the Apps key open the menu. No window is shown in the
first slice — see Q3 for why the obvious "show the window" is not what it looks like.

Four decisions are asked for at the end; each is a one-line answer.

## What runs today

- `watch` is a console program. It stops on Ctrl+C or when its window is closed. The
  README says the window is deliberate: "the window is the scoreboard, and a `watch`
  started where you cannot see it is a `watch` you cannot tell is running."
- Nothing stops it from being started without a window — a scheduled task without `/it`,
  `Start-Process -WindowStyle Hidden`, or a session like the one that started the copy
  running on the maintainer's machine while this was written (`MainWindowHandle` 0). Such
  a `watch` has no way out but Task Manager or `Stop-Process`, which is issue #213.
- The stop path is one `ManualResetEventSlim` in `Program.Watch`, set from
  `Console.CancelKeyPress`. Everything below the loop is already the graceful shutdown.

## Method

Reasoning about the Windows shell is how #46 shipped a change the screen reader could
not hear. So the questions that could be answered by running something were. A
throwaway spike — a ~330-line `net10.0` console program, P/Invoke only, built with the
repository's own `app.ico` as its icon — was launched two ways: with `Start-Process`,
which gives it a fresh console the way a shortcut, a double-click or a scheduled task
does; and from a shell host, which attaches it to an existing one. It logged what the
console APIs returned and what windows actually existed on the desktop, then added a
notification-area icon, asked the shell where the icon was, and removed it.

| question | measured |
|---|---|
| What `GetConsoleWindow()` returns under the default host | A window of class `PseudoConsoleWindow`, owned by our own process, that nothing on screen corresponds to. The window the user sees is class `CASCADIA_HOSTING_WINDOW_CLASS`, owned by `WindowsTerminal.exe` — a different process. |
| `ShowWindow(SW_HIDE)` on that handle | Returns `TRUE` and the pseudo-window's visible flag flips. The Terminal window stays exactly where it was. |
| `FreeConsole()` | Returns `TRUE`. The Terminal window titled by our `Console.Title` is gone from the desktop within two seconds. |
| `AllocConsole()` afterwards | Returns `TRUE` in both runs, and a new Terminal window appears in both. After rebinding `Console.Out`/`Error`/`In` over `OpenStandard*()`, `Console.Title` and `WriteLine` work in it in both runs. `WindowWidth` reported 120×30 only in the `Start-Process` run; in the shell-host run — a process that had no console when it started — it threw `IOException` after the rebind. |
| `GetConsoleProcessList()` | **1** when launched by `Start-Process` — we own the console. **3** when launched from a shell host. |
| `Shell_NotifyIcon(NIM_ADD)` from a console process, icon taken from the exe's own resources with `ExtractIconEx` | `TRUE`; two icon sizes extracted. `NIM_SETVERSION` to `NOTIFYICON_VERSION_4`: `TRUE`. A balloon (`NIF_INFO`): `TRUE`. |
| `Shell_NotifyIconGetRect` | `S_OK`, with a 48×72 rectangle at the bottom-right of the display — the icon exists in the notification area, not merely in our process's opinion. |
| `NIM_DELETE` at quit | `TRUE`. No ghost icon. |
| Any of it with no console at all | The shell-host run had `GetConsoleWindow() == 0` and `Console.WindowWidth` threw. The icon was added and removed identically. |

Not measured, because it needs a person at the desk: the click and keyboard path
(`NIN_SELECT`, `NIN_KEYSELECT`, `WM_CONTEXTMENU` → `TrackPopupMenuEx`), `TaskbarCreated`
after an Explorer restart, `WM_QUERYENDSESSION` at logoff, and what closing a
re-allocated window does to the process. The first of these is wired in the spike and
compiled, and is the listening test the implementation PR has to include, as #1 did for
the pages.

One oddity for the record: the spike's hidden window read its own title back garbled.
It is invisible, so nothing depends on it, but it is a reminder that the implementation
should declare its interop with `LibraryImport` and `StringMarshalling.Utf16` rather
than `DllImport` guesses, so that every string is right by construction.

## The five questions

### Q1 — Viable patterns for a background .NET console app's exit

**A. A notification-area icon owned by the console process, via `Shell_NotifyIcon`.
Recommended.** A hidden top-level window on its own thread runs a message loop; the icon
posts to it; a native popup menu handles the rest. Costs about the size of the spike.
Nothing new ships: the exe already carries the icon (added for the taskbar in #66), and
`user32`, `shell32` and `kernel32` are on every Windows machine.

**B. The same icon through Windows Forms `NotifyIcon`. Rejected.** It needs
`net10.0-windows` and `UseWindowsForms`, which the test project would then have to adopt
too because a `net10.0` project cannot reference a `net10.0-windows` one; it adds the
desktop framework to a self-contained single-file publish; and it brings a UI framework,
an STA thread and `Application.Run` for one icon and two menu items. The spike shows the
plain route works.

**C. A `stop` verb over a named event. Recommended, and first.** `watch` creates
`Local\mtga-pbp-stop-<port>` and waits on it beside the interval it already waits on;
`mtga-pbp stop [port]` opens the same name and sets it, or says "no watch is running on
8787" when `EventWaitHandle.TryOpenExisting` finds nothing. Scriptable, works on a
`watch` with no window at all, and — unlike everything else in this document — it can be
tested on a CI runner with no desktop session. The tray's Quit calls the same routine, so
the tested path is the shipped path.

**D. Hide the console window on demand and show it again later. Rejected on evidence.**
Under Windows Terminal, `ShowWindow` on `GetConsoleWindow()` moves nothing the user can
see (table above). Every "minimise to tray" recipe built on that pair is silently broken
on a default Windows 11.

**E. A Windows service. Rejected.** Services cannot open a browser in the user's session,
run outside the profile whose `Player.log` this reads, and need elevation to install —
which is the reason the README lists the Startup folder ahead of `schtasks`.

**F. A native or full GUI shell. Rejected**, as the issue itself prefers. The report is
the GUI. It already has the stars, the search, the records and the coach's verdict;
duplicating any of it in a window is a second answer to the same question, which is the
drift #201's "one renderer, two mounts" rule exists to prevent.

### Q2 — Double-click

**No distinct double-click behaviour.** With `NOTIFYICON_VERSION_4` the shell delivers
`NIN_SELECT` on the first click and `WM_LBUTTONDBLCLK` only after it, so whatever a
double-click did would happen on top of what the single click already did. Windows 11's
own icons use left-click for the primary surface and right-click for the menu, and a
gesture only mouse users can make is not where a feature should live. The single click's
action is opening the report; a second click opens it again, which is harmless.

### Q3 — Can a hidden console process be re-surfaced?

Not the same window: **no** under Windows Terminal, measured. A fresh one: **yes** —
`FreeConsole()` closes the window the process was started in, `AllocConsole()` opens a
new one later, and after rebinding the standard streams `LiveBoard` could draw into it —
in the shortcut case. In the shell-host run `Console.WindowWidth` threw after the rebind,
and `LiveBoard` falls back to plain appended lines on exactly that exception, so a replay
there would scroll rather than pin. That costs a replay (the board's `Say` lines would
need keeping in a small ring so the new window is not blank above the block), a second
measurement of the width question, and leaves one more thing unmeasured: what closing
that new window does. `CTRL_CLOSE_EVENT` normally ends the process, and whether a handler
that calls `FreeConsole()` and returns can keep it alive is exactly the kind of fact this
document refuses to assert. So:

- **Slice 1 shows no window.** Open report and Quit are the whole surface.
- **"Show window" is slice 2, if wanted**, gated on that one measurement.

When to drop the console is decided by `GetConsoleProcessList`. A count of one means the
process owns it — a shortcut, a double-click, the Startup folder, a scheduled task — and
the window closes the moment the banner and the `serving` line have been printed. A
larger count means a shell is attached: keep it, so Ctrl+C still works and the prompt is
held exactly as `watch` holds it today, with the icon added as well. Anyone typing
`watch --tray` into a terminal gets the terminal they were in.

### Q4 — Minimum UI

A native menu and a tooltip; nothing drawn by us.

| gesture | message (v4) | action |
|---|---|---|
| left click; Enter or Space on the icon after Win+B | `NIN_SELECT`, `NIN_KEYSELECT` | open the report in the browser |
| double-click | `WM_LBUTTONDBLCLK` | nothing beyond the click it contains |
| right click; Shift+F10; Apps key | `WM_CONTEXTMENU` | menu: **Open report**, **Quit** |
| pointer rests on the icon; focus reaches it | tooltip | the board's first line: `mtga-pbp — watching · 3-1 tonight · updated 21:14` |
| Quit | `WM_COMMAND` | `stop.Set()`; the loop's tail runs as it does after Ctrl+C |
| logoff or shutdown | `WM_QUERYENDSESSION` | the same |
| Explorer restarts | `TaskbarCreated` | re-add the icon |

The tooltip is the scoreboard's one-line form, refreshed with `NIM_MODIFY` on every
repaint, and it is also what a screen reader announces at the icon. A native Win32 menu is
read by screen readers with no work on our side, and Win+B reaches the notification area
by keyboard on every Windows since 7 — which is the same standard the pages are held to,
and like the pages it is not claimed until someone has listened to it.

One balloon at start, only when the process owns its console and is about to close it:
"Watching. The report is at http://127.0.0.1:8787/ — right-click this icon to quit." It
answers the README's objection to a `watch` you cannot see: the icon is how you can tell
it is running.

Windows 11 puts a new icon in the overflow behind the ^ chevron until the user drags it
out or turns it on under Settings › Personalization › Taskbar › Other system tray icons.
That is Windows policy, not ours; the README says where the icon went, and the balloon
is the pointer.

### Q5 — Packaging and runtime

- **Same exe, same zip, same target framework.** The interop is declared with
  `[SupportedOSPlatform("windows")]` and every call sits behind `OperatingSystem.IsWindows()`
  so the `net10.0` build stays warning-free; the tool is Windows-only in practice (Arena's
  log lives in a Windows profile) and nothing here changes that.
- **Normal run vs tray run.** `watch` is unchanged. `watch --tray` is a flag on the verb
  rather than a verb of its own or a config key: the 1.0 surface audit freezes verbs and
  keys, and a flag is the smallest thing to add. The Startup-folder shortcut and the
  `schtasks` line gain ` --tray` at the end; `/it` stops mattering, because the window is
  no longer where the state lives.
- **Startup.** Banner, `watching`, `serving` — printed as today, then `FreeConsole()` if
  the process owns the console. A port already in use exits 2 with a message today; with
  no console that message would vanish, so in tray mode it becomes a `MessageBox`: "could
  not listen on port 8787 — is another watch running? Quit it from its icon, or run
  `mtga-pbp stop`." A Startup-folder copy plus a hand-started one is the common way to hit
  this.
- **Shutdown.** Every way out — Quit, `stop`, Ctrl+C in the shell case, logoff — sets the
  one stop signal. The icon is removed in a `finally` so a crash cannot leave a ghost; the
  message-loop thread is a background thread so it can never hold the process open. A
  Quit that lands during a rebuild waits for the rebuild gate, which at the current
  archive is seconds; the tooltip can say "quitting…" meanwhile.
- **Logging.** There is no log file today and this does not add one. `watch --tray > log.txt`
  keeps working: a redirected stream is not a console handle, `LiveBoard` already degrades
  to plain lines, and the attached-process count keeps the console (the shell that did the
  redirection is attached), so nothing is lost.
- **Errors while in the tray.** The poll loop never exits on error (#132). Today it says
  so on the board once per kind of failure; with no console the tooltip's `updated HH:MM`
  is what shows a stalled watch, and the same once-per-kind rule can raise a balloon.

## Risks

1. **The click path is unmeasured.** Everything from the icon to the menu is standard
   Win32, but standard is what #46 was too. The implementation PR includes a checklist —
   mouse, Win+B keyboard, NVDA — filled in by a person before it is marked ready.
2. **Tooltip length.** `szTip` is 128 characters; the composer clips like `Scoreboard.Clip`
   and is unit-tested at the boundary.
3. **Interop correctness.** Use `LibraryImport` (source-generated, .NET 7+) with explicit
   `W` entry points and `StringMarshalling.Utf16`; the spike's garbled window title is
   what a `DllImport` guess looks like.
4. **Two watches.** Handled by the `MessageBox` above; the second instance exits 2 as it
   does now.
5. **SmartScreen and signing.** Unchanged: no new binary, no new prompt.
6. **CI cannot see a notification area.** The interop shell is thin and stays out of the
   unit tests; the event mapping, the tooltip composer and the stop signal are the tested
   parts. The release smoke run already proves the exe starts.

## Implementation plan for #214

Three PR-sized slices, in this order. Each stands alone.

**Slice 0 — `mtga-pbp stop`.** A `StopSignal` that wraps the named event and the interval
wait; `Watch` waits on it instead of the bare `ManualResetEventSlim`; a `stop [port]`
verb; the "no watch is running" answer. Tests: set the event, watch the wait return; open
a name that does not exist; the verb's exit codes. README: the `stop` line in the command
table and one sentence in SUPPORT under "a `watch` you cannot see". *This slice alone ends
the hidden watch from today.*

**Slice 1 — `watch --tray`.** A `Tray` class: hidden window, message loop on a background
thread, icon from the exe's own resources, `NIM_ADD`/`SETVERSION`/`MODIFY`/`DELETE`,
`TaskbarCreated`, `WM_QUERYENDSESSION`, the menu, the balloon. The console-ownership rule
and the `FreeConsole()` after the `serving` line. The `MessageBox` for a port refusal. The
tooltip composed from the board's first line on each repaint. Tests: the event-to-action
mapping (`NIN_SELECT` → open, `WM_COMMAND` Quit → stop, double-click → nothing) against a
recording shell interface; the tooltip composer at 128 characters; the ownership rule
against a fake process count. Docs: a "Live mode in the notification area" section, the
shortcut and `schtasks` lines with `--tray`, the overflow note. Minor version (a new
capability by CONTRIBUTING's table).

**Slice 2 — optional, after 1 has been lived with.** "Show window" via `AllocConsole()`
and a board replay, gated on measuring `CTRL_CLOSE_EVENT` first; and a `QuitWithArena`
config key (default false) that ends the watch when `MTGA.exe` is gone, which was the
other half of the original tray-mode idea.

## Decisions requested

1. `watch --tray`, flag only, default off — or a config key beside it?
2. Left click opens the report; double-click adds nothing; the menu is Open report and
   Quit. Yes, or change what?
3. `mtga-pbp stop` ships as slice 0, before the icon. Yes, or fold it into slice 1?
4. "Show window" is deferred to slice 2. Yes, or wanted in the first cut?
