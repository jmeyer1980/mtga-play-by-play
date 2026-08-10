# MTGA Play-by-Play Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn MTG Arena match logs into readable, searchable, shareable chess-style text transcripts.

**Architecture:** Two stages. `capture` slices `Player.log` into per-match gzip archives keyed by `matchId`; `build` re-parses the archive into a typed event stream and renders an HTML index, per-game HTML pages, and markdown exports. Raw archive is the durable source of truth so parser improvements apply retroactively via `--rebuild`.

**Tech Stack:** C# / .NET 10, `System.Text.Json`, `Microsoft.Data.Sqlite`, NUnit.

## Global Constraints

- Target framework `net10.0`. Language version default (C# 14).
- **No network access.** Every name resolves against the local card database.
- **Never crash on log input, never fail silently.** Malformed lines are skipped and counted; unknown annotation types become `Unknown` events and are counted by type name.
- Card titles are stored at `Formatted = 1` in `Localizations_enUS`, **not** `Formatted = 0`. Always query `ORDER BY Formatted LIMIT 1`.
- The card database is opened **read-only** (`Mode=ReadOnly`). Never write to it.
- The local player's seat is resolved **per match** and never carried across matches.
- Test framework is NUnit. Per project policy, `Assert.Warn` is only for documented accepted limitations and must assert the specific known condition.

## File Structure

```
MtgaPbp.sln
src/MtgaPbp.Core/
  Model.cs              LogEnvelope, MatchSlice, CardInfo, GameEvent, EventKind
  LogScanner.cs         file -> LogEnvelope stream, skips non-JSON
  MatchSlicer.cs        envelopes -> MatchSlice[] grouped by matchId
  RawArchive.cs         gzip write + dedupe ledger
  CardDb.cs             grpId/locId -> names, read-only SQLite
  GameStateTracker.cs   Full/Diff state, object table, alias map, life, turn
  EventExtractor.cs     annotations -> GameEvent[]
src/MtgaPbp.Render/
  Narrator.cs           GameEvent -> beat / verbose lines
  MarkdownRenderer.cs   transcript -> .md
  GamePageRenderer.cs   transcript -> per-game .html
  IndexRenderer.cs      match summaries -> index.html with embedded search
src/MtgaPbp.Cli/
  Program.cs            command dispatch
  Config.cs             mtga-pbp.json, path discovery
tests/MtgaPbp.Tests/
  CardDbTests.cs  LogScannerTests.cs  MatchSlicerTests.cs  RawArchiveTests.cs
  GameStateTrackerTests.cs  EventExtractorTests.cs  NarratorTests.cs
  RendererTests.cs  GoldenFileTests.cs
  Fixtures/
```

`Core` knows GRE shapes; `Render` sees only `GameEvent`. A GRE format change touches `EventExtractor` alone, and renderers are tested against hand-built event lists with no log fixtures.

**Model note:** `GameEvent` is a single wide record with nullable fields rather than an abstract-record hierarchy. These are structured log lines with a common context envelope; a flat shape keeps the narrator a single `switch` and serializes to the HTML page without polymorphic converters. The cost is nullable fields that only apply to some kinds — accepted deliberately.

---

### Task 1: Solution scaffold and CardDb

**Files:**
- Create: `MtgaPbp.sln`, `src/MtgaPbp.Core/MtgaPbp.Core.csproj`, `src/MtgaPbp.Core/Model.cs`, `src/MtgaPbp.Core/CardDb.cs`
- Create: `tests/MtgaPbp.Tests/MtgaPbp.Tests.csproj`, `tests/MtgaPbp.Tests/CardDbTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CardInfo(int GrpId, string Name, string Types, string? Power, string? Toughness, bool IsToken)`; `ICardDb` with `string? NameForLocId(int locId)`, `CardInfo? CardForGrpId(int grpId)`; `CardDb : ICardDb, IDisposable` with `CardDb(string dbPath)` and `static string? FindDatabase(string? overridePath)`.

- [ ] **Step 1: Create the solution and projects**

```bash
cd /c/Users/jerio/RiderProjects/MTGA_Play-by
dotnet new sln -n MtgaPbp
dotnet new classlib -o src/MtgaPbp.Core -f net10.0
dotnet new classlib -o src/MtgaPbp.Render -f net10.0
dotnet new console  -o src/MtgaPbp.Cli -f net10.0
dotnet new nunit    -o tests/MtgaPbp.Tests -f net10.0
rm -f src/MtgaPbp.Core/Class1.cs src/MtgaPbp.Render/Class1.cs tests/MtgaPbp.Tests/UnitTest1.cs
dotnet sln add src/MtgaPbp.Core src/MtgaPbp.Render src/MtgaPbp.Cli tests/MtgaPbp.Tests
dotnet add src/MtgaPbp.Core package Microsoft.Data.Sqlite
dotnet add src/MtgaPbp.Render reference src/MtgaPbp.Core
dotnet add src/MtgaPbp.Cli reference src/MtgaPbp.Core src/MtgaPbp.Render
dotnet add tests/MtgaPbp.Tests reference src/MtgaPbp.Core src/MtgaPbp.Render
dotnet build
```

Expected: build succeeds, 4 projects.

- [ ] **Step 2: Write the failing test**

`tests/MtgaPbp.Tests/CardDbTests.cs` — builds a synthetic SQLite database matching the real schema, so the test does not depend on MTGA being installed.

```csharp
using Microsoft.Data.Sqlite;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class CardDbTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"carddb_{Guid.NewGuid():N}.sqlite");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        Exec(con, @"CREATE TABLE Cards (GrpId INT PRIMARY KEY, TitleId INT, Types TEXT,
                                        Power TEXT, Toughness TEXT, IsToken BOOLEAN)");
        Exec(con, @"CREATE TABLE Localizations_enUS (LocId INT, Formatted INT, Loc TEXT,
                                                     PRIMARY KEY (LocId, Formatted))");
        // Real DB stores card titles at Formatted = 1 only.
        Exec(con, "INSERT INTO Cards VALUES (96179, 648, '5', '', '', 0)");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (648, 1, 'Plains')");
        Exec(con, "INSERT INTO Cards VALUES (91843, 700, '2', '1', '1', 1)");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (700, 1, 'Rabbit')");
        // A LocId that also has a Formatted=0 row, to prove ordering is stable.
        Exec(con, "INSERT INTO Localizations_enUS VALUES (900, 0, 'plain text')");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (900, 1, 'formatted')");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void CardForGrpId_resolves_title_stored_at_Formatted_1()
    {
        using var db = new CardDb(_dbPath);
        var card = db.CardForGrpId(96179);
        Assert.That(card, Is.Not.Null);
        Assert.That(card!.Name, Is.EqualTo("Plains"));
        Assert.That(card.IsToken, Is.False);
    }

    [Test]
    public void CardForGrpId_reads_token_flag_and_stats()
    {
        using var db = new CardDb(_dbPath);
        var card = db.CardForGrpId(91843)!;
        Assert.That(card.Name, Is.EqualTo("Rabbit"));
        Assert.That(card.IsToken, Is.True);
        Assert.That(card.Power, Is.EqualTo("1"));
    }

    [Test]
    public void CardForGrpId_returns_null_for_ability_grpid_absent_from_Cards()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForGrpId(176406), Is.Null);
    }

    [Test]
    public void NameForLocId_prefers_lowest_Formatted_deterministically()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.NameForLocId(900), Is.EqualTo("plain text"));
        Assert.That(db.NameForLocId(648), Is.EqualTo("Plains"));
    }

    [Test]
    public void NameForLocId_returns_null_when_missing()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.NameForLocId(123456), Is.Null);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~CardDbTests"`
Expected: FAIL — `CardDb` and `CardInfo` do not exist (compile error).

- [ ] **Step 4: Write Model.cs**

`src/MtgaPbp.Core/Model.cs`:

```csharp
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record CardInfo(
    int GrpId, string Name, string Types, string? Power, string? Toughness, bool IsToken);

public interface ICardDb
{
    string? NameForLocId(int locId);
    CardInfo? CardForGrpId(int grpId);
}

public sealed record LogEnvelope(long LineNumber, long TimestampMs, JsonElement Root);

public sealed record MatchSlice(
    string MatchId,
    long StartedAtMs,
    long EndedAtMs,
    IReadOnlyList<string> RawLines,
    bool Incomplete);

public enum EventKind
{
    GameStart, Mulligan, TurnStart, PhaseChange,
    LandPlayed, SpellCast, Resolved, Countered,
    Drew, Discarded, Destroyed, Sacrificed, Exiled, Returned,
    StateBasedAction, ZoneMove,
    Damage, LifeChanged, TokenCreated, CounterChanged,
    Scry, Revealed, ManaPaid, Attack, Block, GameEnd, Unknown
}

/// <summary>
/// One transcript-relevant occurrence. Wide-and-nullable by design: these are
/// structured log lines sharing a context envelope, and a flat shape keeps the
/// narrator a single switch and serializes without polymorphic converters.
/// </summary>
public sealed record GameEvent
{
    public int Seq { get; init; }
    public long TimestampMs { get; init; }
    public int GameNumber { get; init; }
    public int Turn { get; init; }
    public int ActiveSeat { get; init; }
    public int Phase { get; init; }
    public int Step { get; init; }
    public EventKind Kind { get; init; }

    public int? ActorSeat { get; init; }
    public int? SourceInstanceId { get; init; }
    public string? SourceName { get; init; }
    public int? TargetInstanceId { get; init; }
    public string? TargetName { get; init; }
    public int? TargetSeat { get; init; }
    public int Amount { get; init; }
    public string? Detail { get; init; }
    public string? RawType { get; init; }
}
```

- [ ] **Step 5: Write CardDb.cs**

`src/MtgaPbp.Core/CardDb.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MtgaPbp.Core;

public sealed class CardDb : ICardDb, IDisposable
{
    private readonly SqliteConnection _con;
    private readonly Dictionary<int, string?> _locCache = new();
    private readonly Dictionary<int, CardInfo?> _cardCache = new();

    public CardDb(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Card database not found at: {dbPath}", dbPath);
        _con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        _con.Open();
    }

    /// <summary>Newest Raw_CardDatabase_*.mtga under the known Arena install paths.</summary>
    public static string? FindDatabase(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        string[] roots =
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
        ];

        return roots.Where(Directory.Exists)
                    .SelectMany(r => Directory.EnumerateFiles(r, "Raw_CardDatabase_*.mtga"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
    }

    public string? NameForLocId(int locId)
    {
        if (_locCache.TryGetValue(locId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        cmd.CommandText =
            "SELECT Loc FROM Localizations_enUS WHERE LocId = $id ORDER BY Formatted LIMIT 1";
        cmd.Parameters.AddWithValue("$id", locId);
        var result = cmd.ExecuteScalar() as string;
        _locCache[locId] = result;
        return result;
    }

    public CardInfo? CardForGrpId(int grpId)
    {
        if (_cardCache.TryGetValue(grpId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        // Card titles live at Formatted = 1; ORDER BY keeps this deterministic.
        cmd.CommandText = """
            SELECT c.GrpId, l.Loc, c.Types, c.Power, c.Toughness, c.IsToken
            FROM Cards c
            LEFT JOIN Localizations_enUS l ON l.LocId = c.TitleId
            WHERE c.GrpId = $id
            ORDER BY l.Formatted
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", grpId);
        using var r = cmd.ExecuteReader();
        CardInfo? info = null;
        if (r.Read() && !r.IsDBNull(1))
        {
            info = new CardInfo(
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                !r.IsDBNull(5) && r.GetBoolean(5));
        }
        _cardCache[grpId] = info;
        return info;
    }

    public void Dispose() => _con.Dispose();
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CardDbTests"`
Expected: PASS, 5 tests.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: solution scaffold and read-only card database"
```

---

### Task 2: LogScanner

**Files:**
- Create: `src/MtgaPbp.Core/LogScanner.cs`, `tests/MtgaPbp.Tests/LogScannerTests.cs`

**Interfaces:**
- Consumes: `LogEnvelope` from Task 1.
- Produces: `LogScanner` with `static IEnumerable<LogEnvelope> Scan(string path, ScanStats stats)` and `sealed class ScanStats { public long NonJsonLines; public long MalformedLines; public long JsonLines; }`.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/LogScannerTests.cs`:

```csharp
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class LogScannerTests
{
    private string _path = null!;

    [TearDown]
    public void TearDown() { if (File.Exists(_path)) File.Delete(_path); }

    private string WriteLog(params string[] lines)
    {
        _path = Path.Combine(Path.GetTempPath(), $"log_{Guid.NewGuid():N}.log");
        File.WriteAllLines(_path, lines);
        return _path;
    }

    [Test]
    public void Scan_skips_non_json_lines_and_counts_them()
    {
        var p = WriteLog(
            "Mono path[0] = 'C:/whatever'",
            "[UnityCrossThreadLogger] noise",
            """{ "timestamp": "1786326812781", "greToClientEvent": { } }""");

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(stats.NonJsonLines, Is.EqualTo(2));
        Assert.That(stats.JsonLines, Is.EqualTo(1));
    }

    [Test]
    public void Scan_parses_string_epoch_timestamp_to_long()
    {
        var p = WriteLog("""{ "timestamp": "1786326812781", "a": 1 }""");
        var got = LogScanner.Scan(p, new ScanStats()).Single();
        Assert.That(got.TimestampMs, Is.EqualTo(1786326812781L));
    }

    [Test]
    public void Scan_counts_malformed_json_without_throwing()
    {
        var p = WriteLog(
            """{ "timestamp": "1", "ok": true }""",
            """{ "truncated": """);

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(stats.MalformedLines, Is.EqualTo(1));
    }

    [Test]
    public void Scan_records_one_based_line_numbers()
    {
        var p = WriteLog("noise", """{ "timestamp": "5" }""");
        Assert.That(LogScanner.Scan(p, new ScanStats()).Single().LineNumber, Is.EqualTo(2));
    }

    [Test]
    public void Scan_defaults_timestamp_to_zero_when_absent()
    {
        var p = WriteLog("""{ "noTimestamp": 1 }""");
        Assert.That(LogScanner.Scan(p, new ScanStats()).Single().TimestampMs, Is.EqualTo(0L));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~LogScannerTests"`
Expected: FAIL — `LogScanner` / `ScanStats` do not exist.

- [ ] **Step 3: Write LogScanner.cs**

```csharp
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed class ScanStats
{
    public long JsonLines;
    public long NonJsonLines;
    public long MalformedLines;
}

public static class LogScanner
{
    /// <summary>
    /// Streams parsed JSON envelopes from an Arena log. Non-JSON and malformed
    /// lines are counted and skipped — a log is untrusted input that changes
    /// with every Arena patch, so this must never throw.
    /// </summary>
    public static IEnumerable<LogEnvelope> Scan(string path, ScanStats stats)
    {
        using var reader = new StreamReader(path);
        long lineNo = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] != '{')
            {
                stats.NonJsonLines++;
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                stats.MalformedLines++;
                continue;
            }

            stats.JsonLines++;
            var root = doc.RootElement.Clone();
            doc.Dispose();
            yield return new LogEnvelope(lineNo, ReadTimestamp(root), root);
        }
    }

    private static long ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return 0;
        return ts.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(ts.GetString(), out var v) => v,
            JsonValueKind.Number when ts.TryGetInt64(out var v) => v,
            _ => 0
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LogScannerTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: streaming log scanner that never throws on bad input"
```

---

### Task 3: MatchSlicer

**Files:**
- Create: `src/MtgaPbp.Core/MatchSlicer.cs`, `tests/MtgaPbp.Tests/MatchSlicerTests.cs`

**Interfaces:**
- Consumes: `LogEnvelope`, `MatchSlice` from Task 1; `LogScanner` from Task 2.
- Produces: `MatchSlicer` with `static IReadOnlyList<MatchSlice> Slice(IEnumerable<LogEnvelope> envelopes)`.

Match ID appears in two places: `greToClientEvent.greToClientMessages[].gameStateMessage.gameInfo.matchID` and `matchGameRoomStateChangedEvent.gameRoomConfig.matchId`. Envelopes without a match ID are dropped. A match is `Incomplete` when no `finalMatchResult` was seen for it.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/MatchSlicerTests.cs`:

```csharp
using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class MatchSlicerTests
{
    private static LogEnvelope Env(long line, long ts, string json) =>
        new(line, ts, JsonDocument.Parse(json).RootElement.Clone());

    private static string GreWithMatch(string matchId) => $$"""
        { "timestamp": "1", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_GameStateMessage",
            "gameStateMessage": { "gameInfo": { "matchID": "{{matchId}}" } } } ] } }
        """;

    private static string RoomFinal(string matchId) => $$"""
        { "timestamp": "9", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
            "gameRoomConfig": { "matchId": "{{matchId}}" },
            "finalMatchResult": { "matchId": "{{matchId}}", "resultList": [
              { "scope": "MatchScope_Match", "winningTeamId": 2 } ] } } } }
        """;

    [Test]
    public void Slice_groups_envelopes_by_match_id()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, GreWithMatch("bbb")),
            Env(3, 300, GreWithMatch("aaa")),
        ]);

        Assert.That(slices.Select(s => s.MatchId), Is.EquivalentTo(new[] { "aaa", "bbb" }));
        Assert.That(slices.Single(s => s.MatchId == "aaa").RawLines, Has.Count.EqualTo(2));
    }

    [Test]
    public void Slice_handles_interleaved_matches()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 150, GreWithMatch("bbb")),
            Env(3, 200, GreWithMatch("aaa")),
            Env(4, 250, GreWithMatch("bbb")),
        ]);
        Assert.That(slices, Has.Count.EqualTo(2));
        Assert.That(slices.All(s => s.RawLines.Count == 2), Is.True);
    }

    [Test]
    public void Slice_reads_match_id_from_room_state_event()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, RoomFinal("ccc"))]);
        Assert.That(slices.Single().MatchId, Is.EqualTo("ccc"));
    }

    [Test]
    public void Slice_marks_match_complete_when_final_result_present()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 900, RoomFinal("aaa")),
        ]);
        Assert.That(slices.Single().Incomplete, Is.False);
    }

    [Test]
    public void Slice_marks_match_incomplete_when_log_was_truncated()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, GreWithMatch("aaa"))]);
        Assert.That(slices.Single().Incomplete, Is.True);
    }

    [Test]
    public void Slice_records_first_and_last_timestamps()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 555, GreWithMatch("aaa")),
        ]);
        var s = slices.Single();
        Assert.That(s.StartedAtMs, Is.EqualTo(100));
        Assert.That(s.EndedAtMs, Is.EqualTo(555));
    }

    [Test]
    public void Slice_drops_envelopes_with_no_match_id()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, """{ "timestamp": "1", "Courses": [] }""")]);
        Assert.That(slices, Is.Empty);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MatchSlicerTests"`
