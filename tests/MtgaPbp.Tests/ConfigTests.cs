using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class ConfigTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() =>
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [Test]
    public void Load_returns_defaults_when_no_config_file_exists()
    {
        var c = Config.Load(_dir);
        Assert.That(c.LogPaths, Is.Not.Empty);
        Assert.That(c.LogPaths[0], Does.Contain("Player.log"));
        Assert.That(c.ArchiveDir, Is.Not.Empty);
    }

    [Test]
    public void Load_reads_overrides_from_mtga_pbp_json()
    {
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), """
        { "LogPaths": [ "C:\\custom\\Player.log" ],
          "OutputDir": "C:\\custom\\out",
          "LocalPlayerUserId": "ABC123" }
        """);

        var c = Config.Load(_dir);
        Assert.That(c.LogPaths, Is.EqualTo(new[] { @"C:\custom\Player.log" }));
        Assert.That(c.OutputDir, Is.EqualTo(@"C:\custom\out"));
        Assert.That(c.LocalPlayerUserId, Is.EqualTo("ABC123"));
    }

    [Test]
    public void Load_survives_a_corrupt_config_file_by_falling_back_to_defaults()
    {
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), "{ not json");
        Assert.That(Config.Load(_dir).LogPaths, Is.Not.Empty);
    }

    [Test]
    public void OpenAfterBuild_defaults_to_false()
    {
        Assert.That(Config.Default().OpenAfterBuild, Is.False);
    }

    [Test]
    public void OpenAfterBuild_can_be_turned_on_in_config()
    {
        // The setting exists for double-clicking the exe, where the console window
        // closes before the output path can be read.
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), """
        { "OpenAfterBuild": true }
        """);
        Assert.That(Config.Load(_dir).OpenAfterBuild, Is.True);
    }

    [Test]
    public void Load_keeps_defaults_for_fields_the_config_omits()
    {
        File.WriteAllText(Path.Combine(_dir, "mtga-pbp.json"), """
        { "OpenAfterBuild": true }
        """);
        var c = Config.Load(_dir);
        Assert.That(c.LogPaths, Is.Not.Empty);
        Assert.That(c.OutputDir, Is.Not.Empty);
    }

    [Test]
    public void Default_log_paths_include_both_current_and_previous_logs()
    {
        var c = Config.Default();
        Assert.That(c.LogPaths.Any(p => p.EndsWith("Player.log")), Is.True);
        Assert.That(c.LogPaths.Any(p => p.EndsWith("Player-prev.log")), Is.True);
    }
}
