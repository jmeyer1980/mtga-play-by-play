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
    private string _originalVersion = null!;

    // Match times render in local time, and the build stamp carries the commit, so
    // without pinning both the golden file would only match on the machine and the
    // commit that generated it.
    [OneTimeSetUp]
    public void PinEnvironment()
    {
        _originalZone = TranscriptSummary.DisplayTimeZone;
        TranscriptSummary.DisplayTimeZone = TimeZoneInfo.Utc;
        _originalVersion = BuildInfo.Version;
        BuildInfo.Version = PinnedVersion;
    }

    [OneTimeTearDown]
    public void RestoreEnvironment()
    {
        TranscriptSummary.DisplayTimeZone = _originalZone;
        BuildInfo.Version = _originalVersion;
    }

    /// <summary>Stands in for whatever commit the suite happens to be running at.</summary>
    internal const string PinnedVersion = "0.0.0-test";

    internal static string FixtureDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    /// <summary>
    /// The single-game sample, and the id it is extracted under. Stored gzipped: 1.1 MB
    /// of raw engine traffic compresses to 56 KB, which is a reasonable thing to check in.
    /// </summary>
    internal const string SampleFixture = "sample-match.json.gz";
    internal const string SampleMatchId = "sample-match-0001";

    /// <summary>
    /// The Bo3 sample. Scrubbed the same way <see cref="SampleFixture"/> was — both
    /// screen names, both user ids, both session ids and the match id are replaced —
    /// and otherwise byte-for-byte the traffic Arena wrote, because the whole point of
    /// it is that multi-game behaviour was previously guessed at rather than observed.
    /// </summary>
    internal const string Bo3Fixture = "bo3-match.json.gz";
    internal const string Bo3MatchId = "sample-bo3-0001";

    /// <summary>The rendered documents each fixture is pinned against.</summary>
    internal const string SampleGolden = "sample-match.expected.md";
    internal const string Bo3Golden = "bo3-match.expected.md";

    internal static string[] ReadFixture(string fileName = SampleFixture)
    {
        using var fs = File.OpenRead(Path.Combine(FixtureDir, fileName));
        using var gz = new System.IO.Compression.GZipStream(
            fs, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    /// <summary>
    /// Extracts one of the checked-in fixtures against the checked-in card names, so
    /// every end-to-end test runs on CI rather than only where Arena is installed.
    /// </summary>
    internal static Transcript ExtractFixture(string fileName, string matchId) =>
        new EventExtractor(FixtureCardDb.Load(FixtureDir))
            .Extract(matchId, ReadFixture(fileName));

    // Golden files live beside the source, not in the copied output directory, so
    // regenerating one updates the checked-in copy.
    internal static string GoldenPath(string name) => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "..", "..", "..", "Fixtures", name);

    /// <summary>
    /// Compares a rendered document against its checked-in copy.
    /// </summary>
    /// <remarks>
    /// The first run writes the file and fails on purpose. A golden file regenerated
    /// without being read stops being a test, and failing is what makes somebody look —
    /// CONTRIBUTING spells the two-run flow out for exactly that reason.
    /// </remarks>
    private static void AssertMatchesGolden(string name, string rendered)
    {
        var path = GoldenPath(name);
        var actual = rendered.ReplaceLineEndings("\n");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            Assert.Fail($"Golden file created at {Path.GetFullPath(path)}. " +
                        "Review it for sanity, then re-run.");
        }

        Assert.That(actual, Is.EqualTo(File.ReadAllText(path).ReplaceLineEndings("\n")));
    }

    /// <summary>
    /// Runs against the checked-in card-name fixture rather than Arena's 237 MB
    /// database, so this end-to-end check runs on CI and not only on a machine with
    /// the game installed. <see cref="CardDbIntegrationTests"/> covers the real
    /// database separately.
    /// </summary>
    private static Transcript Extract() => ExtractFixture(SampleFixture, SampleMatchId);

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
        // Read from UnresolvedNames, not CardsSeen. SawCard strips placeholders out of
        // CardsSeen, so the old form of this test looked for them in the one set they
        // could never be in and passed no matter how many the parser emitted.
        var byGrpId = Extract().UnresolvedNames
            .Where(p => p.Key.StartsWith("Card #", StringComparison.Ordinal)).ToList();
        Assert.That(byGrpId, Is.Empty,
            $"grpIds missing from the card database: {string.Join(", ", byGrpId.Select(p => $"{p.Key} x{p.Value}"))}");
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
    public void Rendered_markdown_matches_the_golden_file() =>
        AssertMatchesGolden(SampleGolden, MarkdownRenderer.Render(Extract()));

    /// <summary>
    /// The same document check over the Bo3, which had none.
    /// </summary>
    /// <remarks>
    /// <see cref="MultiGameTests"/> asks the multi-game path about twenty specific
    /// questions and gets twenty right answers, but nothing has ever read the whole
    /// rendered match as one document. Everything between the assertions is therefore
    /// unpinned: the order the two games appear in, the blank lines around each game
    /// heading, where a game's result line sits relative to the next game's opening,
    /// and what the subtitle says once there is more than one game to count. Any of
    /// those could change today and the suite would stay green (#136).
    /// <para>
    /// It is the cheapest coverage in the project — the fixture, the extraction and the
    /// environment pinning were all already here, so this is one method and one checked
    /// -in file — and it covers precisely the part the targeted assertions cannot,
    /// because a targeted assertion only sees what somebody already thought to ask.
    /// </para>
    /// </remarks>
    [Test]
    public void Rendered_bo3_markdown_matches_the_golden_file() =>
        AssertMatchesGolden(Bo3Golden,
            MarkdownRenderer.Render(ExtractFixture(Bo3Fixture, Bo3MatchId)));

    /// <summary>
    /// The sample match is the case the land rule exists for: a mono-white deck whose
    /// mana base includes Temple of Enlightenment, Temple of Silence and Temple of
    /// Plenty. Counting lands would report it as a four-colour deck — which is the
    /// whole reason they are left out — so this is asserted against the real decklist
    /// rather than a hand-built one.
    /// </summary>
    [Test]
    public void The_deck_is_named_by_the_colours_of_its_spells_not_of_its_lands()
    {
        Assert.That(Extract().DeckColors, Is.EqualTo("W"));
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

    /// <summary>
    /// The sample match attaches exactly one thing: an aura, cast at a creature. Its
    /// AnnotationType_AttachmentCreated therefore has to stay silent, because the cast
    /// line two lines above already says what it went onto and what it did there.
    /// Asserted against real traffic rather than a hand-built fixture, since the whole
    /// question is whether Arena files the target where the suppression looks for it.
    /// </summary>
    [Test]
    public void An_aura_the_cast_line_already_accounts_for_adds_no_second_line()
    {
        var t = Extract();

        Assert.That(t.Events.Any(e => e.Kind == EventKind.Attached), Is.False,
            "the only attachment in this match is an aura whose target is already reported");

        var lines = Narrator.Narrate(t, Density.Verbose);
        Assert.That(
            lines.Any(l => l.Text.Contains("is attached to", StringComparison.Ordinal)),
            Is.False);
    }
}
