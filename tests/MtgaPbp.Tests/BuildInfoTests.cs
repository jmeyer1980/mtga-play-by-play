using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Every rendered artefact says which build wrote it.
/// </summary>
/// <remarks>
/// This exists because of a morning spent proving a fixed bug was still broken. A
/// <c>watch</c> was running from a release built a day earlier and kept rewriting the
/// whole report with pre-fix code; the repository said the bug was gone, the report on
/// disk said it was not, and both were right. Nothing on the page or in the exe named a
/// version, so there was no way to see that they were different programs.
/// </remarks>
public class BuildInfoTests
{
    [Test]
    public void The_running_build_reports_a_version_rather_than_a_placeholder()
    {
        // Assert against the real attribute, not the settable property the golden-file
        // test pins — the point is that the build actually stamped something.
        var real = typeof(BuildInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion;

        Assert.That(real, Is.Not.Empty);
        Assert.That(real, Does.Not.Contain("unknown"));
        Assert.That(real, Does.Match(@"^\d+\.\d+\.\d+"), "a semver the release workflow can set");
    }

    /// <summary>
    /// A version alone would not have settled that morning: every build from a working
    /// copy carries the same one, so two exes a day apart both said 0.2.0. The commit is
    /// the half that tells them apart.
    /// </summary>
    [Test]
    public void A_build_from_a_working_copy_names_the_commit_it_came_from()
    {
        var real = typeof(BuildInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion;

        // Skipped rather than failed off a repository, because a source drop with no .git
        // directory still has to build and its version is still true, just less specific.
        if (!Directory.Exists(Path.Combine(RepoRoot(), ".git")))
            Assert.Ignore("not a git working copy");

        Assert.That(real, Does.Match(@"^\d+\.\d+\.\d+\+[0-9a-f]{8}$"));
    }

    [Test]
    public void Every_rendered_artefact_carries_the_stamp()
    {
        var original = BuildInfo.Version;
        try
        {
            BuildInfo.Version = "9.9.9+deadbeef";
            var t = RendererTests.Sample();

            Assert.Multiple(() =>
            {
                Assert.That(MarkdownRenderer.Render(t), Does.Contain(BuildInfo.Line),
                    "a transcript pasted into a chat should say what wrote it");
                Assert.That(GamePageRenderer.Render(t), Does.Contain(BuildInfo.Line));
                Assert.That(IndexRenderer.Render([IndexRenderer.Summarize(t)]),
                    Does.Contain(BuildInfo.Line),
                    "the index is the page left open while watch rewrites underneath it");
            });
        }
        finally { BuildInfo.Version = original; }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MtgaPbp.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }
}
