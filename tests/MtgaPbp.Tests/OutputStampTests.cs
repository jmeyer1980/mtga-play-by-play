using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A rendered file should carry the time of the match it is about, not the time of the
/// build that happened to write it (#147).
/// </summary>
public class OutputStampTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() =>
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"stamp_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private string File_(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // 2026-08-10 01:53:32 UTC, the sample match's start.
    private const long SampleStart = 1786326812781;

    [Test]
    public void It_stamps_every_file_it_is_given()
    {
        var page = File_("m1.html");
        var text = File_("m1.md");

        Assert.That(OutputStamp.MatchTime(SampleStart, page, text), Is.True);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(SampleStart).UtcDateTime;
        Assert.That(File.GetLastWriteTimeUtc(page), Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)));
        Assert.That(File.GetLastWriteTimeUtc(text), Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The bug itself: two matches written by one build must not end up sharing a time.
    /// </summary>
    [Test]
    public void Two_matches_written_together_keep_their_own_times()
    {
        var older = File_("older.md");
        var newer = File_("newer.md");

        OutputStamp.MatchTime(SampleStart, older);
        OutputStamp.MatchTime(SampleStart + TimeSpan.FromDays(14).Ticks / TimeSpan.TicksPerMillisecond, newer);

        Assert.That(File.GetLastWriteTimeUtc(newer),
            Is.GreaterThan(File.GetLastWriteTimeUtc(older)),
            "sorting the directory by newest has to answer with the newer match");
    }

    /// <summary>
    /// A match with no recorded start is left alone. Stamping it with the epoch would
    /// sort it to the top of a directory as confidently as the build clock sorted it to
    /// the bottom — wrong in a new direction is not an improvement.
    /// </summary>
    [TestCase(0L)]
    [TestCase(-1L)]
    public void A_match_with_no_start_is_left_alone(long startedAtMs)
    {
        var path = File_("m1.md");
        var before = File.GetLastWriteTimeUtc(path);

        Assert.That(OutputStamp.MatchTime(startedAtMs, path), Is.False);
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(before));
    }

    /// <summary>
    /// Every failure is silence. The file's contents are already correct and already
    /// written; a timestamp is a convenience on top of that and worth no build.
    /// </summary>
    [Test]
    public void A_file_that_is_not_there_is_not_an_error()
    {
        Assert.That(OutputStamp.MatchTime(SampleStart, Path.Combine(_dir, "gone.md")), Is.False);
    }

    /// <summary>And a timestamp no filesystem will represent is refused, not thrown.</summary>
    [Test]
    public void An_impossible_timestamp_is_refused_quietly()
    {
        var path = File_("m1.md");
        Assert.That(OutputStamp.MatchTime(long.MaxValue, path), Is.False);
    }
}
