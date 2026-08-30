using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The rule that stands between a mistyped cap and an archive with no undo behind it
/// (#133).
/// </summary>
public class RetentionGuardTests
{
    /// <summary>
    /// The case the guard exists for, in the numbers the issue was filed with: a cap of
    /// 50 read off the README and typed against an archive of 1,217.
    /// </summary>
    [Test]
    public void A_cap_typed_against_a_full_archive_is_large()
    {
        Assert.That(RetentionGuard.WouldBeLarge(doomed: 1167, archived: 1217), Is.True);
    }

    /// <summary>
    /// The steady state, which is what the feature is actually for: the cap is met, one
    /// more match arrives, one old one leaves. Held back here and the cap would never
    /// work at all.
    /// </summary>
    [Test]
    public void One_match_making_room_for_another_is_never_large()
    {
        Assert.That(RetentionGuard.WouldBeLarge(doomed: 1, archived: 60), Is.False);
    }

    /// <summary>
    /// Both conditions have to hold, so a small archive is not nagged over a handful of
    /// matches even when they are a large share of it.
    /// </summary>
    [TestCase(3, 20, false, "15% of the archive, but only three matches")]
    [TestCase(10, 20, false, "half the archive, and still only ten matches")]
    [TestCase(11, 20, true, "past both: eleven matches and 55%")]
    public void The_count_floor_and_the_share_both_apply(
        int doomed, int archived, bool large, string why)
    {
        Assert.That(RetentionGuard.WouldBeLarge(doomed, archived), Is.EqualTo(large), why);
    }

    /// <summary>
    /// And the share alone does not wave a big prune through: 100 matches is a tenth of
    /// a thousand exactly, which is not more than a tenth.
    /// </summary>
    [TestCase(100, 1000, false)]
    [TestCase(101, 1000, true)]
    public void The_share_is_a_strict_threshold(int doomed, int archived, bool large)
    {
        Assert.That(RetentionGuard.WouldBeLarge(doomed, archived), Is.EqualTo(large));
    }

    /// <summary>Nothing to delete is never large, whatever the archive looks like.</summary>
    [Test]
    public void Nothing_to_prune_is_not_large()
    {
        Assert.That(RetentionGuard.WouldBeLarge(doomed: 0, archived: 1217), Is.False);
    }
}
