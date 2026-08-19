using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// How <c>mtga-pbp why</c> reads the turns it was asked for.
/// </summary>
/// <remarks>
/// The bug these were written for is that <c>why &lt;id&gt; 13 14</c> rendered turn 13
/// and never mentioned 14. Nothing said the second operand had been read and thrown
/// away, which is the half that cost the time: the output looked like an answer.
/// <para>
/// The reading is tested and the rendering is not, because <c>Why.Run</c> opens Arena's
/// own card database — 237 MB, and not on CI. Everything that decides what is shown is
/// therefore in <c>ParseTurns</c> and <c>Plan</c>, which need no files at all.
/// </para>
/// </remarks>
public class WhyTests
{
    private static IReadOnlyCollection<int> Turns(int last) => Enumerable.Range(1, last).ToHashSet();

    [Test]
    public void Several_turns_are_all_read()
    {
        var (turns, unreadable) = Why.ParseTurns(["13", "14"]);

        Assert.That(turns, Is.EqualTo(new[] { 13, 14 }));
        Assert.That(unreadable, Is.Empty);
    }

    [Test]
    public void A_range_includes_both_ends()
    {
        var (turns, _) = Why.ParseTurns(["13-15"]);

        Assert.That(turns, Is.EqualTo(new[] { 13, 14, 15 }));
    }

    /// <summary>
    /// The form the issue was reported from. PowerShell's <c>13, 14</c> reaches an exe as
    /// two arguments, the first with the comma still attached, so the most natural thing
    /// to type was the one that silently rendered a single turn.
    /// </summary>
    [Test]
    public void A_comma_separates_turns_the_way_PowerShell_leaves_them()
    {
        Assert.That(Why.ParseTurns(["13,", "14"]).Turns, Is.EqualTo(new[] { 13, 14 }));
        Assert.That(Why.ParseTurns(["13,14"]).Turns, Is.EqualTo(new[] { 13, 14 }));
        Assert.That(Why.ParseTurns(["13,", "14"]).Unreadable, Is.Empty);
    }

    /// <summary>
    /// A shell that was told to quote the whole thing hands over one argument. Reading
    /// it is free; refusing it would be pedantry.
    /// </summary>
    [Test]
    public void Turns_quoted_into_one_argument_are_still_several()
    {
        var (turns, unreadable) = Why.ParseTurns(["13 14"]);

        Assert.That(turns, Is.EqualTo(new[] { 13, 14 }));
        Assert.That(unreadable, Is.Empty);
    }

    [Test]
    public void A_reversed_range_is_read_ascending()
    {
        var (turns, unreadable) = Why.ParseTurns(["15-13"]);

        Assert.That(turns, Is.EqualTo(new[] { 13, 14, 15 }));
        Assert.That(unreadable, Is.Empty);
    }

    [Test]
    public void Turns_arrive_sorted_and_asked_for_once()
    {
        var (turns, _) = Why.ParseTurns(["14", "13", "13-15", "14"]);

        Assert.That(turns, Is.EqualTo(new[] { 13, 14, 15 }));
    }

    [Test]
    public void A_word_is_kept_rather_than_dropped()
    {
        var (turns, unreadable) = Why.ParseTurns(["banana"]);

        Assert.That(turns, Is.Empty);
        Assert.That(unreadable, Is.EqualTo(new[] { "banana" }));
    }

    [Test]
    public void Something_shaped_like_a_range_but_is_not_one_is_unreadable()
    {
        foreach (var operand in new[] { "13-", "-14", "1-2-3", "-1" })
        {
            var (turns, unreadable) = Why.ParseTurns([operand]);

            Assert.That(turns, Is.Empty, $"{operand} yielded turns");
            Assert.That(unreadable, Is.EqualTo(new[] { operand }));
        }
    }

    [Test]
    public void A_number_too_big_to_be_a_turn_is_not_one()
    {
        Assert.That(Why.ParseTurns(["99999999999999"]).Unreadable, Is.Not.Empty);
    }

