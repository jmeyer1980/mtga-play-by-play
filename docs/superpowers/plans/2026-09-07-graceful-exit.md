# Graceful exit for a background `watch` — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `watch` that runs where nobody can see it can be ended without Task Manager — first by a `stop` verb from any terminal, then by a Quit item on a notification-area icon.

**Architecture:** One `StopSignal` (a named kernel event, `Local\mtga-pbp-stop-<port>`) replaces the bare `ManualResetEventSlim` the watch loop waits on; Ctrl+C, `mtga-pbp stop` and the icon's Quit all set it, so the shutdown tail after the loop is untouched. The icon is owned by the existing console process through `Shell_NotifyIcon` on a hidden window with its own message-loop thread; the message-to-action mapping and the tooltip composer are pure functions with tests, and the Win32 shell around them is thin and checked by hand.

**Tech Stack:** .NET 10, NUnit 4, P/Invoke into `kernel32`/`user32`/`shell32` (`DllImport`, explicit `W` entry points). No new packages, no Windows Forms, no target-framework change.

**Spec:** [`docs/superpowers/specs/2026-09-06-graceful-exit-design.md`](../specs/2026-09-06-graceful-exit-design.md), with the maintainer's answers on issue #213: `--tray` as a flag only; left click opens the report and double-click adds nothing; `stop` ships first; "Show window" waits for a later slice.

## Global Constraints

- Every `dotnet` command runs from the repository root. `dotnet test` must stay green; run `dotnet format --verify-no-changes` before each commit.
- Source files are CRLF and UTF-8 (check with `grep -c $'\r' file` equal to `wc -l < file`). Never commit the OS username, a real Arena screen name, or an absolute profile path; grep the staged diff for the OS username and for `C:\Users` before every commit.
- The Cli project stays `net10.0`. Windows-only code is marked `[SupportedOSPlatform("windows")]` and called only inside `OperatingSystem.IsWindows()`.
- Tests are NUnit, `Assert.That(...)` style, named `Like_a_sentence_with_underscores`, with a `<summary>` on the class saying why the tests exist.
- **Never run `mtga-pbp stop` with no port, or on 8787, from a test or a manual check** — the maintainer's real `watch` listens there.
- Manual runs of the debug build use a scratch config beside the built exe so the real archive and `out/` are never touched (see Task 3, step 6). Delete it afterwards.
- Versioning by CONTRIBUTING's table: the `stop` verb is a new capability → **0.9.0**; `--tray` is another → **0.10.0**.
- Slice 0 is Tasks 1–5 and ships as its own PR and release. Slice 1 is Tasks 6–13.

## File structure

Slice 0
- Create `src/MtgaPbp.Cli/StopSignal.cs` — the named event: `Listen`, `Fire`, `IsListening`, `Set`, `Wait`.
- Create `src/MtgaPbp.Cli/StopCommand.cs` — the `stop` verb's logic with writers and patience as parameters.
- Modify `src/MtgaPbp.Cli/Program.cs` — command list, dispatch, usage, and `Watch` waiting on the signal.
- Create `tests/MtgaPbp.Tests/StopSignalTests.cs`, `tests/MtgaPbp.Tests/StopCommandTests.cs`.
- Modify `README.md`, `SUPPORT.md`.

Slice 1
- Create `src/MtgaPbp.Cli/Tray/TrayEvents.cs` — message → `TrayAction`, pure.
- Create `src/MtgaPbp.Cli/Tray/TrayTip.cs` — the tooltip line, pure.
- Create `src/MtgaPbp.Cli/Tray/NativeMethods.cs` — every Win32 declaration, nothing else.
- Create `src/MtgaPbp.Cli/Tray/ConsoleOwnership.cs` — the attached-process rule and `Detach`.
- Create `src/MtgaPbp.Cli/Tray/TrayIcon.cs` — hidden window, message loop, icon, menu.
- Modify `src/MtgaPbp.Render/Scoreboard.cs` — the footer's stop hint becomes a parameter.
- Modify `src/MtgaPbp.Cli/Program.cs` — `--tray`, the port-refusal box, detach, tooltip refresh.
- Create `tests/MtgaPbp.Tests/TrayEventsTests.cs`, `TrayTipTests.cs`, `ConsoleOwnershipTests.cs`; modify `ScoreboardTests.cs`.
- Modify `README.md`.

---

## Slice 0 — `mtga-pbp stop`

### Task 1: `StopSignal`

**Files:**
- Create: `src/MtgaPbp.Cli/StopSignal.cs`
- Test: `tests/MtgaPbp.Tests/StopSignalTests.cs`

**Interfaces:**
- Produces: `public sealed class StopSignal : IDisposable` with `static StopSignal Listen(int port)`, `static bool Fire(int port)`, `static bool IsListening(int port)`, `static string NameFor(int port)`, `bool IsNamed`, `void Set()`, `bool Wait(TimeSpan timeout)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The named event that lets <c>mtga-pbp stop</c> end a <c>watch</c> it cannot see.
/// </summary>
/// <remarks>
/// Windows-only by nature: named events are a kernel feature the other platforms lack,
/// and the tool only ever runs where Arena does. The port in each test is only a name;
/// a random one keeps parallel tests out of each other's way, and none of them is 8787,
/// where a real watch may be listening on the machine running the suite.
/// </remarks>
[Platform("Win")]
public class StopSignalTests
{
    private static int FreshPort() => Random.Shared.Next(40000, 60000);

    [Test]
    public void Nothing_listening_means_nothing_to_fire()
    {
        var port = FreshPort();
        Assert.That(StopSignal.IsListening(port), Is.False);
        Assert.That(StopSignal.Fire(port), Is.False);
    }

    [Test]
    public void Fire_from_outside_ends_the_wait()
    {
        var port = FreshPort();
        using var listener = StopSignal.Listen(port);
        Assert.That(listener.IsNamed, Is.True);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.False, "nothing has fired yet");

        Assert.That(StopSignal.Fire(port), Is.True);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True);
    }

    [Test]
    public void Set_from_inside_ends_the_wait_too()
    {
        // The Ctrl+C path, and later the icon's Quit.
        using var listener = StopSignal.Listen(FreshPort());
        listener.Set();
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True);
    }

    [Test]
    public void The_name_is_per_port()
    {
        int a = FreshPort(), b = a + 1;
        using var listener = StopSignal.Listen(a);
        Assert.That(StopSignal.Fire(b), Is.False);
        Assert.That(listener.Wait(TimeSpan.Zero), Is.False);
        Assert.That(StopSignal.Fire(a), Is.True);
    }

    [Test]
    public void Listening_lasts_exactly_as_long_as_the_listener()
    {
        var port = FreshPort();
        Assert.That(StopSignal.IsListening(port), Is.False);
        var listener = StopSignal.Listen(port);
        Assert.That(StopSignal.IsListening(port), Is.True);
        listener.Dispose();
        Assert.That(StopSignal.IsListening(port), Is.False);
    }

    [Test]
    public void The_name_says_what_it_is_for()
    {
        Assert.That(StopSignal.NameFor(8787), Is.EqualTo(@"Local\mtga-pbp-stop-8787"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StopSignalTests" 2>&1 | tail -5`
Expected: a compile error — `StopSignal` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Diagnostics.CodeAnalysis;

namespace MtgaPbp.Cli;

