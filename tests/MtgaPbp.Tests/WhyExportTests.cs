using System.Text.RegularExpressions;
using MtgaPbp.Cli;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The dump <c>build</c> writes beside each page and markdown (#232): the console
/// command's sections for every turn the match reached, as one file.
/// </summary>
/// <remarks>
/// Asserted structurally against the two checked-in fixtures rather than pinned as a
/// golden file: the annotation lines themselves are <see cref="LogDumpTests"/>'s
/// business, and a 100 KB golden of them would be regenerated without being read. What
/// is new here is the composition — which turns, in which order, under which headings,
/// with what around them — and that is what these check.
/// </remarks>
public class WhyExportTests
{
    private static readonly Regex Says = new(
        @"^=== turn (\d+)(?: of game (\d+))?: what the transcript says ===$", RegexOptions.Multiline);

    private static readonly Regex Log = new(
        @"^=== turn (\d+)(?: of game (\d+))?: what the log says ===$", RegexOptions.Multiline);

    private static (Transcript Transcript, string Text) Export(string fixture, string matchId)
    {
        var raw = GoldenFileTests.ReadFixture(fixture);
        var cards = FixtureCardDb.Load(GoldenFileTests.FixtureDir);
        var transcript = new EventExtractor(cards).Extract(matchId, raw);
        return (transcript, Why.Export(transcript, raw, cards, manaLedger: false));
    }

    private static List<(int Turn, int Game)> Headers(Transcript t) =>
        Narrator.Narrate(t, Density.Verbose, manaLedger: false)
            .Where(l => l.IsTurnHeader && l.Turn > 0)
            .Select(l => (l.Turn, l.Game))
            .ToList();

    [Test]
    public void One_section_of_each_kind_per_turn_the_match_reached()
    {
        var (transcript, text) = Export(GoldenFileTests.SampleFixture, GoldenFileTests.SampleMatchId);
        var headers = Headers(transcript);

        Assert.That(headers, Is.Not.Empty, "the fixture has turns to dump");
        Assert.That(Says.Matches(text).Select(m => int.Parse(m.Groups[1].Value)),
            Is.EqualTo(headers.Select(h => h.Turn)), "one transcript section per turn, in order");
        Assert.That(Log.Matches(text).Select(m => int.Parse(m.Groups[1].Value)),
            Is.EqualTo(headers.Select(h => h.Turn)), "and one log section per turn");
        Assert.That(text, Does.Not.Contain(" of game "), "a single game is not numbered");
    }

    /// <summary>
    /// Turn numbers start again in each game, so the pair is the identity of a section.
    /// Only the pairs a game actually played appear: a range over the match's turns
    /// would otherwise fill the shorter game with empty sections.
    /// </summary>
    [Test]
    public void A_multi_game_match_names_the_game_and_dumps_only_the_turns_it_reached()
    {
        var (transcript, text) = Export(GoldenFileTests.Bo3Fixture, GoldenFileTests.Bo3MatchId);
        var headers = Headers(transcript);

        Assert.That(headers.Select(h => h.Game).Distinct().Count(), Is.GreaterThan(1),
            "the fixture really is more than one game");

        var sections = Says.Matches(text)
            .Select(m => (Turn: int.Parse(m.Groups[1].Value), Game: int.Parse(m.Groups[2].Value)))
            .ToList();
        Assert.That(sections, Is.EqualTo(headers), "exactly the turns each game reached, in order");
    }

    [Test]
    public void Opens_with_the_match_and_closes_with_the_build_that_wrote_it()
    {
        var (transcript, text) = Export(GoldenFileTests.SampleFixture, GoldenFileTests.SampleMatchId);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.That(lines[0], Is.EqualTo(TranscriptSummary.Title(transcript)));
        Assert.That(lines[1], Is.EqualTo(TranscriptSummary.Subtitle(transcript)));
        Assert.That(lines.Last(l => l.Length > 0), Is.EqualTo(BuildInfo.Line));
    }

    /// <summary>
    /// The transcript lines sit under their heading the way the console prints them,
    /// and the raw annotations really are there — an export that wrote the headings
    /// and nothing under them would pass the counts above.
    /// </summary>
    [Test]
    public void Each_section_carries_its_lines()
    {
        var (transcript, text) = Export(GoldenFileTests.SampleFixture, GoldenFileTests.SampleMatchId);
        var narrated = Narrator.Narrate(transcript, Density.Verbose, manaLedger: false)
            .First(l => l.IsTurnHeader && l.Turn > 0);

        Assert.That(text, Does.Contain($"\n  {narrated.Text}\n"), "the turn header, unbulleted");
        Assert.That(text, Does.Contain("\n  - "), "the turn's narrated lines, bulleted");
        Assert.That(text, Does.Contain("\n      by "), "the annotations' subject lines");
    }
}
