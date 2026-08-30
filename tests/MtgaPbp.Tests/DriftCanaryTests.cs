using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class DriftCanaryTests
{
    private static ScanStats Read(long jsonLines) => new() { JsonLines = jsonLines };

    [Test]
    public void A_capture_that_recognized_matches_never_warns()
    {
        Assert.That(DriftCanary.Warn(Read(500_000), slicesSeen: 1), Is.Null);
    }

    [Test]
    public void A_quiet_log_never_warns()
    {
        // A browse-and-quit Arena session, or a log that is mostly rotated away:
        // little traffic and no matches is the everyday case, not drift.
        Assert.That(DriftCanary.Warn(Read(DriftCanary.RecordFloor - 1), slicesSeen: 0), Is.Null);
    }

    [Test]
    public void A_busy_log_with_no_recognizable_match_warns()
    {
        var warning = DriftCanary.Warn(Read(350_000), slicesSeen: 0);

        // Formatted the same way the message formats it, so the assertion holds on
        // machines whose culture does not group thousands with a comma.
        Assert.That(warning, Does.Contain(350_000.ToString("N0")));
        Assert.That(warning, Does.Contain("without recognizing a single match"));
    }

    [Test]
    public void The_floor_itself_is_enough_to_warn()
    {
        Assert.That(DriftCanary.Warn(Read(DriftCanary.RecordFloor), slicesSeen: 0), Is.Not.Null);
    }

    [Test]
    public void Already_archived_matches_still_count_as_recognized()
    {
        // slicesSeen counts what the slicer produced, not what the archive accepted —
        // a night of re-captured matches writes nothing and must not warn.
        Assert.That(DriftCanary.Warn(Read(350_000), slicesSeen: 8), Is.Null);
    }
}