/// <summary>
/// The one signal that ends a <c>watch</c>: Ctrl+C sets it, the icon's Quit sets it,
/// and <c>mtga-pbp stop</c> sets it from another process.
/// </summary>
/// <remarks>
/// A named event rather than a request to the loopback server: the server's Host and
/// Origin checks exist to keep hostile pages out (#116), and a stop endpoint would be one
/// more thing they had to be right about. A kernel object in the <c>Local\</c> namespace
/// is visible only inside this logon session, needs no port of its own, and disappears
/// with the process that created it — so "is a watch running on 8787" is answered by
/// whether the name still opens (#213).
/// <para>
/// Named events are a Windows feature. Elsewhere the signal still serves the in-process
/// callers; <c>stop</c> simply finds nothing, which is what it says.
/// </para>
/// </remarks>
public sealed class StopSignal : IDisposable
{
    private const string Prefix = @"Local\mtga-pbp-stop-";

    private readonly EventWaitHandle _handle;

    private StopSignal(EventWaitHandle handle, bool named)
    {
        _handle = handle;
        IsNamed = named;
    }

    /// <summary>Whether another process can find this signal by port.</summary>
    public bool IsNamed { get; }

    /// <summary>The kernel object a watch on <paramref name="port"/> listens under.</summary>
    public static string NameFor(int port) => Prefix + port;

    /// <summary>Creates the signal a watch on <paramref name="port"/> waits on.</summary>
    public static StopSignal Listen(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new StopSignal(
                    new EventWaitHandle(false, EventResetMode.ManualReset, NameFor(port)),
                    named: true);
            }
            catch (PlatformNotSupportedException) { /* the local one below still works */ }
        }
        return new StopSignal(new EventWaitHandle(false, EventResetMode.ManualReset), named: false);
    }

    /// <summary>
    /// Sets the signal of a watch on <paramref name="port"/>. False when nothing listens
    /// there — which, without named events, is always.
    /// </summary>
    public static bool Fire(int port)
    {
        if (!TryOpen(port, out var handle)) return false;
        using (handle) return handle.Set();
    }

    /// <summary>Whether a watch on <paramref name="port"/> is still holding its signal.</summary>
    public static bool IsListening(int port)
    {
        if (!TryOpen(port, out var handle)) return false;
        handle.Dispose();
        return true;
    }

    private static bool TryOpen(int port, [NotNullWhen(true)] out EventWaitHandle? handle)
    {
        handle = null;
        if (!OperatingSystem.IsWindows()) return false;
        try { return EventWaitHandle.TryOpenExisting(NameFor(port), out handle); }
        catch (PlatformNotSupportedException) { return false; }
    }

    public void Set() => _handle.Set();

    /// <summary>True once set; false when <paramref name="timeout"/> passes first.</summary>
    public bool Wait(TimeSpan timeout) => _handle.WaitOne(timeout);

    public void Dispose() => _handle.Dispose();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~StopSignalTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 5: Format, check line endings, commit**

```bash
dotnet format --verify-no-changes
grep -c $'\r' src/MtgaPbp.Cli/StopSignal.cs; wc -l < src/MtgaPbp.Cli/StopSignal.cs   # equal
git add src/MtgaPbp.Cli/StopSignal.cs tests/MtgaPbp.Tests/StopSignalTests.cs
git commit -m "A named event any process can set ends a watch"
```

### Task 2: `StopCommand` and the `stop` verb

**Files:**
- Create: `src/MtgaPbp.Cli/StopCommand.cs`
- Modify: `src/MtgaPbp.Cli/Program.cs` (the `Commands` array near line 63, the dispatch `switch` near line 43, the banner condition near line 38, `Usage()` near line 100)
- Test: `tests/MtgaPbp.Tests/StopCommandTests.cs`

**Interfaces:**
- Consumes: `StopSignal.Fire(int)`, `StopSignal.IsListening(int)`, `StopSignal.Listen(int)` from Task 1.
- Produces: `public static class StopCommand` with `const int DefaultPort = 8787` and `static int Run(string? portArg, TimeSpan patience, TextWriter stdout, TextWriter stderr)`. Exit codes: 0 delivered, 1 no watch on that port, 2 bad port argument.

- [ ] **Step 1: Write the failing tests**

```csharp
using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The four answers <c>mtga-pbp stop</c> can give, each as a test rather than a
/// manual run.
/// </summary>
/// <remarks>
/// A listener created here stands in for a running watch. None of these touches the
/// default port: a real watch may be on it wherever the suite runs.
/// </remarks>
[Platform("Win")]
public class StopCommandTests
{
    private static int FreshPort() => Random.Shared.Next(40000, 60000);

    private static (int Code, string Out, string Err) Run(string? portArg, TimeSpan? patience = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = StopCommand.Run(portArg, patience ?? TimeSpan.FromSeconds(5), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Test]
    public void A_port_that_is_not_a_number_is_a_usage_error()
    {
        var (code, _, err) = Run("eight");
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("usage: mtga-pbp stop [port]"));
    }

    [Test]
    public void No_watch_on_the_port_says_so()
    {
        var port = FreshPort();
        var (code, _, err) = Run(port.ToString());
        Assert.That(code, Is.EqualTo(1));
        Assert.That(err, Does.Contain($"no watch is running on port {port}"));
    }

    [Test]
    public void A_watch_that_goes_away_is_reported_stopped()
    {
        var port = FreshPort();
        var listener = StopSignal.Listen(port);
        // The watch: once fired, it finishes its poll and exits.
        var watch = Task.Run(() =>
        {
            listener.Wait(TimeSpan.FromSeconds(5));
            listener.Dispose();
        });

        var (code, output, _) = Run(port.ToString());
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.EqualTo("stopped."));
        watch.Wait();
    }

    [Test]
    public void A_watch_that_is_slow_to_leave_is_not_called_stopped()
    {
        var port = FreshPort();
        using var listener = StopSignal.Listen(port);   // never let go within the patience
        var (code, output, _) = Run(port.ToString(), patience: TimeSpan.FromMilliseconds(300));
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("still finishing"));
        Assert.That(listener.Wait(TimeSpan.Zero), Is.True, "the request was delivered");
    }

    [Test]
    public void The_default_port_is_the_watch_default()
    {
        Assert.That(StopCommand.DefaultPort, Is.EqualTo(8787));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StopCommandTests" 2>&1 | tail -5`
Expected: a compile error — `StopCommand` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace MtgaPbp.Cli;

/// <summary>
/// <c>mtga-pbp stop [port]</c>: ends a running <c>watch</c> from any terminal or script.
/// </summary>
/// <remarks>
/// A <c>watch</c> can be started where nobody can see it — a scheduled task without
/// <c>/it</c>, a hidden window, a session that has since closed — and the only way to
/// end that used to be Task Manager (#213). This finds the watch by the named event
/// <see cref="StopSignal"/> creates, fires it, then waits for the name to disappear,
/// which it does when the watch's process exits: "stopped." here means what it means in
/// the watch's own window.
/// <para>
/// Kept apart from <c>Program</c>, with its writers and its patience as parameters, so
/// each answer it can give is a test rather than a manual run.
/// </para>
/// </remarks>
public static class StopCommand
{
    /// <summary>The port <c>watch</c> serves on when none is given.</summary>
    public const int DefaultPort = 8787;

