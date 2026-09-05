using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Which commands say the published build has fallen behind the working copy, and which
/// keep it to themselves.
/// </summary>
/// <remarks>
/// The warning is only ever actionable on a command that writes a stamped file: a
/// re-publish changes what <c>build</c> and <c>watch</c> put on disk, and changes
/// nothing at all about what <c>why</c> or <c>stats</c> print. It used to fire on every
/// command, so a docs-only merge made <c>--version</c> nag about output it was not
/// writing (#196).
/// <para>
/// These build a <c>.git</c> out of plain files, the same way <see cref="WorkingCopyTests"/>
/// does, because that is the whole of what <see cref="WorkingCopy"/> understands. Going
/// through a real tree rather than a stubbed note is the point: it proves the quiet
/// commands are quiet <i>while genuinely stale</i>, which a test of the command list
/// alone would not.
/// </para>
/// </remarks>
public class BannerTests
{
    private string _root = null!;

    /// <summary>A build stamped with a commit the fake working copy has moved past.</summary>
    private const string Behind = "0.7.0+1111111a";

    private const string Head = "2222222bccddeeff00112233445566778899aabb";

    [SetUp]
    public void MakeTempTree() =>
        _root = Directory.CreateTempSubdirectory("mtga-banner").FullName;

    [TearDown]
    public void RemoveTempTree()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Writes a .git at the root and returns a nested "dist" beneath it.</summary>
    private string Repo(string headSha)
    {
        var git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        var refs = Path.Combine(git, "refs", "heads");
        Directory.CreateDirectory(refs);
        File.WriteAllText(Path.Combine(refs, "main"), headSha);

        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        return dist;
    }

    [TestCase("build")]
    [TestCase("watch")]
    [TestCase("all")]
    public void A_command_that_writes_a_stamped_file_says_the_build_is_behind(string command)
    {
        var dist = Repo(Head);
        Assert.That(Banner.StaleNoteFor(command, Behind, dist),
            Does.Contain("Re-publish"));
    }

    [TestCase("capture")]
    [TestCase("stats")]
    [TestCase("why")]
    [TestCase("collection")]
    [TestCase("keep")]
    [TestCase("unkeep")]
    [TestCase("--version")]
    [TestCase("--help")]
    public void A_command_that_writes_no_stamped_file_stays_quiet(string command)
    {
        var dist = Repo(Head);
        Assert.That(Banner.StaleNoteFor(command, Behind, dist), Is.Null);
    }

    /// <summary>
    /// capture earns its place in the quiet column rather than falling into it: it does
    /// write, but it writes to the archive, and the archive carries no build stamp. Only
    /// rendering does, which is why re-publishing before a capture changes nothing.
    /// </summary>
    [Test]
    public void Capture_is_quiet_because_the_archive_carries_no_stamp()
    {
        var dist = Repo(Head);
        Assert.That(Banner.StaleNoteFor("capture", Behind, dist), Is.Null);
        Assert.That(Banner.StaleNoteFor("build", Behind, dist), Is.Not.Null);
    }

    [Test]
    public void A_build_that_matches_the_working_copy_says_nothing_either_way()
    {
        var dist = Repo(Head);
        var current = "0.7.0+" + Head[..8];
        Assert.That(Banner.StaleNoteFor("build", current, dist), Is.Null);
        Assert.That(Banner.StaleNoteFor("why", current, dist), Is.Null);
    }

    /// <summary>An unknown word reaches the banner before it reaches the usage text.</summary>
    [Test]
    public void An_unrecognised_command_stays_quiet()
    {
        var dist = Repo(Head);
        Assert.That(Banner.StaleNoteFor("frobnicate", Behind, dist), Is.Null);
    }

    [Test]
    public void The_banner_names_the_version_whether_or_not_there_is_a_note()
    {
        Assert.That(Banner.Compose(art: false, "0.7.0+abcdef12", null),
            Does.Contain("mtga-pbp 0.7.0+abcdef12").And.Not.Contains("note:"));
        Assert.That(Banner.Compose(art: false, "0.7.0+abcdef12", "note: behind"),
            Does.Contain("mtga-pbp 0.7.0+abcdef12").And.Contains("note: behind"));
    }
}
