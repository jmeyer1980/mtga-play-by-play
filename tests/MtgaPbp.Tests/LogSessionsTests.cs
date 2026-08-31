using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The rotation check, walked through the restart sequences it exists to tell apart.
/// </summary>
public class LogSessionsTests
{
    private string _dir = null!;
    private string _current = null!;
    private string _previous = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"sessions_{Guid.NewGuid():N}")).FullName;
        _current = Path.Combine(_dir, "Player.log");
        _previous = Path.Combine(_dir, "Player-prev.log");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A log header shaped like Arena's: the startup line sits below boilerplate that
    /// is identical between sessions, which is the reason it is the identity.
    /// </summary>
    private static void WriteLog(string path, string startup, int traffic = 3)
    {
        var lines = new List<string>
        {
            "Mono path[0] = 'C:/Program Files (x86)/Steam/steamapps/common/MTGA/MTGA_Data/Managed'",
            "[Physics::Module] Id: 0xdecafbad",
            "Initialize engine version: 6000.3.14f1 (d68c3f99a318)",
            $"Startup Timestamp: {startup}",
        };
        for (var i = 0; i < traffic; i++) lines.Add($"[UnityCrossThreadLogger] line {i}");
        File.WriteAllLines(path, lines);
    }

    private string[] Paths => [_current, _previous];

    [Test]
    public void The_startup_timestamp_is_read_out_of_the_header()
    {
        WriteLog(_current, "8/31/2026 4:08:45 AM");

        var seen = LogSessions.Read(_current);

        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.Startup, Is.EqualTo("8/31/2026 4:08:45 AM"));
        Assert.That(seen.Size, Is.GreaterThan(0));
    }

    [Test]
    public void A_log_with_no_startup_line_has_no_identity()
    {
        File.WriteAllLines(_current, ["Mono path[0] = 'nowhere'", "no timestamp here"]);

        Assert.That(LogSessions.Read(_current), Is.Null);
    }

    [Test]
    public void A_log_that_is_not_there_has_no_identity()
    {
        Assert.That(LogSessions.Read(_current), Is.Null);
    }

    [Test]
    public void The_first_capture_of_all_has_nothing_to_compare_and_says_nothing()
    {
        WriteLog(_current, "session-A");

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Null);
    }

    [Test]
    public void A_capture_with_nothing_restarted_in_between_says_nothing()
    {
        WriteLog(_current, "session-A");
        var first = new LogSessions(_dir);
        first.Observe(Paths);
        first.Commit();

        // The same session, just longer.
        WriteLog(_current, "session-A", traffic: 50);

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Null);
    }

    /// <summary>
    /// One restart. The session that was current is now the previous one, this capture
    /// reads it, and nothing has been lost — so there is nothing to say.
    /// </summary>
    [Test]
    public void One_restart_is_silent_because_the_old_session_is_still_readable()
    {
        WriteLog(_current, "session-A");
        var first = new LogSessions(_dir);
        first.Observe(Paths);
        first.Commit();

        WriteLog(_previous, "session-A");
        WriteLog(_current, "session-B");

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Null);
    }

    /// <summary>
    /// Three restarts: session B began and rotated off the end without any capture ever
    /// seeing it, which is the loss the README could only describe. Two restarts reach
    /// here as well and have lost nothing — see MayHaveLostASession for why the two
    /// cannot be told apart, and why the message says "if" rather than "your matches
    /// are gone".
    /// </summary>
    [Test]
    public void A_session_that_came_and_went_between_captures_is_reported()
    {
        WriteLog(_current, "session-A");
        var first = new LogSessions(_dir);
        first.Observe(Paths);
        first.Commit();

        // A → B → C → D, with only D current and C kept.
        WriteLog(_previous, "session-C");
        WriteLog(_current, "session-D");

        var warning = new LogSessions(_dir).Observe(Paths);

        Assert.That(warning, Is.Not.Null);
        Assert.That(warning, Does.Contain("restarted more than once"));
        Assert.That(warning, Does.Contain("watch"));
    }

    [Test]
    public void A_rotation_is_reported_once_and_not_on_every_capture_after_it()
    {
        WriteLog(_current, "session-A");
        var first = new LogSessions(_dir);
        first.Observe(Paths);
        first.Commit();

        WriteLog(_previous, "session-C");
        WriteLog(_current, "session-D");
        var second = new LogSessions(_dir);
        Assert.That(second.Observe(Paths), Is.Not.Null, "the run that noticed it");
        second.Commit();

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Null,
            "the next run has nothing new to say about the same rotation");
    }

    /// <summary>
    /// Logs that cannot be read say nothing about rotation, and must not be allowed to
    /// erase the record either — that record is the only thing a later capture could
    /// notice a rotation against, so wiping it would silently disarm the check.
    /// </summary>
    [Test]
    public void Unreadable_logs_neither_warn_nor_forget_what_was_seen()
    {
        WriteLog(_current, "session-A");
        var first = new LogSessions(_dir);
        first.Observe(Paths);
        first.Commit();

        File.Delete(_current);
        var blind = new LogSessions(_dir);
        Assert.That(blind.Observe(Paths), Is.Null, "nothing to compare, so nothing to say");
        blind.Commit();

        // Arena comes back, twice restarted. The record survived, so this is still seen.
        WriteLog(_previous, "session-C");
        WriteLog(_current, "session-D");

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Not.Null);
    }

    [Test]
    public void A_damaged_record_is_started_over_rather_than_thrown()
    {
        WriteLog(_current, "session-A");
        File.WriteAllText(Path.Combine(_dir, "logs.json"), "{ this is not json");

        Assert.That(new LogSessions(_dir).Observe(Paths), Is.Null);
    }

    [Test]
    public void Committing_twice_writes_once()
    {
        WriteLog(_current, "session-A");
        var sessions = new LogSessions(_dir);
        sessions.Observe(Paths);

        Assert.That(sessions.Commit(), Is.True);
        Assert.That(sessions.Commit(), Is.False, "nothing has changed since");
    }
}
