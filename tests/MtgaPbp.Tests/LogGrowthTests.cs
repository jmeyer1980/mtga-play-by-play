using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The poll loop's memory of the logs. Every case here is one that used to end `watch`
/// or silently skip a match — see the class remarks on <see cref="LogGrowth"/>.
/// </summary>
public class LogGrowthTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"growth_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private string Log(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Append(string path, string more) => File.AppendAllText(path, more);

    [Test]
    public void A_log_seen_for_the_first_time_counts_as_growth()
    {
        var log = Log("Player.log", "one line");
        var growth = new LogGrowth();

        Assert.That(growth.Measure([log]), Is.True);
    }

    [Test]
    public void A_committed_log_that_has_not_moved_is_not_growth()
    {
        var log = Log("Player.log", "one line");
        var growth = new LogGrowth();

        growth.Measure([log]);
        growth.Commit();

        Assert.That(growth.Measure([log]), Is.False);
    }

    [Test]
    public void A_log_that_grew_since_the_last_capture_is_growth()
    {
        var log = Log("Player.log", "one line");
        var growth = new LogGrowth();
        growth.Measure([log]);
        growth.Commit();

        Append(log, " and another");

        Assert.That(growth.Measure([log]), Is.True);
    }

    /// <summary>Arena restarting truncates the log, so smaller is still news.</summary>
    [Test]
    public void A_log_that_shrank_is_growth()
    {
        var log = Log("Player.log", "a long first session");
        var growth = new LogGrowth();
        growth.Measure([log]);
        growth.Commit();

        File.WriteAllText(log, "new");

        Assert.That(growth.Measure([log]), Is.True);
    }

    /// <summary>
    /// The rotation race itself (#132): between the check that the log is there and the
    /// question of how big it is, Arena's restart deletes it. A log that is not there to
    /// be measured is a log with nothing to say this tick, not an exception out of the
    /// poll loop and out of Main.
    /// </summary>
    [Test]
    public void A_log_that_is_not_there_is_not_an_error()
    {
        var growth = new LogGrowth();

        Assert.That(growth.Measure([Path.Combine(_dir, "Player.log")]), Is.False);
    }

    /// <summary>
    /// The framework behaviour the fix rests on, pinned because nothing else would
    /// notice it changing. <see cref="LogGrowth"/> closes the race by asking one
    /// <see cref="FileInfo"/> both questions instead of asking the file system twice;
    /// if <c>Length</c> ever went back to the disk, the race would quietly return and
    /// every test above would still pass, because none of them can delete a file
    /// between two statements inside a private method.
    /// </summary>
    /// <remarks>
    /// The second half is the shape `watch` actually had, and it throws — which is the
    /// whole of #132, reproduced here without waiting for Arena to restart.
    /// </remarks>
    [Test]
    public void One_FileInfo_answers_both_questions_from_the_same_snapshot()
    {
        var log = Log("Player.log", "0123456789");

        var file = new FileInfo(log);
        Assert.That(file.Exists, Is.True, "which is what fills the snapshot");
        File.Delete(log);

        Assert.That(file.Length, Is.EqualTo(10), "answered from the snapshot, not the disk");

        var second = Log("Player-prev.log", "0123456789");
        var seen = File.Exists(second);
        File.Delete(second);
        Assert.That(seen, Is.True);
        Assert.Throws<FileNotFoundException>(() => _ = new FileInfo(second).Length,
            "asking twice is what ended `watch` on every Arena restart that won the race");
    }

    [Test]
    public void A_log_that_disappears_does_not_hide_another_logs_growth()
    {
        var current = Log("Player.log", "a game");
        var previous = Log("Player-prev.log", "last night");
        var growth = new LogGrowth();
        growth.Measure([current, previous]);
        growth.Commit();

        Append(current, " and another game");
        File.Delete(previous);

        Assert.That(growth.Measure([current, previous]), Is.True);
    }

    /// <summary>
    /// A log that has gone for good must not read as changed forever: the poll would
    /// then capture on every tick, three seconds apart, for as long as `watch` runs.
    /// </summary>
    [Test]
    public void A_log_that_stays_gone_stops_being_growth_once_committed()
    {
        var current = Log("Player.log", "a game");
        var previous = Log("Player-prev.log", "last night");
        var growth = new LogGrowth();
        growth.Measure([current, previous]);
        growth.Commit();

        File.Delete(previous);

        Assert.That(growth.Measure([current, previous]), Is.False);
    }

    /// <summary>
    /// The half of #132 a bare try/catch would have left behind. The old loop recorded
    /// each size as it read it, before the capture ran, so a capture that threw was a
    /// capture the loop believed had already happened. While Arena keeps writing the
    /// next poll sees more growth and the miss heals; on the poll after the night's last
    /// match nothing else is coming, and the match waits for tomorrow.
    /// </summary>
    [Test]
    public void Growth_that_was_measured_but_never_committed_is_offered_again()
    {
        var log = Log("Player.log", "a game");
        var growth = new LogGrowth();
        growth.Measure([log]);
        growth.Commit();

        Append(log, " that ended the night");
        Assert.That(growth.Measure([log]), Is.True, "the growth is seen");

        // The capture throws here — so no Commit — and nothing else is ever written to
        // this log again.
        Assert.That(growth.Measure([log]), Is.True, "and is still owed on the next poll");

        growth.Commit();
        Assert.That(growth.Measure([log]), Is.False, "until the capture that took it returned");
    }
}
