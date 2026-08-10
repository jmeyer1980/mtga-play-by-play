using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// End-to-end coverage over one real, anonymized match. This is the test that catches
/// parser regressions, because it exercises scanner → tracker → extractor → narrator
/// together, which is where they actually appear.
/// </summary>
public class GoldenFileTests
{
    private static string FixtureDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    // Stored gzipped: 1.1 MB of raw engine traffic compresses to 56 KB, which is a
    // reasonable thing to check in.
    private static string SamplePath => Path.Combine(FixtureDir, "sample-match.json.gz");

    private static string[] ReadFixture()
    {
        using var fs = File.OpenRead(SamplePath);
        using var gz = new System.IO.Compression.GZipStream(
            fs, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    // The golden file lives beside the source, not in the copied output directory,
    // so regenerating it updates the checked-in copy.
    private static string GoldenPath => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "..", "..", "..", "Fixtures", "sample-match.expected.md");

    private static Transcript Extract()
    {
        var dbPath = CardDb.FindDatabase(null);
        if (dbPath is null)
            Assert.Ignore("MTG Arena card database not present; this test needs Arena installed.");

        using var db = new CardDb(dbPath!);
        return new EventExtractor(db).Extract("sample-match-0001", ReadFixture());
    }

    [Test]
    public void Real_match_produces_a_transcript_with_both_players_and_a_result()
    {
        var t = Extract();
        Assert.That(t.You, Is.Not.Null);
        Assert.That(t.Opponent, Is.Not.Null);
        Assert.That(t.Events, Is.Not.Empty);
        Assert.That(t.Incomplete, Is.False);
        Assert.That(t.WinningTeamId, Is.Not.Null);
    }

    [Test]
    public void Real_match_names_are_resolved_not_placeholders()
    {
        var placeholders = Extract().CardsSeen
            .Where(c => c.StartsWith("Card #", StringComparison.Ordinal)).ToList();
        Assert.That(placeholders, Is.Empty,
            $"unresolved card names: {string.Join(", ", placeholders)}");
    }

    [Test]
    public void Real_match_leaves_no_annotation_type_unhandled()
    {
        var unknown = Extract().UnknownAnnotations;
        Assert.That(unknown, Is.Empty,
            $"unhandled: {string.Join(", ", unknown.Select(k => $"{k.Key} x{k.Value}"))}");
    }

    [Test]
    public void Real_match_covers_the_core_transcript_beats()
    {
        var kinds = Extract().Events.Select(e => e.Kind).ToHashSet();
        Assert.That(kinds, Does.Contain(EventKind.TurnStart));
        Assert.That(kinds, Does.Contain(EventKind.LandPlayed));
        Assert.That(kinds, Does.Contain(EventKind.SpellCast));
        Assert.That(kinds, Does.Contain(EventKind.Damage));
        Assert.That(kinds, Does.Contain(EventKind.LifeChanged));
        Assert.That(kinds, Does.Contain(EventKind.Attack));
        Assert.That(kinds, Does.Contain(EventKind.Block));
        Assert.That(kinds, Does.Contain(EventKind.GameEnd));
    }

    [Test]
    public void Real_match_turns_are_numbered_from_one_and_never_go_backwards()
    {
        var turns = Extract().Events
            .Where(e => e.Kind == EventKind.TurnStart)
            .Select(e => e.Turn).ToList();

        Assert.That(turns, Is.Not.Empty);
        Assert.That(turns[0], Is.EqualTo(1), "first turn must be 1, not 0");
        Assert.That(turns, Is.Ordered, "turn numbers must not decrease");
    }

    [Test]
    public void Rendered_markdown_matches_the_golden_file()
    {
        var actual = MarkdownRenderer.Render(Extract()).ReplaceLineEndings("\n");

        if (!File.Exists(GoldenPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, actual);
            Assert.Fail($"Golden file created at {Path.GetFullPath(GoldenPath)}. " +
                        "Review it for sanity, then re-run.");
        }

        Assert.That(actual, Is.EqualTo(File.ReadAllText(GoldenPath).ReplaceLineEndings("\n")));
    }

    [Test]
    public void Declared_targets_are_reported_as_effects_not_targets()
    {
        // Accepted limitation, not a regression: SelectTargetsReq is sent only to the
        // player who must choose, and PlayerSubmittedTargets carries no target ids, so
        // the opponent's declared targets are simply not in the log. Interactions are
        // reported as observed effects instead. If Arena ever starts emitting target
        // ids, the first assertion is what will tell us.
        var hasTargetWording = Narrator.Narrate(Extract(), Density.Beats)
            .Any(l => l.Text.Contains("targeting", StringComparison.OrdinalIgnoreCase));

        Assert.That(hasTargetWording, Is.False,
            "transcript should phrase interactions as effects, not declared targets");
        Assert.Warn(
            "Declared targets are unavailable from the Arena log; interactions are " +
            "reported as observed effects. See docs/superpowers/specs/" +
            "2026-08-10-mtga-play-by-play-design.md.");
    }
}