Expected: FAIL — `MatchSlicer` does not exist.

- [ ] **Step 3: Write MatchSlicer.cs**

```csharp
using System.Text.Json;

namespace MtgaPbp.Core;

public static class MatchSlicer
{
    private sealed class Builder
    {
        public long Start = long.MaxValue;
        public long End = long.MinValue;
        public bool SawFinalResult;
        public readonly List<string> Lines = [];
    }

    public static IReadOnlyList<MatchSlice> Slice(IEnumerable<LogEnvelope> envelopes)
    {
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var env in envelopes)
        {
            var matchId = ExtractMatchId(env.Root);
            if (matchId is null) continue;

            if (!builders.TryGetValue(matchId, out var b))
            {
                b = new Builder();
                builders[matchId] = b;
                order.Add(matchId);
            }

            b.Lines.Add(env.Root.GetRawText());
            if (env.TimestampMs > 0)
            {
                if (env.TimestampMs < b.Start) b.Start = env.TimestampMs;
                if (env.TimestampMs > b.End) b.End = env.TimestampMs;
            }
            if (HasFinalResult(env.Root)) b.SawFinalResult = true;
        }

        return order.Select(id =>
        {
            var b = builders[id];
            return new MatchSlice(
                id,
                b.Start == long.MaxValue ? 0 : b.Start,
                b.End == long.MinValue ? 0 : b.End,
                b.Lines,
                Incomplete: !b.SawFinalResult);
        }).ToList();
    }

    private static string? ExtractMatchId(JsonElement root)
    {
        if (root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
            room.TryGetProperty("gameRoomInfo", out var info) &&
            info.TryGetProperty("gameRoomConfig", out var cfg) &&
            cfg.TryGetProperty("matchId", out var mid) &&
            mid.ValueKind == JsonValueKind.String)
            return mid.GetString();

        if (root.TryGetProperty("greToClientEvent", out var gre) &&
            gre.TryGetProperty("greToClientMessages", out var msgs) &&
            msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.TryGetProperty("gameStateMessage", out var gsm) &&
                    gsm.TryGetProperty("gameInfo", out var gi) &&
                    gi.TryGetProperty("matchID", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
        }
        return null;
    }

    private static bool HasFinalResult(JsonElement root) =>
        root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
        room.TryGetProperty("gameRoomInfo", out var info) &&
        info.TryGetProperty("finalMatchResult", out _);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MatchSlicerTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: slice log envelopes into per-match groups"
```

---

### Task 4: RawArchive

**Files:**
- Create: `src/MtgaPbp.Core/RawArchive.cs`, `tests/MtgaPbp.Tests/RawArchiveTests.cs`

**Interfaces:**
- Consumes: `MatchSlice` from Task 1.
- Produces: `RawArchive` with `RawArchive(string archiveRoot)`, `bool Contains(string matchId)`, `bool Write(MatchSlice slice)` (returns false when already present and complete), `IEnumerable<string> MatchIds()`, `IReadOnlyList<string> ReadLines(string matchId)`, `ArchiveEntry? Meta(string matchId)`; `sealed record ArchiveEntry(string MatchId, long StartedAtMs, long EndedAtMs, bool Incomplete)`.

