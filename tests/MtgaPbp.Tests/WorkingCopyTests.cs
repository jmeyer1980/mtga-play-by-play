using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Telling a developer their published copy has fallen behind the source beside it.
/// </summary>
/// <remarks>
/// Built after the fourth time in one night that a published exe drifted behind the
/// working copy and the drift was noticed by a person rather than by the program. The
/// report's build stamp names the commit, which only helps once you already suspect
/// something; this says it unprompted.
/// <para>
/// It reads <c>.git/HEAD</c> and the one ref that names. No git process, no network —
/// the tests build a <c>.git</c> directory out of plain files, which is the whole of
/// what it understands.
/// </para>
/// </remarks>
public class WorkingCopyTests
{
    private string _root = null!;

    [SetUp]
    public void MakeTempTree() =>
        _root = Directory.CreateTempSubdirectory("mtga-wc").FullName;

    [TearDown]
    public void RemoveTempTree()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Writes a .git at the root and returns a nested "dist" beneath it.</summary>
    private string Repo(string head, string? branchSha = null, string? packed = null)
    {
        var git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), head);

        if (branchSha is not null)
        {
            var refs = Path.Combine(git, "refs", "heads");
            Directory.CreateDirectory(refs);
            File.WriteAllText(Path.Combine(refs, "main"), branchSha);
        }
        if (packed is not null) File.WriteAllText(Path.Combine(git, "packed-refs"), packed);

        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        return dist;
    }

    [Test]
    public void A_build_matching_the_working_copy_says_nothing()
    {
        var dist = Repo("ref: refs/heads/main\n", "5c63fa3ec0ffee1234567890abcdef1234567890");
        Assert.That(WorkingCopy.StaleNote("0.3.1+5c63fa3e", dist), Is.Null);
    }

    [Test]
    public void A_build_the_working_copy_has_moved_past_says_both_commits()
    {
        var dist = Repo("ref: refs/heads/main\n", "aabbccddeeff00112233445566778899aabbccdd");

        var note = WorkingCopy.StaleNote("0.3.1+5c63fa3e", dist);
        Assert.That(note, Is.Not.Null);
        Assert.That(note, Does.Contain("5c63fa3e").And.Contain("aabbccdd"),
            "both ends of the comparison, so it is checkable rather than trusted");
    }

    [Test]
    public void A_detached_head_holds_the_commit_outright()
    {
        var dist = Repo("aabbccddeeff00112233445566778899aabbccdd\n");
        Assert.That(WorkingCopy.StaleNote("0.3.1+5c63fa3e", dist), Is.Not.Null);
        Assert.That(WorkingCopy.StaleNote("0.3.1+aabbccdd", dist), Is.Null);
    }

    [Test]
    public void A_ref_that_has_been_packed_away_is_still_found()
    {
        // git gc moves loose refs into packed-refs, and a repo that has been collected
        // would otherwise silently stop being checked.
        var dist = Repo(
            "ref: refs/heads/main\n",
            packed: "# pack-refs with: peeled fully-peeled sorted \n" +
                    "aabbccddeeff00112233445566778899aabbccdd refs/heads/main\n" +
                    "1111111111111111111111111111111111111111 refs/tags/v0.3.1\n");

        Assert.That(WorkingCopy.StaleNote("0.3.1+5c63fa3e", dist), Is.Not.Null);
    }

    /// <summary>
    /// A released copy has no working copy above it and is never warned about anything.
    /// </summary>
    /// <remarks>
    /// This is the case that matters most: the warning is for whoever is building the
    /// thing, and it would be noise — and slightly alarming — on a machine that only
    /// unzipped a release.
    /// </remarks>
    [Test]
    public void An_exe_outside_any_working_copy_is_left_alone()
    {
        var lonely = Path.Combine(_root, "Downloads", "mtga-pbp-v0.3.1-win-x64");
        Directory.CreateDirectory(lonely);
        Assert.That(WorkingCopy.StaleNote("0.3.1+5c63fa3e", lonely), Is.Null);
    }

    [Test]
    public void An_unreadable_or_unstamped_build_is_quiet_rather_than_loud()
    {
        var dist = Repo("ref: refs/heads/main\n", "aabbccddeeff00112233445566778899aabbccdd");

        // No commit in the stamp — a source drop with no .git built it.
        Assert.That(WorkingCopy.StaleNote("0.3.1", dist), Is.Null);
        Assert.That(WorkingCopy.StaleNote("unknown", dist), Is.Null);

        // A .git that exists but says nothing readable.
        var broken = Repo("ref: refs/heads/nowhere\n");
        Assert.That(WorkingCopy.StaleNote("0.3.1+5c63fa3e", broken), Is.Null,
            "a courtesy that throws is worse than one that stays quiet");
    }
}
