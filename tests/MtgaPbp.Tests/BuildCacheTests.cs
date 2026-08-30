using MtgaPbp.Cli;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The rules that decide whether a build may leave a match alone (#122). Getting one of
/// these wrong does not throw — it serves a stale page — so they are asserted one at a
/// time.
/// </summary>
public class BuildCacheTests
{
    private string _out = null!;
    private string _page = null!;
    private string _text = null!;

    private const long Size = 4096;
    private const long Modified = 1786326812781;
    private const string CardDb = "C:/cards.mtga|1786000000000";

    private static readonly Neighbours Around =
        new("newer-id", "2026-08-10 02:10", "older-id", "2026-08-10 01:40");

    [SetUp]
    public void SetUp()
    {
        _out = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cache_{Guid.NewGuid():N}")).FullName;
        _page = Path.Combine(_out, "m1.html");
        _text = Path.Combine(_out, "m1.md");
        File.WriteAllText(_page, "<html></html>");
        File.WriteAllText(_text, "# match");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_out, recursive: true);

    private static CachedMatch Entry() => new(
        Size, Modified,
        Around.NewerId, Around.NewerWhen, Around.OlderId, Around.OlderWhen,
        IndexRenderer.Summarize(RendererTests.Sample()),
        ["Some Unresolved Card"]);

    /// <summary>A cache written by one build and read by the next, with nothing moved.</summary>
    private BuildCache Saved()
    {
        var writing = BuildCache.Load(_out);
        writing.Keep("m1", Entry());
        writing.Save(_out, CardDb);
        return BuildCache.Load(_out);
    }

    private CachedMatch? Ask(BuildCache c, long size = Size, long modified = Modified,
                             Neighbours? around = null, string cardDb = CardDb,
                             string? page = null, string? text = null) =>
        c.Reusable("m1", size, modified, around ?? Around,
                   page ?? _page, text ?? _text, cardDb);

    [Test]
    public void Nothing_moved_means_the_match_can_be_left_alone()
    {
        var hit = Ask(Saved());

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Summary.MatchId, Is.EqualTo("abc-123"));
        Assert.That(hit.Unresolved, Is.EqualTo(new[] { "Some Unresolved Card" }));
    }

    /// <summary>The archived slice was rewritten — a healed match, or a fuller capture.</summary>
    [Test]
    public void A_slice_of_a_different_size_is_rebuilt() =>
        Assert.That(Ask(Saved(), size: Size + 1), Is.Null);

    [Test]
    public void A_slice_written_at_a_different_time_is_rebuilt() =>
        Assert.That(Ask(Saved(), modified: Modified + 1), Is.Null);

    /// <summary>
    /// Appending a match rewrites the links on the one that used to be newest, so its
    /// page is wrong even though its own slice never moved. Exactly one extra page.
    /// </summary>
    [Test]
    public void A_page_whose_neighbours_changed_is_rebuilt() =>
        Assert.That(Ask(Saved(), around: Around with { NewerId = "somebody-else" }), Is.Null);

    /// <summary>Including when only the date beside the link moved.</summary>
    [Test]
    public void A_page_whose_neighbour_date_changed_is_rebuilt() =>
        Assert.That(Ask(Saved(), around: Around with { OlderWhen = "2026-08-09 23:00" }), Is.Null);

    /// <summary>
    /// Every name, face and line of ability text comes out of the card database, so an
    /// updated one can change a page whose match never moved.
    /// </summary>
    [Test]
    public void A_new_card_database_rebuilds_everything() =>
        Assert.That(Ask(Saved(), cardDb: "C:/cards.mtga|1799999999999"), Is.Null);

    /// <summary>The only thing standing between a deleted page and a report that links to it.</summary>
    [Test]
    public void A_missing_output_file_is_rebuilt()
    {
        var cache = Saved();
        File.Delete(_page);
        Assert.That(Ask(cache), Is.Null);

        File.WriteAllText(_page, "<html></html>");
        File.Delete(_text);
        Assert.That(Ask(cache), Is.Null, "the markdown counts as much as the page");
    }

    [Test]
    public void A_match_the_previous_build_never_saw_is_rebuilt() =>
        Assert.That(BuildCache.Load(_out).Reusable(
            "never-seen", Size, Modified, Around, _page, _text, CardDb), Is.Null);

    /// <summary>
    /// A new build of the tool throws the whole cache away. A renderer constant bumped by
    /// hand would have been one forgotten edit away from serving stale pages forever;
    /// this cannot be forgotten, and costs one full rebuild per upgrade.
    /// </summary>
    [Test]
    public void A_different_build_of_the_tool_throws_the_cache_away()
    {
        var was = BuildInfo.Version;
        try
        {
            Saved();
            BuildInfo.Version = "9.9.9+deadbeef";
            Assert.That(Ask(BuildCache.Load(_out)), Is.Null);
        }
        finally
        {
            BuildInfo.Version = was;
        }
    }

    /// <summary>--rebuild asks for the work to be done again, so the cache is not read.</summary>
    [Test]
    public void Ignoring_the_cache_reuses_nothing()
    {
        Saved();
        Assert.That(Ask(BuildCache.Load(_out, ignore: true)), Is.Null);
    }

    /// <summary>
    /// A match that has left the archive leaves the cache with it — only what a build
    /// actually saw is written back.
    /// </summary>
    [Test]
    public void A_match_that_stopped_being_archived_is_forgotten()
    {
        Saved();

        var next = BuildCache.Load(_out);
        Assert.That(Ask(next), Is.Not.Null, "still there before the build that drops it");
        next.Save(_out, CardDb);   // a build that saw no matches at all

        Assert.That(Ask(BuildCache.Load(_out)), Is.Null);
    }

    /// <summary>
    /// Nothing here is worth failing a build over: a cache is a way of being faster,
    /// never a way of being right.
    /// </summary>
    [Test]
    public void An_unreadable_cache_is_simply_empty()
    {
        File.WriteAllText(Path.Combine(_out, ".build-cache.json"), "{ not json");
        Assert.That(Ask(BuildCache.Load(_out)), Is.Null);
    }
}