An incomplete match may be overwritten by a later complete capture of the same match — that is how a match split across `Player-prev.log` and `Player.log` heals.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/RawArchiveTests.cs`:

```csharp
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RawArchiveTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"arch_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static MatchSlice Slice(string id, bool incomplete = false, params string[] lines) =>
        new(id, 100, 200, lines.Length == 0 ? ["""{"a":1}"""] : lines, incomplete);

    [Test]
    public void Write_then_ReadLines_round_trips_content()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", false, """{"x":1}""", """{"y":2}"""));

        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"x":1}""", """{"y":2}""" }));
    }

    [Test]
    public void Write_is_idempotent_for_a_complete_match()
    {
        var a = new RawArchive(_root);
        Assert.That(a.Write(Slice("m1")), Is.True);
        Assert.That(a.Write(Slice("m1")), Is.False, "second write should be skipped");
    }

    [Test]
    public void Write_overwrites_an_incomplete_match_with_a_complete_one()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: true, """{"partial":1}"""));
        Assert.That(a.Write(Slice("m1", incomplete: false, """{"full":1}""")), Is.True);
        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"full":1}""" }));
        Assert.That(a.Meta("m1")!.Incomplete, Is.False);
    }

    [Test]
    public void Ledger_survives_reopening_the_archive()
    {
        new RawArchive(_root).Write(Slice("m1"));
        var reopened = new RawArchive(_root);
        Assert.That(reopened.Contains("m1"), Is.True);
        Assert.That(reopened.MatchIds(), Is.EquivalentTo(new[] { "m1" }));
    }

    [Test]
    public void Meta_records_timestamps()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        var meta = a.Meta("m1")!;
        Assert.That(meta.StartedAtMs, Is.EqualTo(100));
        Assert.That(meta.EndedAtMs, Is.EqualTo(200));
    }

    [Test]
    public void Written_payload_is_gzip_compressed()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        var file = Path.Combine(_root, "raw", "m1.json.gz");
        Assert.That(File.Exists(file), Is.True);
        using var fs = File.OpenRead(file);
        Assert.That(fs.ReadByte(), Is.EqualTo(0x1f));
        Assert.That(fs.ReadByte(), Is.EqualTo(0x8b));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RawArchiveTests"`
Expected: FAIL — `RawArchive` does not exist.

- [ ] **Step 3: Write RawArchive.cs**

```csharp
using System.IO.Compression;
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record ArchiveEntry(string MatchId, long StartedAtMs, long EndedAtMs, bool Incomplete);

public sealed class RawArchive
{
    private readonly string _rawDir;
    private readonly string _ledgerPath;
    private readonly Dictionary<string, ArchiveEntry> _ledger;

    public RawArchive(string archiveRoot)
    {
        _rawDir = Path.Combine(archiveRoot, "raw");
        Directory.CreateDirectory(_rawDir);
        _ledgerPath = Path.Combine(archiveRoot, "index.json");
        _ledger = File.Exists(_ledgerPath)
            ? JsonSerializer.Deserialize<Dictionary<string, ArchiveEntry>>(
                  File.ReadAllText(_ledgerPath)) ?? []
            : [];
    }

    public bool Contains(string matchId) => _ledger.ContainsKey(matchId);

    public IEnumerable<string> MatchIds() => _ledger.Keys;

    public ArchiveEntry? Meta(string matchId) =>
        _ledger.TryGetValue(matchId, out var e) ? e : null;

    /// <summary>
    /// Writes a match. Returns false when an equally-or-more complete copy is
    /// already archived. An incomplete entry is replaced by a complete one so a
    /// match split across Player-prev.log and Player.log heals on the next run.
    /// </summary>
    public bool Write(MatchSlice slice)
    {
        if (_ledger.TryGetValue(slice.MatchId, out var existing) &&
            (!existing.Incomplete || slice.Incomplete))
            return false;

        var path = Path.Combine(_rawDir, $"{slice.MatchId}.json.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz))
        {
            foreach (var line in slice.RawLines) w.WriteLine(line);
        }

        _ledger[slice.MatchId] =
            new ArchiveEntry(slice.MatchId, slice.StartedAtMs, slice.EndedAtMs, slice.Incomplete);
        SaveLedger();
        return true;
    }

    public IReadOnlyList<string> ReadLines(string matchId)
    {
        var path = Path.Combine(_rawDir, $"{matchId}.json.gz");
        if (!File.Exists(path)) return [];
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        var lines = new List<string>();
        while (r.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private void SaveLedger() =>
        File.WriteAllText(_ledgerPath,
            JsonSerializer.Serialize(_ledger, new JsonSerializerOptions { WriteIndented = true }));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RawArchiveTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: gzip match archive with idempotent dedupe ledger"
```

---

### Task 5: GameStateTracker

**Files:**
- Create: `src/MtgaPbp.Core/GameStateTracker.cs`, `tests/MtgaPbp.Tests/GameStateTrackerTests.cs`

**Interfaces:**
- Consumes: `ICardDb` from Task 1.
- Produces: `TrackedObject` (mutable class, fields listed below); `GameStateTracker` with `GameStateTracker(ICardDb cards)`, `void Apply(JsonElement gameStateMessage)`, `int Resolve(int instanceId)`, `TrackedObject? Get(int instanceId)`, `string NameOf(int instanceId)`, `string SeatName(int seat)`, and properties `Turn`, `ActiveSeat`, `Phase`, `Step`, `GameNumber`, `IReadOnlyDictionary<int,int> Life`, `IReadOnlyDictionary<int,TrackedObject> Objects`, `IReadOnlyDictionary<int,string> ZoneTypes`, `int LocalSeat`.

`AnnotationType_ObjectIdChanged` is the highest-risk piece: an object's `instanceId` changes on zone change, and `Resolve` must follow multi-hop chains so a card reads as one entity from cast to graveyard.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/GameStateTrackerTests.cs`:

```csharp
using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class GameStateTrackerTests
{
    private sealed class FakeCardDb : ICardDb
    {
        public string? NameForLocId(int locId) => locId switch
        {
            648 => "Plains",
            44198 => "Temple of Plenty",
            _ => null
        };
        public CardInfo? CardForGrpId(int grpId) => grpId switch
        {
            94131 => new CardInfo(94131, "Temple of Plenty", "5", null, null, false),
            _ => null
        };
    }

    private static GameStateTracker NewTracker() => new(new FakeCardDb());
    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public void Apply_full_state_records_players_life_and_turn()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameInfo": { "gameNumber": 1 },
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 },
                       { "systemSeatNumber": 2, "lifeTotal": 20 } ],
          "turnInfo": { "turnNumber": 1, "activePlayer": 1, "phase": 1, "step": 1 } }
        """));

        Assert.That(t.Life[1], Is.EqualTo(20));
        Assert.That(t.Turn, Is.EqualTo(1));
        Assert.That(t.ActiveSeat, Is.EqualTo(1));
        Assert.That(t.GameNumber, Is.EqualTo(1));
    }

    [Test]
    public void Apply_diff_updates_only_supplied_fields()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 } ],
          "turnInfo": { "turnNumber": 1, "activePlayer": 1 } }
        """));
        t.Apply(Msg("""
        { "type": "GameStateType_Diff",
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 18 } ] }
        """));

        Assert.That(t.Life[1], Is.EqualTo(18));
        Assert.That(t.Turn, Is.EqualTo(1), "turn must survive a diff that omits turnInfo");
    }

    [Test]
    public void NameOf_prefers_the_objects_own_name_loc_id()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 245, "grpId": 96179, "name": 648,
            "type": "GameObjectType_Card", "ownerSeatId": 1, "controllerSeatId": 1 } ] }
        """));
        Assert.That(t.NameOf(245), Is.EqualTo("Plains"));
    }

    [Test]
    public void NameOf_falls_back_to_source_card_for_an_ability()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 433, "grpId": 176406, "type": "GameObjectType_Ability",
            "objectSourceGrpId": 94131, "ownerSeatId": 1, "controllerSeatId": 1 } ] }
        """));
        Assert.That(t.NameOf(433), Is.EqualTo("Temple of Plenty's ability"));
    }

    [Test]
    public void NameOf_degrades_to_grpid_when_unresolvable()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 9, "grpId": 55555, "type": "GameObjectType_Card" } ] }
        """));
        Assert.That(t.NameOf(9), Is.EqualTo("Card #55555"));
    }

    [Test]
    public void Resolve_follows_a_single_id_change()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 305, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 110, "affectedIds": [ 305 ],
            "type": [ "AnnotationType_ObjectIdChanged" ],
            "details": [
              { "key": "orig_id", "valueInt32": [ 305 ] },
              { "key": "new_id",  "valueInt32": [ 430 ] } ] } ] }
        """));
        Assert.That(t.Resolve(305), Is.EqualTo(430));
    }

    [Test]
    public void Resolve_follows_a_multi_hop_chain()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 100 ] },
            { "key": "new_id",  "valueInt32": [ 200 ] } ] },
          { "id": 2, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 200 ] },
            { "key": "new_id",  "valueInt32": [ 300 ] } ] } ] }
        """));
        Assert.That(t.Resolve(100), Is.EqualTo(300));
        Assert.That(t.Resolve(200), Is.EqualTo(300));
        Assert.That(t.Resolve(300), Is.EqualTo(300));
    }

    [Test]
    public void Resolve_survives_a_cyclic_alias_without_hanging()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 10 ] },
            { "key": "new_id",  "valueInt32": [ 20 ] } ] },
          { "id": 2, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 20 ] },
            { "key": "new_id",  "valueInt32": [ 10 ] } ] } ] }
        """));
        Assert.That(t.Resolve(10), Is.AnyOf(10, 20));
    }

    [Test]
    public void NameOf_follows_alias_so_a_card_keeps_its_name_across_zones()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 430, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ],
            "details": [
              { "key": "orig_id", "valueInt32": [ 305 ] },
              { "key": "new_id",  "valueInt32": [ 430 ] } ] } ] }
        """));
        Assert.That(t.NameOf(305), Is.EqualTo("Plains"));
    }

    [Test]
    public void Apply_tracks_object_stats_and_tapped_state()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 50, "grpId": 96179, "name": 648, "type": "GameObjectType_Card",
            "power": 3, "toughness": 4, "damage": 1, "isTapped": true,
            "ownerSeatId": 2, "controllerSeatId": 2, "zoneId": 28 } ] }
        """));
        var o = t.Get(50)!;
        Assert.That(o.Power, Is.EqualTo(3));
        Assert.That(o.Toughness, Is.EqualTo(4));
        Assert.That(o.Damage, Is.EqualTo(1));
        Assert.That(o.IsTapped, Is.True);
        Assert.That(o.ControllerSeat, Is.EqualTo(2));
    }

    [Test]
    public void Apply_records_zone_types_by_id()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "zones": [
          { "zoneId": 28, "type": "ZoneType_Battlefield" },
          { "zoneId": 35, "type": "ZoneType_Hand" } ] }
        """));
        Assert.That(t.ZoneTypes[28], Is.EqualTo("ZoneType_Battlefield"));
        Assert.That(t.ZoneTypes[35], Is.EqualTo("ZoneType_Hand"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GameStateTrackerTests"`
Expected: FAIL — `GameStateTracker` / `TrackedObject` do not exist.

- [ ] **Step 3: Write GameStateTracker.cs**

```csharp
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed class TrackedObject
{
    public int InstanceId;
    public int GrpId;
    public int? NameLocId;
    public string Type = "";
    public int OwnerSeat;
    public int ControllerSeat;
    public int ZoneId;
    public int? Power;
    public int? Toughness;
    public int Damage;
    public bool IsTapped;
    public int? Loyalty;
    public int? ObjectSourceGrpId;
    public readonly Dictionary<int, int> Counters = [];
}

public sealed class GameStateTracker(ICardDb cards)
{
    private readonly Dictionary<int, TrackedObject> _objects = [];
    private readonly Dictionary<int, int> _alias = [];   // old id -> new id
    private readonly Dictionary<int, int> _life = [];
    private readonly Dictionary<int, string> _zoneTypes = [];

    public int Turn { get; private set; }
    public int ActiveSeat { get; private set; }
    public int Phase { get; private set; }
    public int Step { get; private set; }
    public int GameNumber { get; private set; } = 1;
    public int LocalSeat { get; set; }

    public IReadOnlyDictionary<int, int> Life => _life;
    public IReadOnlyDictionary<int, TrackedObject> Objects => _objects;
    public IReadOnlyDictionary<int, string> ZoneTypes => _zoneTypes;

    public void Apply(JsonElement gsm)
    {
        if (gsm.TryGetProperty("gameInfo", out var gi) &&
            gi.TryGetProperty("gameNumber", out var gn) && gn.TryGetInt32(out var gnv))
            GameNumber = gnv;

        if (gsm.TryGetProperty("turnInfo", out var ti))
        {
            if (ti.TryGetProperty("turnNumber", out var v) && v.TryGetInt32(out var tn)) Turn = tn;
            if (ti.TryGetProperty("activePlayer", out v) && v.TryGetInt32(out var ap)) ActiveSeat = ap;
            if (ti.TryGetProperty("phase", out v) && v.TryGetInt32(out var ph)) Phase = ph;
            if (ti.TryGetProperty("step", out v) && v.TryGetInt32(out var st)) Step = st;
        }

        if (gsm.TryGetProperty("players", out var players) &&
            players.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in players.EnumerateArray())
            {
                if (p.TryGetProperty("systemSeatNumber", out var s) && s.TryGetInt32(out var seat) &&
                    p.TryGetProperty("lifeTotal", out var l) && l.TryGetInt32(out var life))
                    _life[seat] = life;
            }
        }

        if (gsm.TryGetProperty("zones", out var zones) && zones.ValueKind == JsonValueKind.Array)
        {
            foreach (var z in zones.EnumerateArray())
            {
                if (z.TryGetProperty("zoneId", out var zi) && zi.TryGetInt32(out var zid) &&
                    z.TryGetProperty("type", out var zt) && zt.ValueKind == JsonValueKind.String)
                    _zoneTypes[zid] = zt.GetString()!;
            }
        }

        if (gsm.TryGetProperty("gameObjects", out var objs) && objs.ValueKind == JsonValueKind.Array)
            foreach (var go in objs.EnumerateArray()) UpsertObject(go);

        // Aliases must be applied before EventExtractor reads this message's annotations.
        if (gsm.TryGetProperty("annotations", out var anns) && anns.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in anns.EnumerateArray())
            {
                if (!HasType(a, "AnnotationType_ObjectIdChanged")) continue;
                var orig = DetailInt(a, "orig_id");
                var next = DetailInt(a, "new_id");
                if (orig is { } o && next is { } n && o != n) _alias[o] = n;
            }
        }
    }

    private void UpsertObject(JsonElement go)
    {
        if (!go.TryGetProperty("instanceId", out var idEl) || !idEl.TryGetInt32(out var id)) return;

        if (!_objects.TryGetValue(id, out var obj))
            _objects[id] = obj = new TrackedObject { InstanceId = id };

        if (go.TryGetProperty("grpId", out var v) && v.TryGetInt32(out var grp)) obj.GrpId = grp;
        if (go.TryGetProperty("name", out v) && v.TryGetInt32(out var nm)) obj.NameLocId = nm;
        if (go.TryGetProperty("type", out v) && v.ValueKind == JsonValueKind.String)
            obj.Type = v.GetString()!;
        if (go.TryGetProperty("ownerSeatId", out v) && v.TryGetInt32(out var os)) obj.OwnerSeat = os;
        if (go.TryGetProperty("controllerSeatId", out v) && v.TryGetInt32(out var cs))
            obj.ControllerSeat = cs;
        if (go.TryGetProperty("zoneId", out v) && v.TryGetInt32(out var zi)) obj.ZoneId = zi;
        if (go.TryGetProperty("power", out v)) obj.Power = ReadStat(v);
        if (go.TryGetProperty("toughness", out v)) obj.Toughness = ReadStat(v);
        if (go.TryGetProperty("damage", out v) && v.TryGetInt32(out var dmg)) obj.Damage = dmg;
        if (go.TryGetProperty("isTapped", out v)) obj.IsTapped = v.ValueKind == JsonValueKind.True;
        if (go.TryGetProperty("loyalty", out v)) obj.Loyalty = ReadStat(v);
        if (go.TryGetProperty("objectSourceGrpId", out v) && v.TryGetInt32(out var src))
            obj.ObjectSourceGrpId = src;
    }

    /// <summary>power/toughness arrive either as a number or as { "value": n }.</summary>
    private static int? ReadStat(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("value", out var v) && v.TryGetInt32(out var vn)) return vn;
        return null;
    }

    /// <summary>Follows the id-change chain to the current id. Cycle-safe.</summary>
    public int Resolve(int instanceId)
    {
        var seen = new HashSet<int>();
        var cur = instanceId;
        while (_alias.TryGetValue(cur, out var next) && seen.Add(cur)) cur = next;
        return cur;
    }

    public TrackedObject? Get(int instanceId)
    {
        var id = Resolve(instanceId);
        if (_objects.TryGetValue(id, out var o)) return o;
        return _objects.TryGetValue(instanceId, out var orig) ? orig : null;
    }

    public string NameOf(int instanceId)
    {
        var o = Get(instanceId);
        if (o is null) return $"#{instanceId}";

        if (o.NameLocId is { } loc && cards.NameForLocId(loc) is { } byLoc) return byLoc;
        if (cards.CardForGrpId(o.GrpId) is { } card) return card.Name;
        if (o.ObjectSourceGrpId is { } srcGrp && cards.CardForGrpId(srcGrp) is { } src)
            return $"{src.Name}'s ability";
        return $"Card #{o.GrpId}";
    }

    public string SeatName(int seat) => seat == LocalSeat ? "You" : "Opponent";

    internal static bool HasType(JsonElement annotation, string type)
    {
        if (!annotation.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var x in t.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString() == type) return true;
        return false;
    }

    internal static int? DetailInt(JsonElement annotation, string key)
    {
        if (!annotation.TryGetProperty("details", out var ds) || ds.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var d in ds.EnumerateArray())
        {
            if (!d.TryGetProperty("key", out var k) || k.GetString() != key) continue;
            if (d.TryGetProperty("valueInt32", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var n in v.EnumerateArray())
                    if (n.TryGetInt32(out var iv)) return iv;
        }
        return null;
    }

    internal static string? DetailString(JsonElement annotation, string key)
    {
        if (!annotation.TryGetProperty("details", out var ds) || ds.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var d in ds.EnumerateArray())
        {
            if (!d.TryGetProperty("key", out var k) || k.GetString() != key) continue;
            if (d.TryGetProperty("valueString", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var s in v.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String) return s.GetString();
        }
        return null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GameStateTrackerTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: game state tracker with cycle-safe object id aliasing"
```

---

### Task 6: EventExtractor

**Files:**
- Create: `src/MtgaPbp.Core/EventExtractor.cs`, `tests/MtgaPbp.Tests/EventExtractorTests.cs`

**Interfaces:**
- Consumes: `GameEvent`, `EventKind`, `ICardDb`, `GameStateTracker`.
- Produces: `Transcript` record and `EventExtractor` with `EventExtractor(ICardDb cards)` and `Transcript Extract(string matchId, IReadOnlyList<string> rawLines)`.

```csharp
public sealed record PlayerInfo(int Seat, string UserId, string ScreenName, string Platform);
public sealed record Transcript(
    string MatchId, long StartedAtMs, long EndedAtMs, string EventName,
    PlayerInfo? You, PlayerInfo? Opponent,
    int? WinningTeamId, int GamesWon, int GamesLost, bool Incomplete,
    IReadOnlyList<GameEvent> Events,
    IReadOnlyDictionary<string,int> UnknownAnnotations,
    IReadOnlySet<string> CardsSeen);
```

Local seat resolution order (per match, never carried across): first `MulliganReq.systemSeatIds`, then first `ActionsAvailableReq.systemSeatIds`.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/EventExtractorTests.cs`:

```csharp
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class EventExtractorTests
{
    private sealed class FakeCardDb : ICardDb
    {
        public string? NameForLocId(int locId) => locId switch
        {
            648 => "Plains",
            1000 => "Lightning Bolt",
            1001 => "Llanowar Elves",
            _ => null
        };
        public CardInfo? CardForGrpId(int grpId) => null;
    }

    private static Transcript Run(params string[] lines) =>
        new EventExtractor(new FakeCardDb()).Extract("m1", lines);

    private const string RoomLine = """
    { "timestamp": "1000", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
        "gameRoomConfig": { "matchId": "m1", "reservedPlayers": [
          { "userId": "ME", "playerName": "PlayerOne", "systemSeatId": 1,
            "teamId": 1, "platformId": "SteamWindows", "eventId": "Ladder" },
          { "userId": "THEM", "playerName": "PlayerTwo", "systemSeatId": 2,
            "teamId": 2, "platformId": "iPhone", "eventId": "Ladder" } ] } } } }
    """;

    private const string MulliganLine = """
    { "timestamp": "1001", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_MulliganReq", "systemSeatIds": [ 1 ] } ] } }
    """;

    private static string Gre(string gsmBody) => $$"""
    { "timestamp": "1002", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_GameStateMessage", "gameStateMessage": {{gsmBody}} } ] } }
    """;

    [Test]
    public void Extract_reads_player_names_and_event_name()
    {
        var t = Run(RoomLine, MulliganLine);
        Assert.That(t.You!.ScreenName, Is.EqualTo("PlayerOne"));
        Assert.That(t.Opponent!.ScreenName, Is.EqualTo("PlayerTwo"));
        Assert.That(t.EventName, Is.EqualTo("Ladder"));
    }

    [Test]
    public void Extract_resolves_local_seat_from_mulligan_request()
    {
        var t = Run(RoomLine, MulliganLine);
        Assert.That(t.You!.Seat, Is.EqualTo(1));
        Assert.That(t.Opponent!.Seat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_resolves_local_seat_from_actions_available_when_no_mulligan()
    {
        var actions = """
        { "timestamp": "1001", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_ActionsAvailableReq", "systemSeatIds": [ 2 ] } ] } }
        """;
        var t = Run(RoomLine, actions);
        Assert.That(t.You!.Seat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_emits_land_played_from_zone_transfer_category()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "turnInfo": { "turnNumber": 1, "activePlayer": 1 },
          "gameObjects": [ { "instanceId": 430, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card", "controllerSeatId": 1 } ],
          "annotations": [ { "id": 111, "affectedIds": [ 430 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "zone_src",  "valueInt32": [ 35 ] },
              { "key": "zone_dest", "valueInt32": [ 28 ] },
              { "key": "category", "valueString": [ "PlayLand" ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.LandPlayed);
        Assert.That(e.SourceName, Is.EqualTo("Plains"));
        Assert.That(e.Turn, Is.EqualTo(1));
    }

    [Test]
    public void Extract_maps_each_zone_transfer_category_to_its_kind()
    {
        foreach (var (category, expected) in new[]
        {
            ("CastSpell", EventKind.SpellCast),
            ("Resolve",   EventKind.Resolved),
            ("Draw",      EventKind.Drew),
            ("Discard",   EventKind.Discarded),
            ("Destroy",   EventKind.Destroyed),
            ("Sacrifice", EventKind.Sacrificed),
            ("Exile",     EventKind.Exiled),
            ("Return",    EventKind.Returned),
            ("Countered", EventKind.Countered),
            ("SBA_Damage", EventKind.StateBasedAction),
            ("Put",       EventKind.ZoneMove),
        })
        {
            var t = Run(RoomLine, MulliganLine, Gre($$"""
            { "type": "GameStateType_Full",
              "gameObjects": [ { "instanceId": 1, "grpId": 1, "name": 1000,
                                 "type": "GameObjectType_Card" } ],
              "annotations": [ { "id": 1, "affectedIds": [ 1 ],
                "type": [ "AnnotationType_ZoneTransfer" ], "details": [
                  { "key": "category", "valueString": [ "{{category}}" ] } ] } ] }
            """));
            Assert.That(t.Events.Select(x => x.Kind), Does.Contain(expected),
                $"category {category} should map to {expected}");
        }
    }

    [Test]
    public void Extract_emits_damage_with_source_and_amount()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 436, "grpId": 5, "name": 1000,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 248, "affectorId": 436, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_DamageDealt" ], "details": [
              { "key": "damage", "valueInt32": [ 2 ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Damage);
        Assert.That(e.SourceName, Is.EqualTo("Lightning Bolt"));
        Assert.That(e.Amount, Is.EqualTo(2));
        Assert.That(e.TargetSeat, Is.EqualTo(1), "affectedIds 1 and 2 are player seats");
    }

    [Test]
    public void Extract_emits_life_change_with_delta()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 252, "affectedIds": [ 1 ], "type": [ "AnnotationType_ModifiedLife" ],
            "details": [ { "key": "life", "valueInt32": [ -2 ] } ] } ] }
        """));
        var e = t.Events.Single(x => x.Kind == EventKind.LifeChanged);
        Assert.That(e.Amount, Is.EqualTo(-2));
        Assert.That(e.TargetSeat, Is.EqualTo(1));
    }

    [Test]
    public void Extract_emits_turn_start()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 3, "activePlayer": 2 },
          "annotations": [ { "id": 106, "affectorId": 2, "affectedIds": [ 2 ],
            "type": [ "AnnotationType_NewTurnStarted" ] } ] }
        """));
        var e = t.Events.Single(x => x.Kind == EventKind.TurnStart);
        Assert.That(e.Turn, Is.EqualTo(3));
        Assert.That(e.ActorSeat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_records_unknown_annotations_without_dropping_them()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_SomethingBrandNew" ] } ] }
        """));
        Assert.That(t.UnknownAnnotations["AnnotationType_SomethingBrandNew"], Is.EqualTo(1));
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Unknown), Is.True);
    }

    [Test]
    public void Extract_reads_final_result_and_game_record()
    {
        var final = """
        { "timestamp": "2000", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
            "gameRoomConfig": { "matchId": "m1" },
            "finalMatchResult": { "matchId": "m1", "resultList": [
              { "scope": "MatchScope_Game",  "winningTeamId": 1 },
              { "scope": "MatchScope_Game",  "winningTeamId": 2 },
              { "scope": "MatchScope_Game",  "winningTeamId": 1 },
              { "scope": "MatchScope_Match", "winningTeamId": 1 } ] } } } }
        """;
        var t = Run(RoomLine, MulliganLine, final);
        Assert.That(t.WinningTeamId, Is.EqualTo(1));
        Assert.That(t.GamesWon, Is.EqualTo(2));
        Assert.That(t.GamesLost, Is.EqualTo(1));
        Assert.That(t.Events.Any(x => x.Kind == EventKind.GameEnd), Is.True);
    }

    [Test]
    public void Extract_collects_card_names_for_the_search_index()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 1, "grpId": 1, "name": 1000,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "CastSpell" ] } ] } ] }
        """));
        Assert.That(t.CardsSeen, Does.Contain("Lightning Bolt"));
    }

    [Test]
    public void Extract_never_throws_on_malformed_annotations()
    {
        Assert.DoesNotThrow(() => Run(RoomLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1 },
          { "id": 2, "type": "not-an-array" },
          { "id": 3, "type": [ "AnnotationType_ZoneTransfer" ] } ] }
        """)));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EventExtractorTests"`
Expected: FAIL — `EventExtractor` / `Transcript` / `PlayerInfo` do not exist.

- [ ] **Step 3: Write EventExtractor.cs**

```csharp
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record PlayerInfo(int Seat, string UserId, string ScreenName, string Platform);

public sealed record Transcript(
    string MatchId, long StartedAtMs, long EndedAtMs, string EventName,
    PlayerInfo? You, PlayerInfo? Opponent,
    int? WinningTeamId, int GamesWon, int GamesLost, bool Incomplete,
    IReadOnlyList<GameEvent> Events,
    IReadOnlyDictionary<string, int> UnknownAnnotations,
    IReadOnlySet<string> CardsSeen);

public sealed class EventExtractor(ICardDb cards)
{
    private static readonly Dictionary<string, EventKind> CategoryKinds = new(StringComparer.Ordinal)
    {
        ["PlayLand"] = EventKind.LandPlayed,
        ["CastSpell"] = EventKind.SpellCast,
        ["Resolve"] = EventKind.Resolved,
        ["Countered"] = EventKind.Countered,
        ["Draw"] = EventKind.Drew,
        ["Discard"] = EventKind.Discarded,
        ["Destroy"] = EventKind.Destroyed,
        ["Sacrifice"] = EventKind.Sacrificed,
        ["Exile"] = EventKind.Exiled,
        ["Return"] = EventKind.Returned,
    };

    private static readonly Dictionary<string, EventKind> SimpleAnnotationKinds =
        new(StringComparer.Ordinal)
        {
            ["AnnotationType_NewTurnStarted"] = EventKind.TurnStart,
            ["AnnotationType_DamageDealt"] = EventKind.Damage,
            ["AnnotationType_ModifiedLife"] = EventKind.LifeChanged,
            ["AnnotationType_TokenCreated"] = EventKind.TokenCreated,
            ["AnnotationType_CounterAdded"] = EventKind.CounterChanged,
            ["AnnotationType_CounterRemoved"] = EventKind.CounterChanged,
            ["AnnotationType_Scry"] = EventKind.Scry,
            ["AnnotationType_RevealedCardCreated"] = EventKind.Revealed,
            ["AnnotationType_ManaPaid"] = EventKind.ManaPaid,
            ["AnnotationType_PhaseOrStepModified"] = EventKind.PhaseChange,
        };

    /// <summary>Annotations that carry no transcript value and are silently dropped.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "AnnotationType_ObjectIdChanged",       // consumed by the tracker as aliasing
        "AnnotationType_AbilityInstanceCreated",
        "AnnotationType_AbilityInstanceDeleted",
        "AnnotationType_TappedUntappedPermanent",
        "AnnotationType_UserActionTaken",
        "AnnotationType_ResolutionStart",
        "AnnotationType_ResolutionComplete",
        "AnnotationType_LayeredEffectCreated",
        "AnnotationType_LayeredEffectDestroyed",
        "AnnotationType_PowerToughnessModCreated",
        "AnnotationType_PlayerSelectingTargets",
        "AnnotationType_PlayerSubmittedTargets", // carries no target ids — see spec
        "AnnotationType_ShouldntPlay",
        "AnnotationType_MultistepEffectStarted",
        "AnnotationType_MultistepEffectComplete",
        "AnnotationType_SyntheticEvent",
        "AnnotationType_TokenDeleted",
        "AnnotationType_GainDesignation",
        "AnnotationType_AttachmentCreated",
        "AnnotationType_ChoiceResult",
        "AnnotationType_RevealedCardDeleted",
        "AnnotationType_DisqualifiedEffect",
        "AnnotationType_Shuffle",
    };

    public Transcript Extract(string matchId, IReadOnlyList<string> rawLines)
    {
        var tracker = new GameStateTracker(cards);
        var events = new List<GameEvent>();
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var cardsSeen = new HashSet<string>(StringComparer.Ordinal);
        var seatMeta = new Dictionary<int, PlayerInfo>();

        long started = 0, ended = 0;
        string eventName = "";
        int? localSeat = null, fallbackSeat = null, winningTeam = null;
        int gamesForTeam1 = 0, gamesForTeam2 = 0;
        var sawFinal = false;
        var seq = 0;

        foreach (var raw in rawLines)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { continue; }

            var ts = ReadTimestamp(root);
            if (ts > 0) { if (started == 0) started = ts; ended = ts; }

            if (root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
                room.TryGetProperty("gameRoomInfo", out var info))
            {
                ReadRoom(info, seatMeta, ref eventName);
                if (info.TryGetProperty("finalMatchResult", out var fmr))
                {
                    sawFinal = true;
                    ReadResults(fmr, ref winningTeam, ref gamesForTeam1, ref gamesForTeam2);
                }
            }

            if (!root.TryGetProperty("greToClientEvent", out var gre) ||
                !gre.TryGetProperty("greToClientMessages", out var msgs) ||
                msgs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var m in msgs.EnumerateArray())
            {
                var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type is "GREMessageType_MulliganReq" && localSeat is null)
                    localSeat = FirstSeat(m);
                else if (type is "GREMessageType_ActionsAvailableReq" && fallbackSeat is null)
                    fallbackSeat = FirstSeat(m);

                if (!m.TryGetProperty("gameStateMessage", out var gsm)) continue;

                tracker.Apply(gsm);
                if (gsm.TryGetProperty("annotations", out var anns) &&
                    anns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in anns.EnumerateArray())
                        EmitFor(a, tracker, ts, ref seq, events, unknown, cardsSeen);
                }
            }
        }

        var you = (localSeat ?? fallbackSeat) is { } seat && seatMeta.TryGetValue(seat, out var y)
            ? y : null;
        var opp = you is null ? null : seatMeta.Values.FirstOrDefault(p => p.Seat != you.Seat);
        tracker.LocalSeat = you?.Seat ?? 0;

        var yourTeam = you?.Seat;   // teamId equals seat in every observed match
        var won = yourTeam == 1 ? gamesForTeam1 : gamesForTeam2;
        var lost = yourTeam == 1 ? gamesForTeam2 : gamesForTeam1;

        if (sawFinal)
        {
            events.Add(new GameEvent
            {
                Seq = seq++,
                TimestampMs = ended,
                Kind = EventKind.GameEnd,
                Amount = winningTeam ?? 0,
                Detail = winningTeam is null ? null
                    : winningTeam == yourTeam ? "You win the match" : "Opponent wins the match"
            });
        }

        return new Transcript(
            matchId, started, ended, eventName, you, opp,
            winningTeam, won, lost, Incomplete: !sawFinal,
            events, unknown, cardsSeen);
    }

    private void EmitFor(
        JsonElement a, GameStateTracker tracker, long ts, ref int seq,
        List<GameEvent> events, Dictionary<string, int> unknown, HashSet<string> cardsSeen)
    {
        if (!a.TryGetProperty("type", out var types) || types.ValueKind != JsonValueKind.Array)
            return;

        foreach (var typeEl in types.EnumerateArray())
        {
            var type = typeEl.GetString();
            if (type is null || Ignored.Contains(type)) continue;

            GameEvent? ev = null;

            if (type == "AnnotationType_ZoneTransfer")
            {
                var category = GameStateTracker.DetailString(a, "category") ?? "";
                var kind = CategoryKinds.TryGetValue(category, out var k) ? k
                    : category.StartsWith("SBA_", StringComparison.Ordinal)
                        ? EventKind.StateBasedAction
                        : EventKind.ZoneMove;

                var objId = FirstAffected(a);
                var name = objId is { } oid ? tracker.NameOf(oid) : null;
                if (name is not null && !name.StartsWith('#')) cardsSeen.Add(name);

                ev = Base(tracker, ts, kind) with
                {
                    SourceInstanceId = objId,
                    SourceName = name,
                    ActorSeat = objId is { } id2 ? tracker.Get(id2)?.ControllerSeat : null,
                    Detail = category
                };
            }
            else if (SimpleAnnotationKinds.TryGetValue(type, out var simple))
            {
                var affector = a.TryGetProperty("affectorId", out var af) && af.TryGetInt32(out var afv)
                    ? afv : (int?)null;
                var affected = FirstAffected(a);

                var sourceName = affector is { } s && s > 2 ? tracker.NameOf(s) : null;
                if (sourceName is not null && !sourceName.StartsWith('#')) cardsSeen.Add(sourceName);

                ev = Base(tracker, ts, simple) with
                {
                    ActorSeat = affector is { } s2 && s2 <= 2 ? s2 : null,
                    SourceInstanceId = affector,
                    SourceName = sourceName,
                    // Seats 1 and 2 are players; anything larger is an object instance id.
                    TargetSeat = affected is { } t2 && t2 <= 2 ? t2 : null,
                    TargetInstanceId = affected is { } t3 && t3 > 2 ? t3 : null,
                    TargetName = affected is { } t4 && t4 > 2 ? tracker.NameOf(t4) : null,
                    Amount = AmountFor(type, a)
                };
            }
            else
            {
                unknown[type] = unknown.GetValueOrDefault(type) + 1;
                ev = Base(tracker, ts, EventKind.Unknown) with { RawType = type };
            }

            if (ev is not null) events.Add(ev with { Seq = seq++ });
        }
    }

    private static int AmountFor(string type, JsonElement a) => type switch
    {
        "AnnotationType_DamageDealt" => GameStateTracker.DetailInt(a, "damage") ?? 0,
        "AnnotationType_ModifiedLife" => GameStateTracker.DetailInt(a, "life") ?? 0,
        "AnnotationType_CounterAdded" => GameStateTracker.DetailInt(a, "transaction_amount") ?? 0,
        "AnnotationType_CounterRemoved" => -(GameStateTracker.DetailInt(a, "transaction_amount") ?? 0),
        _ => 0
    };

    private static GameEvent Base(GameStateTracker t, long ts, EventKind kind) => new()
    {
        TimestampMs = ts,
        GameNumber = t.GameNumber,
        Turn = t.Turn,
        ActiveSeat = t.ActiveSeat,
        Phase = t.Phase,
        Step = t.Step,
        Kind = kind
    };

    private static int? FirstAffected(JsonElement a)
    {
        if (!a.TryGetProperty("affectedIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var v in ids.EnumerateArray())
            if (v.TryGetInt32(out var iv)) return iv;
        return null;
    }

    private static int? FirstSeat(JsonElement message)
    {
        if (!message.TryGetProperty("systemSeatIds", out var ids) ||
            ids.ValueKind != JsonValueKind.Array) return null;
        foreach (var v in ids.EnumerateArray())
            if (v.TryGetInt32(out var iv)) return iv;
        return null;
    }

    private static void ReadRoom(
        JsonElement info, Dictionary<int, PlayerInfo> seats, ref string eventName)
    {
        if (!info.TryGetProperty("gameRoomConfig", out var cfg) ||
            !cfg.TryGetProperty("reservedPlayers", out var players) ||
            players.ValueKind != JsonValueKind.Array) return;

        foreach (var p in players.EnumerateArray())
        {
            if (!p.TryGetProperty("systemSeatId", out var s) || !s.TryGetInt32(out var seat))
                continue;
            seats[seat] = new PlayerInfo(
                seat,
                p.TryGetProperty("userId", out var u) ? u.GetString() ?? "" : "",
                p.TryGetProperty("playerName", out var n) ? n.GetString() ?? "" : "",
                p.TryGetProperty("platformId", out var pl) ? pl.GetString() ?? "" : "");
            if (eventName.Length == 0 && p.TryGetProperty("eventId", out var e))
                eventName = e.GetString() ?? "";
        }
    }

    private static void ReadResults(
        JsonElement fmr, ref int? winningTeam, ref int team1Games, ref int team2Games)
    {
        if (!fmr.TryGetProperty("resultList", out var list) ||
            list.ValueKind != JsonValueKind.Array) return;

        foreach (var r in list.EnumerateArray())
        {
            var scope = r.TryGetProperty("scope", out var s) ? s.GetString() : null;
            if (!r.TryGetProperty("winningTeamId", out var w) || !w.TryGetInt32(out var team))
                continue;
            if (scope == "MatchScope_Match") winningTeam = team;
            else if (scope == "MatchScope_Game")
            {
                if (team == 1) team1Games++;
                else if (team == 2) team2Games++;
            }
        }
    }

    private static long ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return 0;
        return ts.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(ts.GetString(), out var v) => v,
            JsonValueKind.Number when ts.TryGetInt64(out var v) => v,
            _ => 0
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EventExtractorTests"`
Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: extract typed game events from GRE annotations"
```

---

### Task 7: Narrator

**Files:**
- Create: `src/MtgaPbp.Render/Narrator.cs`, `tests/MtgaPbp.Tests/NarratorTests.cs`

**Interfaces:**
- Consumes: `GameEvent`, `EventKind`, `Transcript`.
- Produces: `Narrator` with `static IReadOnlyList<Line> Narrate(Transcript t, Density density)`; `enum Density { Beats, Verbose }`; `sealed record Line(int Turn, int Indent, string Text, bool IsTurnHeader)`.

Beats excludes `PhaseChange`, `ManaPaid`, and `Unknown`. Density is a filter over one event list, never a second parse.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/NarratorTests.cs`:

```csharp
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class NarratorTests
{
    private static Transcript T(params GameEvent[] events) => new(
        "m1", 0, 0, "Ladder",
        new PlayerInfo(1, "ME", "PlayerOne", "SteamWindows"),
        new PlayerInfo(2, "THEM", "PlayerTwo", "iPhone"),
        WinningTeamId: 1, GamesWon: 2, GamesLost: 0, Incomplete: false,
        events, new Dictionary<string, int>(), new HashSet<string>());

    private static GameEvent E(EventKind kind, int seq = 0) =>
        new() { Seq = seq, Kind = kind, Turn = 1, ActiveSeat = 1 };

    [Test]
    public void Beats_omits_phase_changes_mana_and_unknown()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.PhaseChange, 0),
            E(EventKind.ManaPaid, 1),
            E(EventKind.Unknown, 2),
            E(EventKind.LandPlayed, 3) with { SourceName = "Plains", ActorSeat = 1 }
        ), Density.Beats);

        Assert.That(lines.Any(l => l.Text.Contains("Plains")), Is.True);
        Assert.That(lines.Any(l => l.Text.Contains("phase", StringComparison.OrdinalIgnoreCase)),
            Is.False);
    }

    [Test]
    public void Verbose_includes_what_beats_omits()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.ManaPaid, 0) with { SourceName = "Island" },
            E(EventKind.Unknown, 1) with { RawType = "AnnotationType_Whatever" }
        ), Density.Verbose);

        Assert.That(lines, Is.Not.Empty);
        Assert.That(lines.Any(l => l.Text.Contains("AnnotationType_Whatever")), Is.True);
    }

    [Test]
    public void Turn_start_produces_a_turn_header_naming_the_active_player()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.TurnStart) with { Turn = 4, ActorSeat = 2 }), Density.Beats);

        var header = lines.Single(l => l.IsTurnHeader);
        Assert.That(header.Text, Does.Contain("Turn 4"));
        Assert.That(header.Text, Does.Contain("Opponent"));
    }

    [Test]
    public void Damage_to_a_player_reads_as_a_sentence()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Damage) with { SourceName = "Monastery Swiftspear", TargetSeat = 1, Amount = 2 }
        ), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Monastery Swiftspear deals 2 damage to You"));
    }

    [Test]
    public void Damage_to_a_permanent_names_the_permanent()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Damage) with
            {
                SourceName = "Lightning Bolt", TargetInstanceId = 99,
                TargetName = "Llanowar Elves", Amount = 3
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Lightning Bolt deals 3 damage to Llanowar Elves"));
    }

    [Test]
    public void Life_change_shows_direction_and_owner()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged) with { TargetSeat = 2, Amount = -3 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Opponent loses 3 life"));

        lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged) with { TargetSeat = 1, Amount = 4 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("You gain 4 life"));
    }

    [Test]
    public void Spell_cast_and_land_played_read_naturally()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.SpellCast, 0) with { SourceName = "Counterspell", ActorSeat = 1 },
            E(EventKind.LandPlayed, 1) with { SourceName = "Island", ActorSeat = 2 }
        ), Density.Beats);

        Assert.That(lines[0].Text, Is.EqualTo("You cast Counterspell"));
        Assert.That(lines[1].Text, Is.EqualTo("Opponent plays Island"));
    }

    [Test]
    public void Game_end_states_the_match_outcome()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.GameEnd) with { Detail = "You win the match" }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("You win the match"));
    }

    [Test]
    public void Narrate_drops_events_it_cannot_phrase_rather_than_emitting_blanks()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.ZoneMove) with { SourceName = null }), Density.Beats);
        Assert.That(lines.Any(l => string.IsNullOrWhiteSpace(l.Text)), Is.False);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~NarratorTests"`
Expected: FAIL — `Narrator` / `Density` / `Line` do not exist.

- [ ] **Step 3: Write Narrator.cs**

```csharp
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

public sealed record Line(int Turn, int Indent, string Text, bool IsTurnHeader);

public static class Narrator
{
    private static readonly HashSet<EventKind> VerboseOnly =
        [EventKind.PhaseChange, EventKind.ManaPaid, EventKind.Unknown];

    public static IReadOnlyList<Line> Narrate(Transcript t, Density density)
    {
        var lines = new List<Line>();
        foreach (var e in t.Events.OrderBy(x => x.Seq))
        {
            if (density == Density.Beats && VerboseOnly.Contains(e.Kind)) continue;
            var text = Phrase(e, t);
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add(new Line(e.Turn, e.Kind == EventKind.TurnStart ? 0 : 1, text,
                               e.Kind == EventKind.TurnStart));
        }
        return lines;
    }

    private static string Who(int? seat, Transcript t) =>
        seat is null ? "Someone" : seat == t.You?.Seat ? "You" : "Opponent";

    private static string Verb(int? seat, string youForm, string theyForm, Transcript t) =>
        seat == t.You?.Seat ? youForm : theyForm;

    private static string? Phrase(GameEvent e, Transcript t) => e.Kind switch
    {
        EventKind.TurnStart =>
            $"Turn {e.Turn} — {Who(e.ActorSeat ?? e.ActiveSeat, t)}",

        EventKind.LandPlayed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "play", "plays", t)} {e.SourceName}",

        EventKind.SpellCast when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "cast", "casts", t)} {e.SourceName}",

        EventKind.Resolved when e.SourceName is not null => $"{e.SourceName} resolves",
        EventKind.Countered when e.SourceName is not null => $"{e.SourceName} is countered",

        EventKind.Drew when e.SourceName is not null && e.ActorSeat == t.You?.Seat =>
            $"You draw {e.SourceName}",
        EventKind.Drew => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "draw", "draws", t)} a card",

        EventKind.Discarded when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "discard", "discards", t)} {e.SourceName}",

        EventKind.Destroyed when e.SourceName is not null => $"{e.SourceName} is destroyed",
        EventKind.Sacrificed when e.SourceName is not null => $"{e.SourceName} is sacrificed",
        EventKind.Exiled when e.SourceName is not null => $"{e.SourceName} is exiled",
        EventKind.Returned when e.SourceName is not null => $"{e.SourceName} returns to hand",
        EventKind.StateBasedAction when e.SourceName is not null =>
            $"{e.SourceName} is put into the graveyard",

        EventKind.Damage when e.TargetSeat is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {Who(e.TargetSeat, t)}",
        EventKind.Damage when e.TargetName is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {e.TargetName}",

        EventKind.LifeChanged when e.Amount != 0 =>
            $"{Who(e.TargetSeat, t)} " +
            $"{Verb(e.TargetSeat, e.Amount > 0 ? "gain" : "lose", e.Amount > 0 ? "gains" : "loses", t)} " +
            $"{Math.Abs(e.Amount)} life",

        EventKind.TokenCreated when e.TargetName is not null =>
            $"{e.SourceName ?? "An effect"} creates {e.TargetName}",

        EventKind.CounterChanged when e.TargetName is not null && e.Amount != 0 =>
            $"{e.TargetName} {(e.Amount > 0 ? "gets" : "loses")} {Math.Abs(e.Amount)} counter" +
            $"{(Math.Abs(e.Amount) == 1 ? "" : "s")}",

        EventKind.Scry => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)}",
        EventKind.Revealed when e.SourceName is not null => $"{e.SourceName} is revealed",

        EventKind.ManaPaid when e.SourceName is not null => $"taps {e.SourceName} for mana",
        EventKind.PhaseChange => $"— phase {e.Phase}, step {e.Step} —",
        EventKind.Unknown => $"[unhandled: {e.RawType}]",

        EventKind.GameEnd => e.Detail,
        EventKind.ZoneMove when e.SourceName is not null =>
            $"{e.SourceName} moves ({e.Detail})",

        _ => null
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~NarratorTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: narrate typed events into readable beats and verbose lines"
```

---

### Task 8: Markdown renderer

**Files:**
- Create: `src/MtgaPbp.Render/MarkdownRenderer.cs`, `tests/MtgaPbp.Tests/RendererTests.cs`

**Interfaces:**
- Consumes: `Transcript`, `Narrator`, `Density`, `Line`.
- Produces: `MarkdownRenderer` with `static string Render(Transcript t)`.

Markdown must stand alone when pasted into Discord — no dependency on the local card database or on the index page.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/RendererTests.cs`:

```csharp
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RendererTests
{
    internal static Transcript Sample(bool incomplete = false) => new(
        "abc-123", 1786326812781, 1786327812781, "Ladder",
        new PlayerInfo(1, "ME", "PlayerOne", "SteamWindows"),
        new PlayerInfo(2, "THEM", "PlayerTwo", "iPhone"),
        WinningTeamId: 1, GamesWon: 2, GamesLost: 1, Incomplete: incomplete,
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
            new GameEvent { Seq = 1, Kind = EventKind.LandPlayed, Turn = 1,
                            ActorSeat = 1, SourceName = "Plains" },
            new GameEvent { Seq = 2, Kind = EventKind.SpellCast, Turn = 1,
                            ActorSeat = 2, SourceName = "Lightning Bolt" },
            new GameEvent { Seq = 3, Kind = EventKind.GameEnd, Detail = "You win the match" },
        ],
        new Dictionary<string, int>(),
        new HashSet<string> { "Plains", "Lightning Bolt" });

    [Test]
    public void Markdown_has_a_heading_with_opponent_and_result()
    {
        var md = MarkdownRenderer.Render(Sample());
        Assert.That(md, Does.StartWith("# "));
        Assert.That(md, Does.Contain("PlayerTwo"));
        Assert.That(md, Does.Contain("Won 2-1"));
    }

    [Test]
    public void Markdown_contains_the_beats_not_the_verbose_stream()
    {
        var md = MarkdownRenderer.Render(Sample());
        Assert.That(md, Does.Contain("Plains"));
        Assert.That(md, Does.Contain("Lightning Bolt"));
        Assert.That(md, Does.Not.Contain("unhandled"));
    }

    [Test]
    public void Markdown_flags_a_truncated_match()
    {
        Assert.That(MarkdownRenderer.Render(Sample(incomplete: true)),
            Does.Contain("incomplete"));
    }

    [Test]
    public void Markdown_renders_turn_headers_as_subheadings()
    {
        Assert.That(MarkdownRenderer.Render(Sample()), Does.Contain("## Turn 1"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: FAIL — `MarkdownRenderer` does not exist.

- [ ] **Step 3: Write MarkdownRenderer.cs**

```csharp
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static class MarkdownRenderer
{
    public static string Render(Transcript t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {TranscriptSummary.Title(t)}");
        sb.AppendLine();
        sb.AppendLine($"*{TranscriptSummary.Subtitle(t)}*");
        sb.AppendLine();
        if (t.Incomplete)
            sb.AppendLine("> This match is incomplete — the log was rotated before it finished.")
              .AppendLine();

        foreach (var line in Narrator.Narrate(t, Density.Beats))
        {
            if (line.IsTurnHeader) sb.AppendLine().AppendLine($"## {line.Text}");
            else sb.AppendLine($"- {line.Text}");
        }
        return sb.ToString();
    }
}

public static class TranscriptSummary
{
    public static string Title(Transcript t) =>
        $"{t.You?.ScreenName ?? "You"} vs {t.Opponent?.ScreenName ?? "Opponent"}";

    public static string Result(Transcript t)
    {
        if (t.Incomplete && t.WinningTeamId is null) return "Unfinished";
        var won = t.WinningTeamId is not null && t.WinningTeamId == t.You?.Seat;
        return $"{(won ? "Won" : "Lost")} {t.GamesWon}-{t.GamesLost}";
    }

    public static string Subtitle(Transcript t) =>
        $"{t.EventName} · {Date(t):yyyy-MM-dd HH:mm} · {Result(t)} · {Turns(t)} turns";

    public static DateTimeOffset Date(Transcript t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.StartedAtMs == 0 ? 0 : t.StartedAtMs).ToLocalTime();

    public static int Turns(Transcript t) => t.Events.Count == 0 ? 0 : t.Events.Max(e => e.Turn);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: standalone markdown transcript export"
```

---

### Task 9: Per-game HTML page

**Files:**
- Create: `src/MtgaPbp.Render/GamePageRenderer.cs`
- Modify: `tests/MtgaPbp.Tests/RendererTests.cs` (append tests)

**Interfaces:**
- Consumes: `Transcript`, `Narrator`, `Density`, `TranscriptSummary`.
- Produces: `GamePageRenderer` with `static string Render(Transcript t)`.

Self-contained: inline CSS and JS, no `fetch`, no external assets. Both densities are emitted into the page and toggled by a button. Turn headers get `id="t{N}"` anchors.

- [ ] **Step 1: Write the failing test (append to RendererTests.cs)**

```csharp
    [Test]
    public void GamePage_is_self_contained_with_no_external_requests()
    {
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Not.Contain("fetch("));
        Assert.That(html, Does.Not.Contain("<script src="));
        Assert.That(html, Does.Not.Contain("<link rel=\"stylesheet\""));
        Assert.That(html, Does.Not.Contain("http://"));
    }

    [Test]
    public void GamePage_contains_both_densities_and_a_toggle()
    {
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Contain("data-density=\"beats\""));
        Assert.That(html, Does.Contain("data-density=\"verbose\""));
        Assert.That(html, Does.Contain("id=\"density-toggle\""));
    }

    [Test]
    public void GamePage_gives_each_turn_an_anchor()
    {
        Assert.That(GamePageRenderer.Render(Sample()), Does.Contain("id=\"t1\""));
    }

    [Test]
    public void GamePage_escapes_html_in_player_names()
    {
        var t = Sample() with { Opponent = new PlayerInfo(2, "X", "<script>bad</script>", "PC") };
        var html = GamePageRenderer.Render(t);
        Assert.That(html, Does.Not.Contain("<script>bad"));
        Assert.That(html, Does.Contain("&lt;script&gt;bad"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: FAIL — `GamePageRenderer` does not exist.

- [ ] **Step 3: Write GamePageRenderer.cs**

```csharp
using System.Net;
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static class GamePageRenderer
{
    public static string Render(Transcript t)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(TranscriptSummary.Title(t))}</title>
            <style>{Css}</style></head><body>
            <header>
              <p class="back"><a href="../index.html">&larr; All games</a></p>
              <h1>{E(TranscriptSummary.Title(t))}</h1>
              <p class="sub">{E(TranscriptSummary.Subtitle(t))}</p>
              <button id="density-toggle" type="button">Show verbose</button>
            </header>
            """);

        if (t.Incomplete)
            sb.Append("""<p class="warn">This match is incomplete — the log was rotated before it finished.</p>""");

        foreach (var density in new[] { Density.Beats, Density.Verbose })
        {
            var slug = density == Density.Beats ? "beats" : "verbose";
            sb.Append($"""<section data-density="{slug}"{(density == Density.Verbose ? " hidden" : "")}>""");
            foreach (var line in Narrator.Narrate(t, density))
            {
                if (line.IsTurnHeader)
                    sb.Append($"""<h2 id="t{line.Turn}">{E(line.Text)}</h2>""");
                else
                    sb.Append($"""<p class="beat">{E(line.Text)}</p>""");
            }
            sb.Append("</section>");
        }

        sb.Append($"""
            <script>{Script}</script></body></html>
            """);
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{color-scheme:light dark}
        body{font:16px/1.6 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
             max-width:52rem;margin:0 auto;padding:2rem 1rem}
        header{border-bottom:1px solid currentColor;padding-bottom:1rem;margin-bottom:1rem;opacity:.95}
        h1{font-size:1.4rem;margin:.2rem 0}
        .sub{opacity:.7;margin:.2rem 0 .8rem}
        .back a{text-decoration:none;opacity:.7}
        h2{font-size:1rem;margin:1.6rem 0 .4rem;padding-top:.6rem;border-top:1px dashed currentColor;opacity:.85}
        .beat{margin:.15rem 0 .15rem 1.5rem}
        .warn{border-left:3px solid #c80;padding-left:.8rem;opacity:.85}
        button{font:inherit;padding:.3rem .8rem;cursor:pointer}
        """;

    private const string Script = """
        (function () {
          var btn = document.getElementById('density-toggle');
          var beats = document.querySelector('[data-density="beats"]');
          var verbose = document.querySelector('[data-density="verbose"]');
          btn.addEventListener('click', function () {
            var showVerbose = verbose.hidden;
            verbose.hidden = !showVerbose;
            beats.hidden = showVerbose;
            btn.textContent = showVerbose ? 'Show beats' : 'Show verbose';
          });
        })();
        """;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: self-contained per-game HTML page with density toggle"
```

---

### Task 10: Index page with embedded search

**Files:**
- Create: `src/MtgaPbp.Render/IndexRenderer.cs`
- Modify: `tests/MtgaPbp.Tests/RendererTests.cs` (append tests)

**Interfaces:**
- Consumes: `Transcript`, `TranscriptSummary`.
- Produces: `sealed record MatchSummary(string MatchId, string Date, long SortKey, string EventName, string Opponent, string Result, int Turns, bool Incomplete, IReadOnlyList<string> Cards)`; `IndexRenderer` with `static MatchSummary Summarize(Transcript t)` and `static string Render(IEnumerable<MatchSummary> rows)`.

Search data is embedded as a JSON blob in the page, because browsers block `fetch()` on `file://`. Sorted most-recent-first.

- [ ] **Step 1: Write the failing test (append to RendererTests.cs)**

```csharp
    [Test]
    public void Summarize_extracts_the_searchable_fields()
    {
        var s = IndexRenderer.Summarize(Sample());
        Assert.That(s.MatchId, Is.EqualTo("abc-123"));
        Assert.That(s.Opponent, Is.EqualTo("PlayerTwo"));
        Assert.That(s.Result, Is.EqualTo("Won 2-1"));
        Assert.That(s.Cards, Does.Contain("Lightning Bolt"));
        Assert.That(s.EventName, Is.EqualTo("Ladder"));
    }

    [Test]
    public void Index_embeds_data_rather_than_fetching_it()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Not.Contain("fetch("));
        Assert.That(html, Does.Contain("id=\"data\""));
        Assert.That(html, Does.Contain("PlayerTwo"));
    }

    [Test]
    public void Index_links_to_each_game_page()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Contain("games/abc-123.html"));
    }

    [Test]
    public void Index_sorts_most_recent_first()
    {
        var older = Sample() with { MatchId = "old", StartedAtMs = 1_000_000_000_000 };
        var newer = Sample() with { MatchId = "new", StartedAtMs = 2_000_000_000_000 };
        var html = IndexRenderer.Render(
            [IndexRenderer.Summarize(older), IndexRenderer.Summarize(newer)]);

        Assert.That(html.IndexOf("\"new\"", StringComparison.Ordinal),
            Is.LessThan(html.IndexOf("\"old\"", StringComparison.Ordinal)));
    }

    [Test]
    public void Index_has_a_search_box()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Contain("id=\"q\""));
    }

    [Test]
    public void Index_renders_an_empty_archive_without_crashing()
    {
        Assert.That(IndexRenderer.Render([]), Does.Contain("No games"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: FAIL — `IndexRenderer` / `MatchSummary` do not exist.

- [ ] **Step 3: Write IndexRenderer.cs**

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public sealed record MatchSummary(
    string MatchId, string Date, long SortKey, string EventName,
    string Opponent, string Result, int Turns, bool Incomplete,
    IReadOnlyList<string> Cards);

public static class IndexRenderer
{
    public static MatchSummary Summarize(Transcript t) => new(
        t.MatchId,
        TranscriptSummary.Date(t).ToString("yyyy-MM-dd HH:mm"),
        t.StartedAtMs,
        t.EventName,
        t.Opponent?.ScreenName ?? "Opponent",
        TranscriptSummary.Result(t),
        TranscriptSummary.Turns(t),
        t.Incomplete,
        t.CardsSeen.OrderBy(c => c, StringComparer.Ordinal).ToList());

    public static string Render(IEnumerable<MatchSummary> rows)
    {
        var ordered = rows.OrderByDescending(r => r.SortKey).ToList();
        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        // </script> inside embedded JSON would close the tag early.
        json = json.Replace("</", "<\\/", StringComparison.Ordinal);

        var body = ordered.Count == 0
            ? "<p class=\"empty\">No games archived yet. Play a match, then run <code>mtga-pbp</code>.</p>"
            : "<table id=\"rows\"><thead><tr><th>Date</th><th>Event</th><th>Opponent</th>"
              + "<th>Result</th><th>Turns</th></tr></thead><tbody></tbody></table>";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MTGA Play-by-Play</title>
            <style>{{Css}}</style></head><body>
            <h1>MTGA Play-by-Play</h1>
            <p class="sub">{{ordered.Count}} game{{(ordered.Count == 1 ? "" : "s")}} archived</p>
            <input id="q" type="search" placeholder="Search opponent, event, result, or card…"
                   autocomplete="off">
            <p id="count" class="sub"></p>
            {{body}}
            <script id="data" type="application/json">{{json}}</script>
            <script>{{Script}}</script>
            </body></html>
            """;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{color-scheme:light dark}
        body{font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;
             max-width:64rem;margin:0 auto;padding:2rem 1rem}
        h1{font-size:1.5rem;margin:0 0 .2rem}
        .sub{opacity:.65;margin:.2rem 0 1rem}
        #q{width:100%;font:inherit;padding:.55rem .7rem;margin-bottom:1rem;
           border:1px solid currentColor;border-radius:.4rem;background:transparent;color:inherit}
        table{width:100%;border-collapse:collapse}
        th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid rgba(128,128,128,.3)}
        th{font-size:.8rem;text-transform:uppercase;letter-spacing:.04em;opacity:.6}
        tbody tr:hover{background:rgba(128,128,128,.12)}
        a{color:inherit}
        .win{color:#2a2}.loss{opacity:.7}
        .empty{opacity:.7}
        code{font-family:ui-monospace,Menlo,Consolas,monospace}
        """;

    private const string Script = """
        (function () {
          var el = document.getElementById('data');
          if (!el) return;
          var rows = JSON.parse(el.textContent);
          var tbody = document.querySelector('#rows tbody');
          var q = document.getElementById('q');
          var count = document.getElementById('count');
          if (!tbody) return;

          function esc(s) {
            return String(s).replace(/[&<>"]/g, function (c) {
              return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
            });
          }

          function draw(list) {
            tbody.innerHTML = list.map(function (r) {
              var cls = r.Result.indexOf('Won') === 0 ? 'win' : 'loss';
              return '<tr><td><a href="games/' + encodeURIComponent(r.MatchId) + '.html">' +
                esc(r.Date) + '</a></td><td>' + esc(r.EventName) + '</td><td>' +
                esc(r.Opponent) + '</td><td class="' + cls + '">' + esc(r.Result) +
                (r.Incomplete ? ' *' : '') + '</td><td>' + r.Turns + '</td></tr>';
            }).join('');
            count.textContent = list.length + ' of ' + rows.length + ' shown';
          }

          function haystack(r) {
            return (r.Opponent + ' ' + r.EventName + ' ' + r.Result + ' ' +
                    r.Date + ' ' + r.Cards.join(' ')).toLowerCase();
          }

          q.addEventListener('input', function () {
            var terms = q.value.toLowerCase().split(/\s+/).filter(Boolean);
            draw(!terms.length ? rows : rows.filter(function (r) {
              var h = haystack(r);
              return terms.every(function (t) { return h.indexOf(t) !== -1; });
            }));
          });

          draw(rows);
        })();
        """;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RendererTests"`
Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: searchable index page with embedded match data"
```

---

### Task 11: CLI and configuration

**Files:**
- Create: `src/MtgaPbp.Cli/Config.cs`, `src/MtgaPbp.Cli/Program.cs`
- Create: `tests/MtgaPbp.Tests/ConfigTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `Config` with `static Config Load(string exeDir)`, properties `LogPaths` (string[]), `CardDbPath` (string?), `ArchiveDir`, `OutputDir`, `LocalPlayerUserId` (string?); `static Config Default()`.

Commands: bare (capture + build), `capture`, `build`, `build --rebuild`, `stats`.

- [ ] **Step 1: Write the failing test**

`tests/MtgaPbp.Tests/ConfigTests.cs`:

```csharp
using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class ConfigTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() =>
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [Test]
    public void Load_returns_defaults_when_no_config_file_exists()
    {
        var c = Config.Load(_dir);
        Assert.That(c.LogPaths, Is.Not.Empty);
        Assert.That(c.LogPaths[0], Does.Contain("Player.log"));
        Assert.That(c.ArchiveDir, Is.Not.Empty);
    }

    [Test]
    public void Load_reads_overrides_from_mtga_pbp_json()
    {
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), """
        { "LogPaths": [ "C:\\custom\\Player.log" ],
          "OutputDir": "C:\\custom\\out",
          "LocalPlayerUserId": "ABC123" }
        """);

        var c = Config.Load(_dir);
        Assert.That(c.LogPaths, Is.EqualTo(new[] { @"C:\custom\Player.log" }));
        Assert.That(c.OutputDir, Is.EqualTo(@"C:\custom\out"));
        Assert.That(c.LocalPlayerUserId, Is.EqualTo("ABC123"));
    }

    [Test]
    public void Load_survives_a_corrupt_config_file_by_falling_back_to_defaults()
    {
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), "{ not json");
        Assert.That(Config.Load(_dir).LogPaths, Is.Not.Empty);
    }

    [Test]
    public void Default_log_paths_include_both_current_and_previous_logs()
    {
        var c = Config.Default();
        Assert.That(c.LogPaths.Any(p => p.EndsWith("Player.log")), Is.True);
        Assert.That(c.LogPaths.Any(p => p.EndsWith("Player-prev.log")), Is.True);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConfigTests"`
Expected: FAIL — `MtgaPbp.Cli.Config` does not exist. (Also add a project reference: `dotnet add tests/MtgaPbp.Tests reference src/MtgaPbp.Cli`.)

- [ ] **Step 3: Write Config.cs**

```csharp
using System.Text.Json;

namespace MtgaPbp.Cli;

public sealed class Config
{
    public string[] LogPaths { get; set; } = [];
    public string? CardDbPath { get; set; }
    public string ArchiveDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string? LocalPlayerUserId { get; set; }

    public static Config Default()
    {
        var low = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Wizards Of The Coast", "MTGA");
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MTGA_PlayByPlay");

        return new Config
        {
            LogPaths = [Path.Combine(low, "Player.log"), Path.Combine(low, "Player-prev.log")],
            ArchiveDir = Path.Combine(home, "archive"),
            OutputDir = Path.Combine(home, "out"),
        };
    }

    public static Config Load(string exeDir)
    {
        var path = Path.Combine(exeDir, "mtga-pbp.json");
        var cfg = Default();
        if (!File.Exists(path)) return cfg;

        try
        {
            var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
            if (loaded is null) return cfg;
            if (loaded.LogPaths.Length > 0) cfg.LogPaths = loaded.LogPaths;
            if (!string.IsNullOrWhiteSpace(loaded.CardDbPath)) cfg.CardDbPath = loaded.CardDbPath;
            if (!string.IsNullOrWhiteSpace(loaded.ArchiveDir)) cfg.ArchiveDir = loaded.ArchiveDir;
            if (!string.IsNullOrWhiteSpace(loaded.OutputDir)) cfg.OutputDir = loaded.OutputDir;
            if (!string.IsNullOrWhiteSpace(loaded.LocalPlayerUserId))
                cfg.LocalPlayerUserId = loaded.LocalPlayerUserId;
            return cfg;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Ignoring malformed {path}; using defaults.");
            return cfg;
        }
    }
}
```

- [ ] **Step 4: Write Program.cs**

```csharp
using MtgaPbp.Core;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var exeDir = AppContext.BaseDirectory;
        var cfg = Config.Load(exeDir);
        var command = args.FirstOrDefault() ?? "all";
        var rebuild = args.Contains("--rebuild");

        try
        {
            return command switch
            {
                "capture" => Capture(cfg),
                "build" => Build(cfg, rebuild),
                "stats" => Stats(cfg),
                "all" => Capture(cfg) is var c && c != 0 ? c : Build(cfg, rebuild),
                _ => Usage()
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            mtga-pbp                  capture new matches, then rebuild the site
            mtga-pbp capture          capture only
            mtga-pbp build            rebuild the site from the archive
            mtga-pbp build --rebuild  force re-parse of every archived match
            mtga-pbp stats            unhandled annotations and unresolved cards
            """);
        return 1;
    }

    private static int Capture(Config cfg)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        var stats = new ScanStats();
        var added = 0;

        foreach (var log in cfg.LogPaths.Where(File.Exists))
        {
            var slices = MatchSlicer.Slice(LogScanner.Scan(log, stats));
            foreach (var slice in slices)
                if (archive.Write(slice)) added++;
        }

        Console.WriteLine(
            $"captured {added} new match(es); {stats.JsonLines:N0} json lines read, " +
            $"{stats.MalformedLines} malformed");
        return 0;
    }

    private static (CardDb db, string path) OpenCards(Config cfg)
    {
        var path = CardDb.FindDatabase(cfg.CardDbPath)
            ?? throw new FileNotFoundException(
                "Card database not found. Looked for Raw_CardDatabase_*.mtga under the Arena " +
                "install directories. Set \"CardDbPath\" in mtga-pbp.json to point at it.");
        return (new CardDb(path), path);
    }

    private static int Build(Config cfg, bool rebuild)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        var (cards, dbPath) = OpenCards(cfg);
        using var _ = cards;

        var gamesDir = Path.Combine(cfg.OutputDir, "games");
        var textDir = Path.Combine(cfg.OutputDir, "text");
        Directory.CreateDirectory(gamesDir);
        Directory.CreateDirectory(textDir);

        var extractor = new EventExtractor(cards);
        var summaries = new List<MatchSummary>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var matchId in archive.MatchIds())
        {
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;

            var transcript = extractor.Extract(matchId, lines);
            File.WriteAllText(Path.Combine(gamesDir, $"{matchId}.html"),
                GamePageRenderer.Render(transcript));
            File.WriteAllText(Path.Combine(textDir, $"{matchId}.md"),
                MarkdownRenderer.Render(transcript));
            summaries.Add(IndexRenderer.Summarize(transcript));

            foreach (var c in transcript.CardsSeen.Where(c => c.StartsWith("Card #")))
                unresolved.Add(c);
        }

        File.WriteAllText(Path.Combine(cfg.OutputDir, "index.html"),
            IndexRenderer.Render(summaries));
        if (unresolved.Count > 0)
            File.WriteAllLines(Path.Combine(cfg.OutputDir, "unresolved.txt"), unresolved);

        Console.WriteLine($"built {summaries.Count} game(s) into {cfg.OutputDir}");
        Console.WriteLine($"card database: {dbPath}");
        if (unresolved.Count > 0)
            Console.WriteLine($"{unresolved.Count} unresolved card id(s) — see unresolved.txt");
        return 0;
    }

    private static int Stats(Config cfg)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        var (cards, _) = OpenCards(cfg);
        using var _ = cards;

        var extractor = new EventExtractor(cards);
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = 0;

        foreach (var matchId in archive.MatchIds())
        {
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;
            matches++;
            var t = extractor.Extract(matchId, lines);
            foreach (var (k, v) in t.UnknownAnnotations)
                unknown[k] = unknown.GetValueOrDefault(k) + v;
            foreach (var c in t.CardsSeen.Where(c => c.StartsWith("Card #")))
                unresolved[c] = unresolved.GetValueOrDefault(c) + 1;
        }

        Console.WriteLine($"{matches} match(es) in archive\n");
        Console.WriteLine("unhandled annotation types:");
        foreach (var (k, v) in unknown.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unknown.Count == 0) Console.WriteLine("  (none)");

        Console.WriteLine("\nunresolved cards:");
        foreach (var (k, v) in unresolved.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unresolved.Count == 0) Console.WriteLine("  (none)");
        return 0;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, all tests green.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: CLI with capture, build, and stats commands"
