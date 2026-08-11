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
    private TimeZoneInfo _originalZone = null!;

    // Match times render in local time, so without pinning this the golden file
    // would only match on a machine in the timezone that generated it.
    [OneTimeSetUp]
    public void PinTimeZone()
    {
        _originalZone = TranscriptSummary.DisplayTimeZone;
        TranscriptSummary.DisplayTimeZone = TimeZoneInfo.Utc;
    }

    [OneTimeTearDown]
    public void RestoreTimeZone() => TranscriptSummary.DisplayTimeZone = _originalZone;

    private static string FixtureDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    // Stored gzipped: 1.1 MB of raw engine traffic compresses to 56 KB, which is a
    // reasonable thing to check in.
    private static string SamplePath => Path.Combine(FixtureDir, "sample-match.json.gz");

    internal static string[] ReadFixture()
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

    /// <summary>
    /// Runs against the checked-in card-name fixture rather than Arena's 237 MB
    /// database, so this end-to-end check runs on CI and not only on a machine with
    /// the game installed. <see cref="CardDbIntegrationTests"/> covers the real
    /// database separately.
    /// </summary>
    private static Transcript Extract() =>
        new EventExtractor(FixtureCardDb.Load(FixtureDir))
            .Extract("sample-match-0001", ReadFixture());

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

    /// <summary>
    /// This test used to assert the opposite, on the belief that Arena never sends
    /// targets. It does — as AnnotationType_TargetSpec inside persistentAnnotations,
    /// an array the parser did not read. That was a stale assumption, not an accepted
    /// limitation, so the test is inverted rather than warned about.
    /// </summary>
    [Test]
    public void Spells_report_what_they_targeted()
    {
        var withTargets = Extract().Events
            .Where(e => e.Kind == EventKind.SpellCast && e.TargetName is not null)
            .ToList();

        Assert.That(withTargets, Is.Not.Empty,
            "the sample match casts targeted spells; TargetSpec should name their targets");

        var lines = Narrator.Narrate(Extract(), Density.Beats);
        Assert.That(lines.Any(l => l.Text.Contains("targeting", StringComparison.Ordinal)),
            Is.True, "targets should reach the transcript");
    }
}