    /// <param name="portArg">The first operand, or null for the default port.</param>
    /// <param name="patience">
    /// How long to wait for the watch to be gone before saying that it is still going.
    /// A watch mid-rebuild finishes the rebuild first, which at a large archive is
    /// seconds; the request has been delivered either way.
    /// </param>
    public static int Run(string? portArg, TimeSpan patience, TextWriter stdout, TextWriter stderr)
    {
        int port;
        if (portArg is null) port = DefaultPort;
        else if (!int.TryParse(portArg, out port) || port is < 1 or > 65535)
        {
            stderr.WriteLine($"usage: mtga-pbp stop [port]   ({portArg} is not a port)");
            return 2;
        }

        if (!StopSignal.Fire(port))
        {
            stderr.WriteLine($"no watch is running on port {port}");
            return 1;
        }

        var deadline = DateTime.UtcNow + patience;
        while (StopSignal.IsListening(port))
        {
            if (DateTime.UtcNow >= deadline)
            {
                stdout.WriteLine($"asked the watch on port {port} to stop; it is still finishing " +
                                 "(a rebuild in progress takes a few seconds more)");
                return 0;
            }
            Thread.Sleep(100);
        }
        stdout.WriteLine("stopped.");
        return 0;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~StopCommandTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: Wire the verb into `Program.cs`**

The `Commands` array (currently `["capture", "build", "stats", "watch", "keep", "unkeep", "collection", "why"]`) gains `"stop"`:

```csharp
    private static readonly string[] Commands =
        ["capture", "build", "stats", "watch", "stop", "keep", "unkeep", "collection", "why"];
```

The banner condition — `stop` is a one-line answer, like `keep`:

```csharp
        if (command is not ("keep" or "unkeep" or "stop")) Banner.Write(command);
```

The dispatch `switch`, after the `"watch"` arm:

```csharp
                "stop" => StopCommand.Run(operands.FirstOrDefault(), TimeSpan.FromSeconds(10),
                                          Console.Out, Console.Error),
```

`Usage()`, after the `watch` line:

```csharp
            mtga-pbp stop [port]      stop a running watch (default 8787)
```

- [ ] **Step 6: Build, run the whole suite, commit**

```bash
dotnet build -nologo -v q 2>&1 | tail -3
dotnet test 2>&1 | tail -3
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/StopCommand.cs src/MtgaPbp.Cli/Program.cs tests/MtgaPbp.Tests/StopCommandTests.cs
git commit -m "mtga-pbp stop ends a running watch from any terminal"
```

### Task 3: `watch` waits on the signal

**Files:**
- Modify: `src/MtgaPbp.Cli/Program.cs` — `Watch`, the lines `var stop = new ManualResetEventSlim(false);` and `Console.CancelKeyPress += ...` (near line 624), and the `serving` line (near line 520).

**Interfaces:**
- Consumes: `StopSignal.Listen(int)`, `.Set()`, `.Wait(TimeSpan)` from Task 1.

- [ ] **Step 1: Move the stop signal up and make it the named one**

Delete these two lines where they are now (just above `var logs = new LogGrowth();`):

```csharp
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
```

Insert, directly after `Console.WriteLine($"serving  {server.Url}");` and its comment block:

```csharp
        // One signal, three senders: Ctrl+C here, `mtga-pbp stop` from any other
        // terminal, and — under --tray — the icon's Quit. Named after the port, so a
        // second watch on another port is a different name (#213). Created before the
        // first capture rather than after it: a `stop` typed during a long first build
        // used to find nothing listening, and a Ctrl+C during it killed the process
        // mid-write. Both now end the watch as soon as that build has landed.
        using var stop = StopSignal.Listen(server.Port);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
```

The loop condition `while (!stop.Wait(interval))` needs no change: `Wait` keeps the same shape.

- [ ] **Step 2: Build and run the suite**

Run: `dotnet build -nologo -v q 2>&1 | tail -3 && dotnet test 2>&1 | tail -3`
Expected: build clean, `Passed!` with the count from Task 2 (no new tests here — `Watch` is not unit-testable; the manual check below is its test).

- [ ] **Step 3: Update the scoreboard test expectation, if any, for the footer**

None in this slice — the footer still says `Ctrl+C to stop`. (Slice 1 changes it.)

- [ ] **Step 4: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Program.cs
git commit -m "watch waits on the named stop signal from before its first build"
```

- [ ] **Step 5: Build the debug exe**

Run: `dotnet build src/MtgaPbp.Cli -c Debug -nologo -v q 2>&1 | tail -2`
Expected: `Build succeeded.` The exe is `src/MtgaPbp.Cli/bin/Debug/net10.0/mtga-pbp.exe`.

- [ ] **Step 6: Make the scratch config, so the manual check never touches the real archive**

In PowerShell, from the repo root:

```powershell
$scratch = Join-Path $env:TEMP ("pbp-scratch-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $scratch 'archive'), (Join-Path $scratch 'out') | Out-Null
New-Item -ItemType File (Join-Path $scratch 'empty.log') | Out-Null
$cfg = @{ ArchiveDir = (Join-Path $scratch 'archive'); OutputDir = (Join-Path $scratch 'out'); LogPaths = @((Join-Path $scratch 'empty.log')) }
$cfgPath = 'src/MtgaPbp.Cli/bin/Debug/net10.0/mtga-pbp.json'
$cfg | ConvertTo-Json | Set-Content -Encoding utf8 $cfgPath
Get-Content $cfgPath | ConvertFrom-Json | Out-Null   # parses, or this throws
"scratch: $scratch"
```

Then check the `archive` and `report:` lines the exe prints point into `$scratch` — a config that fails to parse silently becomes the default config, which is the real archive.

- [ ] **Step 7: The manual check — two terminals**

Terminal A: `.\src\MtgaPbp.Cli\bin\Debug\net10.0\mtga-pbp.exe watch 8799`
Expected: the banner, `archive`/`output` lines inside the scratch folder, `serving  http://127.0.0.1:8799/`, the board.

Terminal B: `.\src\MtgaPbp.Cli\bin\Debug\net10.0\mtga-pbp.exe stop 8799`
Expected in B: `stopped.` (within ~3 s). Expected in A: a blank line, then `stopped.`, exit code 0 (`$LASTEXITCODE`).

Terminal B again: `.\src\MtgaPbp.Cli\bin\Debug\net10.0\mtga-pbp.exe stop 8799`
Expected: `no watch is running on port 8799`, exit code 1.

- [ ] **Step 8: Remove the scratch config**

```powershell
Remove-Item 'src/MtgaPbp.Cli/bin/Debug/net10.0/mtga-pbp.json'
Remove-Item -Recurse -Force $scratch
```

### Task 4: Documentation for `stop`

**Files:**
- Modify: `README.md` — the command table (near line 66), the desktop-shortcut section's "Press Ctrl+C" sentence (near line 118), and the end of the logon section (near line 325).
- Modify: `SUPPORT.md` — section 1, "close `watch`" (near line 16).

- [ ] **Step 1: The command table**

After the `watch` row:

```markdown
| `.\mtga-pbp.exe stop [port]` | stop a running `watch` from any terminal (default 8787) |
```

- [ ] **Step 2: The shortcut section**

Replace

```markdown
actually read. Press Ctrl+C in that window, or just close it, when you are done.
```

with

```markdown
actually read. Press Ctrl+C in that window, or just close it, when you are done — or
run `.\mtga-pbp.exe stop` from any terminal, which finds the watch by its port and says
`stopped.` once the process has gone.
```

- [ ] **Step 3: The logon section**

After the paragraph ending `... is a `watch` you cannot tell is running.` add:

```markdown
If one is running where you cannot see it — a task created without `/it`, say — 
`.\mtga-pbp.exe stop` ends it without Task Manager.
```

- [ ] **Step 4: SUPPORT.md**

Replace

```markdown
If the stamp is not the version you expect, close `watch`, start it again from the new
copy, and look at the output afresh.
```

with

```markdown
If the stamp is not the version you expect, close `watch` (or run `mtga-pbp stop`), start
it again from the new copy, and look at the output afresh.
```

- [ ] **Step 5: Check line endings and commit**

```bash
for f in README.md SUPPORT.md; do echo "$f $(grep -c $'\r' $f) $(wc -l < $f)"; done   # pairs equal
git add README.md SUPPORT.md
git commit -m "Say how to stop a watch you cannot see"
```

### Task 5: Slice 0 PR, review, merge, release 0.9.0

- [ ] **Step 1: Push and open the PR**

```bash
git push -u origin feat/214-stop-verb
gh pr create --base main --title 'Stop a running `watch` from any terminal: `mtga-pbp stop`' --body-file - <<'EOF'
Slice 0 of #214, from the plan approved on #213: a `stop` verb over a named event, so a `watch` running where nobody can see it can be ended without Task Manager.

- `StopSignal`: `Local\mtga-pbp-stop-<port>`, created by `watch` before its first build; Ctrl+C sets it, and so does `mtga-pbp stop [port]` from any other process.
- `mtga-pbp stop` fires it, then waits (10 s) for the name to disappear, which happens when the watch's process exits — so `stopped.` means what it means in the watch's own window. `no watch is running on port N` otherwise.
- `watch` now registers its stop signal before the first capture: a `stop` or Ctrl+C during a long first build ends the watch as soon as that build lands, instead of finding nothing or killing the process mid-write.

Tests: 11 new (`StopSignalTests`, `StopCommandTests`), Windows-only by `[Platform("Win")]`. Manual: two-terminal check against a scratch config (watch on 8799, `stop 8799` → `stopped.` both sides; again → `no watch is running`).

Minor version (a new verb).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 2: Wait for CI and confirm the test step ran**

```bash
gh pr checks --watch --interval 15
gh run view <run id> --log | grep -i "Total tests"
```

- [ ] **Step 3: Self-review**

Dispatch a code-reviewer subagent over `git diff main...HEAD`. Fix what it finds in a follow-up commit. Address any Copilot comment with a reply naming the commit.

- [ ] **Step 4: Merge and release**

```bash
gh pr merge --squash --delete-branch
git checkout main && git pull --ff-only
# Directory.Build.props: <Version>0.8.0</Version> → <Version>0.9.0</Version>
git commit -am "chore: 0.9.0" && git push origin main
git tag -a v0.9.0 -m "v0.9.0" && git push origin v0.9.0
gh run list --workflow release.yml --limit 1     # wait for it, then confirm the Smoke-run step
```

- [ ] **Step 5: Redeploy the maintainer's copy (only if a `watch` is running from `dist/`)**

The running watch predates `stop`, so it must be ended by PID once more: `Get-Process mtga-pbp | Stop-Process`. Then the CONTRIBUTING publish line into `dist/`, then `Start-Process dist/mtga-pbp.exe watch`. From this point `dist/mtga-pbp.exe stop` is the way.

---

## Slice 1 — `watch --tray`

### Task 6: `TrayEvents` — messages to actions

**Files:**
- Create: `src/MtgaPbp.Cli/Tray/TrayEvents.cs`
- Test: `tests/MtgaPbp.Tests/TrayEventsTests.cs`

**Interfaces:**
- Produces: `public enum TrayAction { None, OpenReport, ShowMenu, Quit, ReAddIcon }`; `public static class TrayEvents` with constants `WM_QUERYENDSESSION`, `WM_CONTEXTMENU`, `WM_COMMAND`, `WM_LBUTTONDBLCLK`, `WM_TRAY`, `NIN_SELECT`, `NIN_KEYSELECT`, `MenuOpenReport`, `MenuQuit`; `static TrayAction For(uint msg, nint wParam, nint lParam, uint taskbarCreated)`; `static (int X, int Y) Point(nint wParam)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MtgaPbp.Cli.Tray;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// What each thing the shell can send the icon asks the watch to do — decided here,
/// with no shell in the room, because the part that talks to Windows cannot run in CI.
/// </summary>
public class TrayEventsTests
{
    private const uint TaskbarCreated = 0xC0FF;

    private static nint Pack(int low, int high) => (nint)(((high & 0xFFFF) << 16) | (low & 0xFFFF));

    private static TrayAction Icon(uint ev) => TrayEvents.For(TrayEvents.WM_TRAY, Pack(10, 20), Pack((int)ev, 1), TaskbarCreated);

    [Test]
    public void A_left_click_opens_the_report() =>
        Assert.That(Icon(TrayEvents.NIN_SELECT), Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void Enter_on_the_focused_icon_opens_the_report_too() =>
        Assert.That(Icon(TrayEvents.NIN_KEYSELECT), Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void A_right_click_or_shift_f10_shows_the_menu() =>
        Assert.That(Icon(TrayEvents.WM_CONTEXTMENU), Is.EqualTo(TrayAction.ShowMenu));

    [Test]
    public void A_double_click_adds_nothing_to_the_click_it_contains() =>
        Assert.That(Icon(TrayEvents.WM_LBUTTONDBLCLK), Is.EqualTo(TrayAction.None));

    [Test]
    public void Mouse_movement_over_the_icon_is_nothing() =>
        Assert.That(Icon(0x0200 /* WM_MOUSEMOVE */), Is.EqualTo(TrayAction.None));

    [Test]
    public void The_menu_s_open_item_opens_the_report() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_COMMAND, (nint)TrayEvents.MenuOpenReport, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void The_menu_s_quit_item_quits() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_COMMAND, (nint)TrayEvents.MenuQuit, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.Quit));

    [Test]
    public void Logoff_quits() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_QUERYENDSESSION, 0, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.Quit));

    [Test]
    public void An_explorer_restart_puts_the_icon_back() =>
        Assert.That(TrayEvents.For(TaskbarCreated, 0, 0, TaskbarCreated), Is.EqualTo(TrayAction.ReAddIcon));

    [Test]
    public void A_failed_registration_never_matches_a_message() =>
        Assert.That(TrayEvents.For(0, 0, 0, taskbarCreated: 0), Is.EqualTo(TrayAction.None));

    [Test]
    public void Anything_else_is_nothing() =>
        Assert.That(TrayEvents.For(0x000F /* WM_PAINT */, 0, 0, TaskbarCreated), Is.EqualTo(TrayAction.None));

    [Test]
    public void The_pointer_position_unpacks_with_its_sign()
    {
        // A second monitor to the left puts x below zero.
        Assert.That(TrayEvents.Point(Pack(-100, 50)), Is.EqualTo((-100, 50)));
        Assert.That(TrayEvents.Point(Pack(2184, 1528)), Is.EqualTo((2184, 1528)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TrayEventsTests" 2>&1 | tail -5`
Expected: compile error — namespace `MtgaPbp.Cli.Tray` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace MtgaPbp.Cli.Tray;

/// <summary>What the icon asks the watch to do.</summary>
public enum TrayAction { None, OpenReport, ShowMenu, Quit, ReAddIcon }

/// <summary>
/// Turns the messages the shell sends a notification-area icon into actions, with no
/// shell in the room.
/// </summary>
/// <remarks>
/// With <c>NOTIFYICON_VERSION_4</c> the shell packs the event into the low word of
/// <c>lParam</c> and the icon id into the high word, and the pointer position into
/// <c>wParam</c>. A left click arrives as <c>NIN_SELECT</c>, Enter on a focused icon
/// (Win+B, arrows) as <c>NIN_KEYSELECT</c>, and a right click, Shift+F10 or the Apps
/// key as <c>WM_CONTEXTMENU</c>. A double-click is delivered <em>after</em> the click it
/// contains, which is why it maps to nothing: whatever it did would happen on top of
/// what the single click already did (#213, Q2).
/// </remarks>
public static class TrayEvents
{
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>The callback message the icon is registered with: <c>WM_APP + 1</c>.</summary>
    public const uint WM_TRAY = 0x8000 + 1;

    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;

    public const uint MenuOpenReport = 1;
    public const uint MenuQuit = 2;

    /// <param name="taskbarCreated">
    /// The registered <c>TaskbarCreated</c> message, or 0 when registering it failed —
    /// in which case no message can be it.
    /// </param>
    public static TrayAction For(uint msg, nint wParam, nint lParam, uint taskbarCreated)
    {
        if (msg == WM_TRAY)
        {
            var ev = (uint)(lParam & 0xFFFF);
            return ev switch
            {
                NIN_SELECT or NIN_KEYSELECT => TrayAction.OpenReport,
                WM_CONTEXTMENU => TrayAction.ShowMenu,
                _ => TrayAction.None,
            };
        }
        if (msg == WM_COMMAND)
        {
            var id = (uint)(wParam & 0xFFFF);
            return id switch
            {
                MenuOpenReport => TrayAction.OpenReport,
                MenuQuit => TrayAction.Quit,
                _ => TrayAction.None,
            };
        }
        if (msg == WM_QUERYENDSESSION) return TrayAction.Quit;
        if (taskbarCreated != 0 && msg == taskbarCreated) return TrayAction.ReAddIcon;
        return TrayAction.None;
    }

    /// <summary>The pointer position the shell packed into <c>wParam</c>: x low, y high, each signed.</summary>
    public static (int X, int Y) Point(nint wParam) =>
        ((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TrayEventsTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 12`

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Tray/TrayEvents.cs tests/MtgaPbp.Tests/TrayEventsTests.cs
git commit -m "Decide what each notification-area message asks for, without the shell"
```

### Task 7: `TrayTip` — the tooltip line

**Files:**
- Create: `src/MtgaPbp.Cli/Tray/TrayTip.cs`
- Test: `tests/MtgaPbp.Tests/TrayTipTests.cs`

**Interfaces:**
- Consumes: `SessionRow` from `MtgaPbp.Render` (`Won`, `Lost`, `Drawn`).
- Produces: `public static class TrayTip` with `const int MaxLength = 127`, `static string Compose(SessionRow? session, DateTime updated)`, `static string Clip(string s)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MtgaPbp.Cli.Tray;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The icon's tooltip is the scoreboard's headline in one line, and it is what a screen
/// reader announces at the icon — so it names the program before the score.
/// </summary>
public class TrayTipTests
{
    private static readonly DateTime At = new(2026, 9, 6, 21, 14, 9);

    private static SessionRow Session(int won, int lost, int drawn = 0) =>
        new(0, "2026-09-06 19:30", won + lost + drawn, won, lost, drawn, [], ["m1"]);

    [Test]
    public void Before_the_first_match_it_says_so() =>
        Assert.That(TrayTip.Compose(null, At), Is.EqualTo("mtga-pbp — watching · no matches yet · updated 21:14"));

    [Test]
    public void The_record_is_the_headline() =>
        Assert.That(TrayTip.Compose(Session(9, 13), At), Is.EqualTo("mtga-pbp — watching · 9-13 tonight · updated 21:14"));

    [Test]
    public void A_draw_shows_in_the_record() =>
        Assert.That(TrayTip.Compose(Session(9, 13, drawn: 1), At), Does.Contain("9-13-1 tonight"));

    [Test]
    public void It_fits_the_shell_s_128_characters()
    {
        var clipped = TrayTip.Clip(new string('x', 300));
        Assert.That(clipped.Length, Is.EqualTo(TrayTip.MaxLength));
        Assert.That(clipped, Does.EndWith("…"));
    }

    [Test]
    public void Nothing_short_is_touched()
    {
        var exact = new string('x', TrayTip.MaxLength);
        Assert.That(TrayTip.Clip(exact), Is.EqualTo(exact));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TrayTipTests" 2>&1 | tail -5`
Expected: compile error — `TrayTip` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using MtgaPbp.Render;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The icon's tooltip: the scoreboard's headline in one line.
/// </summary>
/// <remarks>
/// It is also what a screen reader announces at the icon, so it says what the thing is
/// before it says how the evening is going. <c>szTip</c> holds 128 characters including
/// the terminator; anything longer is clipped, the way the board clips.
/// </remarks>
public static class TrayTip
{
    /// <summary>What fits in <c>NOTIFYICONDATA.szTip</c> beside its terminator.</summary>
    public const int MaxLength = 127;

    public static string Compose(SessionRow? session, DateTime updated)
    {
        var record = session is null
            ? "no matches yet"
            : (session.Drawn == 0
                ? $"{session.Won}-{session.Lost}"
                : $"{session.Won}-{session.Lost}-{session.Drawn}") + " tonight";
        return Clip($"mtga-pbp — watching · {record} · updated {updated:HH:mm}");
    }

    public static string Clip(string s) =>
        s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TrayTipTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Tray/TrayTip.cs tests/MtgaPbp.Tests/TrayTipTests.cs
git commit -m "The icon's tooltip is the scoreboard's headline"
```

### Task 8: `NativeMethods` and `ConsoleOwnership`

**Files:**
- Create: `src/MtgaPbp.Cli/Tray/NativeMethods.cs`
- Create: `src/MtgaPbp.Cli/Tray/ConsoleOwnership.cs`
- Test: `tests/MtgaPbp.Tests/ConsoleOwnershipTests.cs`

**Interfaces:**
- Produces: `internal static class NativeMethods` (every declaration below; Task 10 uses them all); `public static class ConsoleOwnership` with `static bool ShouldDetach(uint attachedProcesses)`, `static uint AttachedProcesses()`, `static bool Detach()`.

- [ ] **Step 1: Write the failing test**

```csharp
using MtgaPbp.Cli.Tray;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// When <c>watch --tray</c> may let go of its console: only when nothing else is
/// attached to it, so that a shell it was typed into keeps its prompt and its Ctrl+C.
/// </summary>
public class ConsoleOwnershipTests
{
    [Test]
    public void Alone_on_the_console_means_a_shortcut_started_it() =>
        Assert.That(ConsoleOwnership.ShouldDetach(1), Is.True);

    [Test]
    public void A_shell_on_the_console_keeps_it() =>
        Assert.That(ConsoleOwnership.ShouldDetach(3), Is.False);

    [Test]
    public void No_console_at_all_has_nothing_to_let_go_of() =>
        Assert.That(ConsoleOwnership.ShouldDetach(0), Is.False);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConsoleOwnershipTests" 2>&1 | tail -5`
Expected: compile error — `ConsoleOwnership` does not exist.

- [ ] **Step 3: Write `NativeMethods.cs`**

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The Win32 surface the icon needs, and nothing else.
/// </summary>
/// <remarks>
/// Every entry point that has a <c>W</c> variant is named with it and marked
/// <c>ExactSpelling</c>: the investigation's spike bound <c>DefWindowProc</c> without
/// either, got the ANSI variant for a Unicode window class, and read its own title back
/// as garbage. Here there is no guess for the runtime to make.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd; public uint message; public nint wParam, lParam;
        public uint time; public POINT pt; public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIIF_INFO = 1;
    public const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    public const uint MB_OK = 0x0000, MB_ICONWARNING = 0x0030;
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetConsoleProcessList(uint[] list, uint count);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    public static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyWindow(nint h);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)]
    public static extern int GetMessage(out MSG msg, nint h, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    public static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    public static extern bool PostMessage(nint h, uint msg, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string s);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint wnd, nint tpm);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetForegroundWindow(nint h);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyIcon(nint h);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MessageBox(nint h, string text, string caption, uint type);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint n);
}
```

- [ ] **Step 4: Write `ConsoleOwnership.cs`**

```csharp
using System.Runtime.Versioning;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// Whether this process is the only thing attached to its console — and so whether
/// the window would close if it let go.
/// </summary>
/// <remarks>
/// A shortcut, a double-click, the Startup folder and a scheduled task all give the
/// process a console of its own: the attached-process list is just us, and
/// <c>FreeConsole</c> closes the window. (Measured under Windows Terminal, where hiding
/// the window is a no-op — see the design note for #213.) Typed into a terminal, the
/// shell is attached too; letting go would leave its prompt held by a process it can no
/// longer Ctrl+C, so the console is kept and the icon is simply added beside it.
/// </remarks>
public static class ConsoleOwnership
{
    /// <summary>The rule, apart from the call, so it can be tested without a console.</summary>
    public static bool ShouldDetach(uint attachedProcesses) => attachedProcesses == 1;

    /// <summary>How many processes share this console; 0 when there is none.</summary>
    /// <remarks>
    /// Two slots are enough: a buffer too small for the list makes the call return the
    /// count it would have needed, and 2 already means "not alone".
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static uint AttachedProcesses()
    {
        var ids = new uint[2];
        return NativeMethods.GetConsoleProcessList(ids, (uint)ids.Length);
    }

    /// <summary>
    /// Lets go of the console and points the standard streams at nothing, so that
    /// everything the watch would have printed is dropped rather than thrown.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Detach()
    {
        if (!NativeMethods.FreeConsole()) return false;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        return true;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConsoleOwnershipTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 3`. Also `dotnet build -nologo -v q 2>&1 | grep -c CA1416` → `0`.

- [ ] **Step 6: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Tray/NativeMethods.cs src/MtgaPbp.Cli/Tray/ConsoleOwnership.cs tests/MtgaPbp.Tests/ConsoleOwnershipTests.cs
git commit -m "The Win32 the icon needs, and the rule for letting go of the console"
```

### Task 9: The scoreboard's stop hint

**Files:**
- Modify: `src/MtgaPbp.Render/Scoreboard.cs` — the `Lines` signature (line 54) and the footer line (`  updated ... · Ctrl+C to stop`).
- Test: `tests/MtgaPbp.Tests/ScoreboardTests.cs`

**Interfaces:**
- Produces: `Scoreboard.Lines(..., int width = 80, int height = 24, string stopHint = "Ctrl+C to stop")`.

- [ ] **Step 1: Write the failing test**

Add to `ScoreboardTests`:

```csharp
    [Test]
    public void The_footer_says_how_to_stop_and_can_be_told_otherwise()
    {
        Assert.That(Text(Board()), Does.Contain("· Ctrl+C to stop"));
        var lines = Scoreboard.Lines(Session(), [], null, null, "http://127.0.0.1:8787/", Updated,
                                     78, 30, stopHint: "Ctrl+C or the icon's Quit to stop");
        Assert.That(Text(lines), Does.Contain("· Ctrl+C or the icon's Quit to stop"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ScoreboardTests.The_footer" 2>&1 | tail -5`
Expected: compile error — no `stopHint` parameter.

- [ ] **Step 3: Implement**

Add the parameter after `height`:

```csharp
        int width = 80,
        int height = 24,
        string stopHint = "Ctrl+C to stop")
```

and change the footer line to:

```csharp
        lines.Add($"  updated {updated:HH:mm:ss} · live at {url} · {stopHint}");
```

Add to the `<param>` docs: `/// <param name="stopHint">How this watch is stopped — Ctrl+C alone, or the icon's Quit as well under <c>--tray</c>.</param>`

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test 2>&1 | tail -3`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Render/Scoreboard.cs tests/MtgaPbp.Tests/ScoreboardTests.cs
git commit -m "The board's footer can name the icon as a way to stop"
```

### Task 10: `TrayIcon` — the shell

**Files:**
- Create: `src/MtgaPbp.Cli/Tray/TrayIcon.cs`

**Interfaces:**
- Consumes: everything in `NativeMethods` (Task 8); `TrayEvents.For`, `TrayEvents.Point`, the `TrayEvents` constants (Task 6).
- Produces: `[SupportedOSPlatform("windows")] public sealed class TrayIcon : IDisposable` with `static TrayIcon Start(string tip, Action openReport, Action quit)` (throws `InvalidOperationException` when the shell refuses), `void SetTip(string tip)`, `void Balloon(string title, string text)`, `void Dispose()`.

No unit test: this is the part that talks to Windows, checked by hand in Task 13.

- [ ] **Step 1: Write the class**

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MtgaPbp.Cli.Tray.NativeMethods;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The notification-area icon <c>watch --tray</c> lives behind: one hidden window on
/// its own thread, one icon, one menu.
/// </summary>
/// <remarks>
/// A hidden top-level window rather than a message-only one: the shell announces a
/// recreated taskbar by broadcast, broadcasts do not reach message-only windows, and an
/// icon whose window missed it is gone for good when Explorer restarts. The message loop
/// runs on a background thread so it can never be what keeps the process alive — the
/// watch's own loop is the foreground.
/// <para>
/// Every message that means something is turned into a <see cref="TrayAction"/> by
/// <see cref="TrayEvents"/>, which is the half with tests; this is the half that talks to
/// Windows, and is checked by hand (the checklist on the pull request). Menu choices are
/// posted back to the window as <c>WM_COMMAND</c> so that they, too, go through the
/// tested mapping.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "MtgaPbpTray";
    private const uint IconId = 1;

    /// <summary><c>WM_APP + 2</c>; <c>WM_APP + 1</c> is the icon's own callback.</summary>
    private const uint WM_APP_QUIT = 0x8000 + 2;

    private readonly Action _openReport;
    private readonly Action _quit;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private WndProc? _proc;          // held so the collector cannot take the callback from under the window
    private nint _hwnd, _icon;
    private uint _taskbarCreated;
    private string _tip;
    private volatile bool _added;
    private Exception? _failure;

    private TrayIcon(string tip, Action openReport, Action quit)
    {
        _tip = tip;
        _openReport = openReport;
        _quit = quit;
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
    }

    /// <summary>
    /// Puts the icon up and returns once it is there. Throws when the shell refused it,
    /// so the caller can stay in the console it still has.
    /// </summary>
    public static TrayIcon Start(string tip, Action openReport, Action quit)
    {
        var tray = new TrayIcon(tip, openReport, quit);
        tray._thread.Start();
        tray._ready.Wait();
        if (tray._failure is not null) throw tray._failure;
        return tray;
    }

    /// <summary>Replaces the tooltip. Safe from any thread: the shell does the marshalling.</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>A notification balloon — a toast, on Windows 10 and later.</summary>
    public void Balloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Takes the icon down and ends the message loop. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_hwnd != 0 && _thread.IsAlive)
        {
            PostMessage(_hwnd, WM_APP_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _proc = WindowProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _proc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed (error {Marshal.GetLastWin32Error()})");
            _hwnd = CreateWindowEx(0, ClassName, "mtga-pbp", 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
            if (_hwnd == 0)
                throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()})");
            _icon = OwnIcon();
            if (!Add())
                throw new InvalidOperationException("the shell refused the notification-area icon");
        }
        catch (Exception e)
        {
            _failure = e;
            Remove();
            _ready.Set();
            return;
        }
        _ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private bool Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = TrayEvents.WM_TRAY;
        data.hIcon = _icon;
        data.szTip = _tip;
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
        return true;
    }

    private void Remove()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>The exe's own icon, which #66 put there for the taskbar.</summary>
    private static nint OwnIcon()
    {
        var small = new nint[1];
        var path = Environment.ProcessPath;
        if (path is not null && ExtractIconEx(path, 0, null, small, 1) > 0) return small[0];
        return 0;   // the shell draws a blank; still something to quit from
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_APP_QUIT)
        {
            Remove();
            DestroyWindow(hwnd);
            return 0;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        switch (TrayEvents.For(msg, wParam, lParam, _taskbarCreated))
        {
            case TrayAction.OpenReport:
                _openReport();
                return 0;
            case TrayAction.ShowMenu:
                ShowMenu(TrayEvents.Point(wParam));
                return 0;
            case TrayAction.Quit:
                _quit();
                // WM_QUERYENDSESSION wants TRUE for "go ahead"; a menu command ignores it.
                return msg == TrayEvents.WM_QUERYENDSESSION ? 1 : 0;
            case TrayAction.ReAddIcon:
                _added = false;
                Add();
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu((int X, int Y) at)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, TrayEvents.MenuOpenReport, "&Open report");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, TrayEvents.MenuQuit, "&Quit");
            SetMenuDefaultItem(menu, TrayEvents.MenuOpenReport, 0);
            // Without the foreground call the menu stays up after the pointer leaves it
            // (Microsoft KB 135788); the WM_NULL afterwards is the same article's other half.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.X, at.Y, _hwnd, 0);
            PostMessage(_hwnd, WM_NULL, 0, 0);
            if (chosen != 0) PostMessage(_hwnd, TrayEvents.WM_COMMAND, chosen, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
```

- [ ] **Step 2: Build clean**

Run: `dotnet build -nologo -v q 2>&1 | tail -3` and `dotnet build -nologo 2>&1 | grep -c "warning CA1416"` → `0`.

- [ ] **Step 3: Commit**

```bash
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Tray/TrayIcon.cs
git commit -m "A notification-area icon a console process can own"
```

### Task 11: `watch --tray`

**Files:**
- Modify: `src/MtgaPbp.Cli/Program.cs` — `Options` (line 66), `Main`'s flags and the `"watch"` dispatch arm, `Watch`'s signature, the `SocketException` catch, the block after the stop signal, `Repaint`, and the board's `Draw` call.

**Interfaces:**
- Consumes: `TrayIcon.Start/SetTip/Balloon/Dispose` (Task 10), `TrayTip.Compose` (Task 7), `ConsoleOwnership.ShouldDetach/AttachedProcesses/Detach` (Task 8), `NativeMethods.MessageBox` and the `MB_*` constants (Task 8), `Scoreboard.Lines(..., stopHint:)` (Task 9), `StopSignal` (Task 1).

- [ ] **Step 1: The flag**

```csharp
    private static readonly string[] Options = ["--open", "--rebuild", "--prune", "--tray"];
```

In `Main`, beside `var rebuild = args.Contains("--rebuild");`:

```csharp
        // Live in the notification area instead of a window. A flag rather than a config
        // key: the same exe is started three ways (shortcut, Startup folder, terminal),
        // and the target line of a shortcut is where the choice belongs (#213).
        var tray = args.Contains("--tray");
```

and the dispatch arm becomes `"watch" => Watch(cfg, operands, open, prune, rebuild, tray),`.

- [ ] **Step 2: A lease around the icon, so `using var` needs no platform guard**

Add inside `Program`, near `Watch`:

```csharp
    /// <summary>
    /// The icon's lifetime as a plain <see cref="IDisposable"/>: <c>TrayIcon</c> is a
    /// Windows-only type, and every touch of it has to sit inside a platform check —
    /// this holds those checks so that <c>Watch</c> can say <c>using var</c> and move on.
    /// A lease with no icon is the ordinary, windowed watch.
    /// </summary>
    private sealed class TrayLease(Tray.TrayIcon? icon) : IDisposable
    {
        public bool Active => icon is not null;

        public void Tip(string text)
        {
            if (OperatingSystem.IsWindows()) icon?.SetTip(text);
        }

        public void Balloon(string title, string text)
        {
            if (OperatingSystem.IsWindows()) icon?.Balloon(title, text);
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows()) icon?.Dispose();
        }
    }

    /// <summary>
    /// The icon, or none — with the reason said, because a watch asked to live in the
    /// notification area and left in a window should not leave that unexplained.
    /// </summary>
    private static TrayLease StartTray(bool wanted, LiveServer server, StopSignal stop)
    {
        if (!wanted) return new TrayLease(null);
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--tray needs Windows; staying in this window");
            return new TrayLease(null);
        }
        try
        {
            return new TrayLease(Tray.TrayIcon.Start(
                Tray.TrayTip.Compose(null, DateTime.Now),
                openReport: () => OpenInBrowser(server.Url),
                quit: stop.Set));
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"no notification-area icon ({e.Message}); staying in this window");
            return new TrayLease(null);
        }
    }
```

- [ ] **Step 3: `Watch` — signature and the port refusal**

Signature: `private static int Watch(Config cfg, string[] operands, bool open, bool prune, bool rebuild, bool tray)`.

The `catch (SocketException ex)` block becomes:

```csharp
        catch (SocketException ex)
        {
            var message = $"could not listen on port {port} ({ex.Message}). " +
                          "Pass a different one: mtga-pbp watch 9000";
            // Under --tray from a shortcut there is no window for the line below to land
            // in, and a second watch started at logon beside a hand-started one is the
            // usual way here. A box is the one thing that is seen either way.
            if (tray && OperatingSystem.IsWindows())
                Tray.NativeMethods.MessageBox(0,
                    $"{message}\n\nIs another watch already running? Quit it from its icon, " +
                    $"or run: mtga-pbp stop {port}",
                    "mtga-pbp", Tray.NativeMethods.MB_OK | Tray.NativeMethods.MB_ICONWARNING);
            Console.Error.WriteLine(message);
            return 2;
        }
```

- [ ] **Step 4: `Watch` — the icon, the announcement, the detach**

Directly after the `Console.CancelKeyPress += ...` line from Task 3:

```csharp
        using var lease = StartTray(tray, server, stop);
        if (lease.Active)
        {
            Console.WriteLine("running in the notification area — right-click the icon to quit, " +
                              $"or run: mtga-pbp stop {server.Port}");
            // Let go of the window only when it is ours alone. Typed into a terminal, the
            // shell is attached too, and it keeps its prompt and its Ctrl+C; from a
            // shortcut or the Startup folder the window has said all it had to say.
            if (OperatingSystem.IsWindows() &&
                Tray.ConsoleOwnership.ShouldDetach(Tray.ConsoleOwnership.AttachedProcesses()))
            {
                lease.Balloon("mtga-pbp",
                    $"Watching. The report is at {server.Url} — right-click this icon to quit.");
                Tray.ConsoleOwnership.Detach();
            }
        }
```

- [ ] **Step 5: `Watch` — the tooltip and the footer**

In `Repaint`, after `var tonight = st.Sessions.FirstOrDefault();`:

```csharp
            lease.Tip(Tray.TrayTip.Compose(tonight, DateTime.Now));
```

The `board.Draw(Scoreboard.Lines(...))` call gains a last argument:

```csharp
            board.Draw(Scoreboard.Lines(
                tonight, beats, playing,
                cfg.SuggestDeckRotation ? SessionCoach.NextUp(st, slug) : null,
                server.Url, DateTime.Now, board.Width, board.Height,
                stopHint: lease.Active ? "Ctrl+C or the icon's Quit to stop" : "Ctrl+C to stop"));
```

- [ ] **Step 6: `Usage()`**

Replace the `watch` line with:

```csharp
            mtga-pbp watch [port] [--tray]  serve the report and keep it live (default 8787);
                                      --tray lives in the notification area instead
```

- [ ] **Step 7: Build, test, commit**

```bash
dotnet build -nologo -v q 2>&1 | tail -3
dotnet build -nologo 2>&1 | grep -c "warning CA1416"      # 0
dotnet test 2>&1 | tail -3
dotnet format --verify-no-changes
git add src/MtgaPbp.Cli/Program.cs
git commit -m "watch --tray: the same watch behind an icon, its window let go when it is ours"
```

### Task 12: README for `--tray`

**Files:**
- Modify: `README.md` — after the `#### A desktop shortcut that runs it` section (before `### Rebuilds only touch what changed`), the command table, and the logon section's closing paragraph.

- [ ] **Step 1: The command table**

Change the `watch` row to:

```markdown
| `.\mtga-pbp.exe watch [--tray]` | serve the report and keep it live (see below); `--tray` puts it in the notification area |
```

- [ ] **Step 2: The new section**

```markdown
#### In the notification area

```powershell
.\mtga-pbp.exe watch --tray
```

The same `watch`, living behind an icon in the notification area instead of a window.
Started from a shortcut, the Startup folder or a scheduled task, its window closes as
soon as it has said where the report is; started from a terminal you typed into, that
terminal is kept — Ctrl+C still works — and the icon is added beside it.

- **Left-click** the icon (or <kbd>Win</kbd>+<kbd>B</kbd>, arrow to it, <kbd>Enter</kbd>)
  to open the report.
- **Right-click** it (or <kbd>Shift</kbd>+<kbd>F10</kbd>) for the menu: **Open report**
  and **Quit**. Quit is the same stop as Ctrl+C: the icon goes, the port closes, the
  process exits.
- The tooltip is the scoreboard's headline — tonight's record and when it last updated.
- `.\mtga-pbp.exe stop` ends it too.

Windows 11 keeps a new icon behind the **^** chevron until you drag it out or turn it on
under Settings › Personalization › Taskbar › Other system tray icons; a notification on
start says it is there. Double-clicking does nothing the single click did not.

To make a shortcut for it, build the `watch` shortcut as above and put ` --tray` after
`watch`, outside the quotes: `"C:\path\to\mtga-pbp\mtga-pbp.exe" watch --tray`.
```

- [ ] **Step 3: The logon section**

After the paragraph that ends `... is a `watch` you cannot tell is running.` (and the `stop` sentence from Task 4), add:

```markdown
If you would rather have the icon than the window at every logon, put ` --tray` after
`watch` in the shortcut's target or the `schtasks` line. Then the icon is how you can
tell it is running, and its **Quit** — or `stop` — is how you end it. Keep `/it` on the
scheduled task either way: the icon needs your session as much as the window did.
```

- [ ] **Step 4: Check line endings and commit**

```bash
echo "README.md $(grep -c $'\r' README.md) $(wc -l < README.md)"   # equal
git add README.md
git commit -m "Say how watch --tray works and how to stop it"
```

### Task 13: The checklist, the PR, review, merge, release 0.10.0

- [ ] **Step 1: Build the debug exe and the scratch config (Task 3, steps 5–6)**

- [ ] **Step 2: Typed into a terminal**

`.\src\MtgaPbp.Cli\bin\Debug\net10.0\mtga-pbp.exe watch 8799 --tray`
Expected: the banner; `serving`; `running in the notification area — right-click the icon to quit, or run: mtga-pbp stop 8799`; the window stays (a shell is attached); the board's footer reads `Ctrl+C or the icon's Quit to stop`; an icon in the notification area (look behind the ^ chevron) with the exe's own picture and the tooltip `mtga-pbp — watching · no matches yet · updated HH:MM`.
Right-click the icon → a menu **Open report** (bold) / **Quit**. Quit → the window prints `stopped.`, the icon is gone, `$LASTEXITCODE` is 0.

- [ ] **Step 3: From a shortcut-style launch**

`Start-Process .\src\MtgaPbp.Cli\bin\Debug\net10.0\mtga-pbp.exe -ArgumentList 'watch 8799 --tray'`
Expected: a window appears and closes by itself once `serving` has printed; a toast "Watching. The report is at http://127.0.0.1:8799/ — right-click this icon to quit."; the icon is there. Left-click → the report opens in the browser. `.\src\...\mtga-pbp.exe stop 8799` → `stopped.`; the icon is gone; `Get-Process mtga-pbp` no longer lists it.

- [ ] **Step 4: Keyboard**

Start it again as in step 3. <kbd>Win</kbd>+<kbd>B</kbd>, arrow keys to the icon (open the overflow with Enter if it is behind the chevron), <kbd>Enter</kbd> → the report opens. <kbd>Shift</kbd>+<kbd>F10</kbd> on it → the menu; arrow to Quit, Enter → gone.

- [ ] **Step 5: Explorer restart**

Start it again. Task Manager → Windows Explorer → Restart. Expected: the icon comes back on its own within a few seconds.

- [ ] **Step 6: Second instance**

With one running on 8799 from step 5: `Start-Process ... -ArgumentList 'watch 8799 --tray'`. Expected: a message box "could not listen on port 8799 … Is another watch already running? Quit it from its icon, or run: mtga-pbp stop 8799". OK → that second process exits 2. Then `stop 8799`.

- [ ] **Step 7: Screen reader (the maintainer)**

With NVDA running: <kbd>Win</kbd>+<kbd>B</kbd> to the icon — the tooltip text is announced; the menu items are announced as "Open report" and "Quit". Record the result on the PR; if the icon is silent, that is a finding for the PR, not a reason to hold it — `stop` still works.

- [ ] **Step 8: Remove the scratch config (Task 3, step 8)**

- [ ] **Step 9: Push, PR, CI, self-review, merge, release 0.10.0**

As Task 5, with the title `watch --tray: live in the notification area, quit from its icon` and the checklist above pasted into the PR body with each line marked done / not done / by whom. Bump `Directory.Build.props` to 0.10.0, tag `v0.10.0`. Redeploy `dist/` per CONTRIBUTING; from now on `dist\mtga-pbp.exe stop` ends the maintainer's watch, and the Startup shortcut can gain ` --tray`.

---

## Self-review against the spec

- Spec Q1-A (P/Invoke icon): Tasks 8, 10. Q1-C (`stop` verb): Tasks 1–3. Q2 (no double-click): Task 6 test `A_double_click_adds_nothing…`. Q3 (no window in slice 1; detach only when alone): Tasks 8, 11. Q4 (menu of two, tooltip = headline, balloon on start, keyboard reach): Tasks 7, 10, 11; keyboard and NVDA are checklist steps 4 and 7. Q5 (flag not key, MessageBox on refusal, one stop signal, icon removed in the teardown path, background loop thread): Tasks 10, 11 — the icon is removed in `WindowProc`'s `WM_APP_QUIT` path which `Dispose` posts, and `Dispose` runs from `using var` on every exit from `Watch` after the lease is taken, including the exception path.
- Risks list: 128-char tip (Task 7 test); `W` entry points (Task 8); two watches (Task 11 step 3); CI cannot see a tray (the only untested class is `TrayIcon`, by design).
- Slice 2 (Show window, QuitWithArena) is not in this plan, as decided.
- Names used across tasks: `StopSignal.Listen/Fire/IsListening/Set/Wait`, `StopCommand.Run/DefaultPort`, `TrayEvents.For/Point` and its constants, `TrayTip.Compose/Clip/MaxLength`, `ConsoleOwnership.ShouldDetach/AttachedProcesses/Detach`, `TrayIcon.Start/SetTip/Balloon/Dispose`, `Scoreboard.Lines(..., stopHint:)`, `TrayLease.Active/Tip/Balloon`, `StartTray` — consistent between the task that defines each and the tasks that use it.