    /// <summary>
    /// Turn numbering starts at 1, so zero is an operand that could not be read rather
    /// than a turn the match stopped short of. The distinction is the whole difference
    /// between "cannot read 0 as a turn" and the nonsense "this match has no turn 0,
    /// its turns run 1 to 22".
    /// </summary>
    [Test]
    public void Zero_is_not_a_turn_the_match_missed_it_is_not_a_turn()
    {
        foreach (var operand in new[] { "0", "0-5", "-3-5" })
        {
            var (turns, unreadable) = Why.ParseTurns([operand]);

            Assert.That(turns, Is.Empty, $"{operand} yielded turns");
            Assert.That(unreadable, Is.EqualTo(new[] { operand }));
        }

        // And it travels: alone it lists the turns, beside a real one it refuses.
        Assert.That(Why.Plan(["0"], Turns(14)).Outcome, Is.EqualTo(WhyOutcome.ListTurns));
        Assert.That(Why.Plan(["13", "0"], Turns(14)).Outcome, Is.EqualTo(WhyOutcome.Refuse));
        Assert.That(Why.Plan(["13", "0"], Turns(14)).ExitCode, Is.EqualTo(2));
    }

    /// <summary>
    /// A range is expanded to reach the turns inside it, so an absurd one has to be
    /// refused while it is still two integers rather than after it is two billion.
    /// </summary>
    [Test]
    public void An_absurd_range_is_refused_before_it_is_built()
    {
        var (turns, unreadable) = Why.ParseTurns(["1-2000000000"]);

        Assert.That(turns, Is.Empty);
        Assert.That(unreadable, Is.EqualTo(new[] { "1-2000000000" }));
    }

    [Test]
    public void Every_turn_asked_for_is_rendered()
    {
        var plan = Why.Plan(["13", "14"], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.Render));
        Assert.That(plan.Turns, Is.EqualTo(new[] { 13, 14 }));
        Assert.That(plan.Complaint, Is.Null);
        Assert.That(plan.ExitCode, Is.Zero);
    }

    [Test]
    public void No_turn_at_all_lists_the_match_turns()
    {
        var plan = Why.Plan([], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.ListTurns));
        Assert.That(plan.Complaint, Is.Null);
        Assert.That(plan.ExitCode, Is.Zero);
    }

    /// <summary>
    /// <c>why &lt;id&gt; banana</c> falling through to the turn list started as an
    /// accident of the old parse, and it is how people find out what turns exist. It is
    /// kept deliberately — with a line saying which word could not be read.
    /// </summary>
    [Test]
    public void A_word_alone_still_lists_the_turns_and_says_why()
    {
        var plan = Why.Plan(["banana"], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.ListTurns));
        Assert.That(plan.Complaint, Does.Contain("banana"));
        Assert.That(plan.ExitCode, Is.Zero);
    }

    /// <summary>
    /// The regression this whole change exists for. A request that was only half
    /// understood must not quietly become a smaller request that succeeds.
    /// </summary>
    [Test]
    public void A_half_read_request_renders_nothing_and_says_which_half()
    {
        var plan = Why.Plan(["13", "banana"], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.Refuse));
        Assert.That(plan.Turns, Is.Empty, "turn 13 alone is the bug, not the fix");
        Assert.That(plan.Complaint, Does.Contain("banana"));
        Assert.That(plan.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void A_turn_the_match_never_reached_is_reported_with_the_ones_it_did()
    {
        var plan = Why.Plan(["40"], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.Refuse));
        Assert.That(plan.Complaint, Does.Contain("no turn 40"));
        Assert.That(plan.Complaint, Does.Contain("1 to 14"));
        Assert.That(plan.ExitCode, Is.EqualTo(4));
    }

    /// <summary>
    /// A range running off the end of the match is a typo worth reporting. Clamping it
    /// to what exists would be the same quiet truncation this change removes.
    /// </summary>
    [Test]
    public void A_range_off_the_end_of_the_match_is_refused_rather_than_clamped()
    {
        var plan = Why.Plan(["13-40"], Turns(14));

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.Refuse));
        Assert.That(plan.Turns, Is.Empty);
        Assert.That(plan.Complaint, Does.Contain("no turns 15"));
        Assert.That(plan.Complaint, Does.Contain("and 18 more"), "a complaint stops naming eventually");
        Assert.That(plan.ExitCode, Is.EqualTo(4));
    }

    [Test]
    public void A_match_with_no_turns_says_that_rather_than_naming_a_range()
    {
        var plan = Why.Plan(["1"], []);

        Assert.That(plan.Outcome, Is.EqualTo(WhyOutcome.Refuse));
        Assert.That(plan.Complaint, Is.EqualTo("this match has no turns"));
        Assert.That(plan.ExitCode, Is.EqualTo(4));
    }
}
