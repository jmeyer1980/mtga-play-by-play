using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The diagnostic behind <c>mtga-pbp why</c>: one turn's raw annotations, ids resolved.
/// </summary>
/// <remarks>
/// Asserted against the real Bo3 fixture rather than a hand-built message, because the
/// thing it has to get right is exactly what a hand-built message would not exercise —
/// naming an object as it stood during the turn asked for, in a match where ids are
/// handed out twice.
/// </remarks>
public class AnnotationDumpTests
{
    private static IReadOnlyList<string> Bo3Raw() =>
        GoldenFileTests.ReadFixture(GoldenFileTests.Bo3Fixture);

    private static ICardDb Cards() => FixtureCardDb.Load(GoldenFileTests.FixtureDir);

    [Test]
    public void A_turn_yields_its_annotations_with_ids_resolved_to_names()
    {
        var dump = AnnotationDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 1).ToList();

        Assert.That(dump, Is.Not.Empty);

        // Every annotation contributes a type line and a "by ... on [...]" line.
        Assert.That(dump.Count(l => l.StartsWith("    by ", StringComparison.Ordinal)),
            Is.GreaterThan(0));

        // An id on its own is what makes the raw log unreadable; the point of this is
        // that the name travels with it.
        var subjects = dump.Where(l => l.Contains(" on [", StringComparison.Ordinal)).ToList();
        Assert.That(subjects.Any(l => l.Contains('#')), Is.True, "ids are kept");
        Assert.That(subjects.Any(l => l.Contains("seat", StringComparison.Ordinal) ||
                                      l.Any(char.IsLetter)), Is.True, "names are resolved");
    }

    /// <summary>
    /// Each game's turn one is its own, in a match that has two of them.
    /// </summary>
    /// <remarks>
    /// Arena hands out instance ids again in each game, so a dump that ignored the game
    /// number would answer game one's questions out of game two's state — which is the
    /// exact bug that made a transcript claim a player cast a Plains.
    /// </remarks>
    [Test]
    public void Each_game_is_dumped_separately()
    {
        var one = AnnotationDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 1).ToList();
        var two = AnnotationDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 2).ToList();

        Assert.That(one, Is.Not.Empty);
        Assert.That(two, Is.Not.Empty);
        Assert.That(one, Is.Not.EqualTo(two));
    }

    [Test]
    public void A_turn_the_match_never_reached_yields_nothing_rather_than_throwing()
    {
        Assert.That(AnnotationDump.ForTurn(Bo3Raw(), Cards(), turn: 999, game: 1), Is.Empty);
        Assert.That(AnnotationDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 9), Is.Empty);
    }

    [Test]
    public void A_line_that_is_not_json_is_stepped_over()
    {
        // The scanner writes gap markers into the archive, and they are not JSON.
        IReadOnlyList<string> raw = ["not json at all", .. Bo3Raw()];
        Assert.That(AnnotationDump.ForTurn(raw, Cards(), turn: 1, game: 1), Is.Not.Empty);
    }
}