```

---

### Task 12: Golden-file test and first real run

**Files:**
- Create: `tests/MtgaPbp.Tests/GoldenFileTests.cs`, `tests/MtgaPbp.Tests/Fixtures/` (fixture + expected markdown)
- Modify: `tests/MtgaPbp.Tests/MtgaPbp.Tests.csproj` (copy fixtures to output)

**Interfaces:**
- Consumes: `EventExtractor`, `MarkdownRenderer`, `CardDb`.
- Produces: nothing consumed by later tasks.

The fixture is one real archived match with screen names and user IDs replaced by stable pseudonyms — including the user's own. It exercises scanner → slicer → tracker → extractor → narrator in one pass, which is where regressions actually appear.

- [ ] **Step 1: Capture real data and mint the fixture**

```bash
dotnet run --project src/MtgaPbp.Cli -- capture
dotnet run --project src/MtgaPbp.Cli -- build
dotnet run --project src/MtgaPbp.Cli -- stats
```

Pick one completed match from `~/MTGA_PlayByPlay/archive/raw/`, decompress it, and
replace every occurrence of both players' `playerName` and `userId` values with
`PlayerOne`/`USER_ONE` and `PlayerTwo`/`USER_TWO`. Save as
`tests/MtgaPbp.Tests/Fixtures/sample-match.jsonl`.

- [ ] **Step 2: Make the csproj copy fixtures to the output directory**

Add inside `tests/MtgaPbp.Tests/MtgaPbp.Tests.csproj`:

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Write the golden-file test**

`tests/MtgaPbp.Tests/GoldenFileTests.cs`:

```csharp
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class GoldenFileTests
{
    private static string FixtureDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    private static string SamplePath => Path.Combine(FixtureDir, "sample-match.jsonl");
    private static string GoldenPath => Path.Combine(FixtureDir, "sample-match.expected.md");

    private static Transcript Extract()
    {
        var dbPath = CardDb.FindDatabase(null);
        Assert.That(dbPath, Is.Not.Null,
            "MTGA card database not found; this test requires Arena installed.");
        using var db = new CardDb(dbPath!);
        return new EventExtractor(db).Extract("sample", File.ReadAllLines(SamplePath));
    }

    [Test]
    public void Real_match_produces_a_transcript_with_both_players_and_a_result()
    {
        var t = Extract();
        Assert.That(t.You, Is.Not.Null);
        Assert.That(t.Opponent, Is.Not.Null);
        Assert.That(t.Events, Is.Not.Empty);
        Assert.That(t.Incomplete, Is.False);
    }

    [Test]
    public void Real_match_names_are_resolved_not_placeholders()
    {
        var t = Extract();
        var placeholders = t.CardsSeen.Where(c => c.StartsWith("Card #")).ToList();
        Assert.That(placeholders, Is.Empty,
            $"unresolved card names: {string.Join(", ", placeholders)}");
    }

    [Test]
    public void Rendered_markdown_matches_the_golden_file()
    {
        var actual = MarkdownRenderer.Render(Extract()).ReplaceLineEndings("\n");

        if (!File.Exists(GoldenPath))
        {
            File.WriteAllText(GoldenPath, actual);
            Assert.Fail($"Golden file created at {GoldenPath}. Review it, then re-run.");
        }

        Assert.That(actual,
            Is.EqualTo(File.ReadAllText(GoldenPath).ReplaceLineEndings("\n")));
    }

    [Test]
    public void Declared_targets_are_reported_as_effects_not_targets()
    {
        // Accepted limitation: the GRE never sends target ids for the opponent, so
        // the transcript reports observed effects instead. If Arena ever starts
        // emitting them, this warning is the signal to revisit the design.
        var t = Extract();
        var hasTargetWording = Narrator.Narrate(t, Density.Beats)
            .Any(l => l.Text.Contains("targeting", StringComparison.OrdinalIgnoreCase));

        Assert.That(hasTargetWording, Is.False,
            "transcript should phrase interactions as effects, not declared targets");
        Assert.Warn(
            "Declared targets are unavailable from the Arena log (SelectTargetsReq is " +
            "sent only to the choosing player and PlayerSubmittedTargets carries no " +
            "target ids). Interactions are reported as observed effects — see the spec.");
    }
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: first run FAILS on `Rendered_markdown_matches_the_golden_file` while it writes the golden file. Read the generated markdown — it must be a sensible transcript, not noise. Then re-run; expected PASS with one warning.

- [ ] **Step 5: Publish a single-file executable and run it end to end**

```bash
dotnet publish src/MtgaPbp.Cli -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o dist
./dist/MtgaPbp.Cli.exe
```

Expected: `captured N new match(es)`, then `built N game(s)`. Open the generated
`index.html`, confirm the list renders, search filters it, and a game page opens
with a working density toggle.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "test: golden-file coverage over a real anonymized match"
```

---

## Self-Review

**1. Spec coverage.** Every spec section maps to a task: two-stage architecture → 2/3/4; components table → 1–11; local player identification → 6; card name resolution incl. the `Formatted` trap and ability fallback → 1, 5; event model table → 6; density → 7; output layout and the `file://` constraint → 9, 10; error handling table → 2 (malformed/non-JSON), 4 (idempotence), 5 (cycle safety), 6 (unknown annotations), 11 (missing card DB, corrupt config); testing section → 5, 6, 12; CLI → 11; per-turn anchors and board summary → 9.

**Gap found and accepted:** the spec's end-of-turn per-player board summary (creature names, P/T, damage, counters) is not implemented by Task 9, which delivers only turn anchors and headers. `GameStateTracker` exposes everything needed, but events are a flat stream with no end-of-turn snapshot. Adding it requires the extractor to emit a `BoardSnapshot` event at each turn boundary. **Deferred to a follow-up task rather than silently dropped** — it is additive, touches only Task 6 and Task 9, and is not needed for a working v1. Flagged for the user.

**2. Placeholder scan.** No TBD/TODO/"similar to Task N". Every code step carries real code. Task 12 Step 1 is a manual data step by necessity — it names the exact file, the exact substitutions, and the exact destination path.

**3. Type consistency.** `ICardDb.NameForLocId`/`CardForGrpId` are used identically in Tasks 1, 5, 6. `GameStateTracker.DetailInt`/`DetailString`/`HasType` are declared `internal static` in Task 5 and consumed from `EventExtractor` in Task 6 — both are in `MtgaPbp.Core`, so `internal` resolves. `Transcript` is constructed in Task 6 and consumed in 7/8/9/10 with matching property names. `TranscriptSummary` is introduced in Task 8 and reused in 9/10. `Density`/`Line`/`Narrator.Narrate` are consistent across 7/8/9. `MatchSummary` field names match the JS in Task 10 (`MatchId`, `Date`, `EventName`, `Opponent`, `Result`, `Turns`, `Incomplete`, `Cards`) — `System.Text.Json` serializes PascalCase by default, and the script reads PascalCase.

One inconsistency found and fixed inline: Task 11's test project needs a reference to `MtgaPbp.Cli`, which the Task 1 scaffold does not add. Noted in Task 11 Step 2.
