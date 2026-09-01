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

    /// <summary>
    /// The bug behind #134, as the upgrade that caused it: the user sets an ArchiveDir
    /// in their own file, a new release drops its shipped config into the same folder,
    /// and the setting has to still be there afterwards. Up to 0.6.0 both were named
    /// mtga-pbp.json, so unzipping replaced the user's file and the next run built a
    /// fresh archive at the default location.
    /// </summary>
    [Test]
    public void An_upgrade_overwriting_the_shipped_config_leaves_the_users_settings_alone()
    {
        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "ArchiveDir": "C:\\mine\\archive" }""");
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");

        var c = Config.Load(_dir);
        Assert.That(c.ArchiveDir, Is.EqualTo(@"C:\mine\archive"), "the user's setting survives");
        Assert.That(c.OpenAfterBuild, Is.True, "and the shipped one still applies");

        // The upgrade again, which rewrites only the file it ships.
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");
        Assert.That(Config.Load(_dir).ArchiveDir, Is.EqualTo(@"C:\mine\archive"));
    }

    /// <summary>
    /// The layering hazard that made a nullable DTO necessary. A bool has no unset
    /// state, so a user file that never mentions OpenAfterBuild deserializes to false
    /// — and applying that would switch off what the shipped layer turned on.
    /// </summary>
    [Test]
    public void A_key_the_user_file_omits_keeps_what_the_shipped_file_said()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");
        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "OutputDir": "C:\\mine\\out" }""");

        var c = Config.Load(_dir);
        Assert.That(c.OpenAfterBuild, Is.True);
        Assert.That(c.OutputDir, Is.EqualTo(@"C:\mine\out"));
    }

    /// <summary>And the user can still say no to something the shipped file said yes to.</summary>
    [Test]
    public void The_user_file_can_turn_off_what_the_shipped_file_turned_on()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");
        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "OpenAfterBuild": false }""");

        Assert.That(Config.Load(_dir).OpenAfterBuild, Is.False);
    }

    /// <summary>
    /// The shipped file alone is enough for a fresh install, which is the whole reason
    /// it is still shipped: without it a double-clicked exe builds and the console
    /// closes before the path can be read.
    /// </summary>
    [Test]
    public void The_shipped_file_applies_when_the_user_has_written_none()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");

        Assert.That(Config.Load(_dir).OpenAfterBuild, Is.True);
    }

    /// <summary>
    /// Zero is this setting's way of saying "no limit", so a later layer has to be
    /// able to state it — otherwise a cap set by an earlier layer could never be
    /// lifted, and the documented contract would be unenforceable.
    /// </summary>
    [Test]
    public void The_user_file_can_lift_a_cap_the_shipped_file_set()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "MaxArchivedMatches": 60 }""");
        Assert.That(Config.Load(_dir).MaxArchivedMatches, Is.EqualTo(60));

        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "MaxArchivedMatches": 0 }""");
        Assert.That(Config.Load(_dir).MaxArchivedMatches, Is.Zero, "zero means no limit");
    }

    /// <summary>
    /// A negative is not a smaller number of matches to keep, it is not a number of
    /// matches at all — and MaxArchivedMatches drives an irreversible delete, so it is
    /// ignored rather than clamped to something nobody asked for.
    /// </summary>
    [Test]
    public void A_negative_cap_is_ignored()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "MaxArchivedMatches": 60 }""");
        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "MaxArchivedMatches": -1 }""");

        Assert.That(Config.Load(_dir).MaxArchivedMatches, Is.EqualTo(60));
    }

    /// <summary>
    /// A broken user file costs only what that file would have said. What the shipped
    /// layer set is still in effect, which is why the message no longer claims
    /// everything went back to defaults.
    /// </summary>
    [Test]
    public void A_malformed_user_file_leaves_the_shipped_layer_in_effect()
    {
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");
        File.WriteAllText(Path.Combine(_dir, Config.UserFile), "{ not json");

        var c = Config.Load(_dir);
        Assert.That(c.OpenAfterBuild, Is.True);
        Assert.That(c.LogPaths, Is.Not.Empty);
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
    public void Rotation_suggestions_are_on_until_the_user_says_otherwise()
    {
        Assert.That(Config.Default().SuggestDeckRotation, Is.True);

        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "SuggestDeckRotation": false }""");
        Assert.That(Config.Load(_dir).SuggestDeckRotation, Is.False);
    }

    /// <summary>
    /// The point of switching it off is not hearing it again, so an upgrade must not
    /// be able to undo that. The shipped layer is rewritten by every release and the
    /// user layer is not, which is what makes the silence stick.
    /// </summary>
    [Test]
    public void An_upgrade_cannot_switch_rotation_suggestions_back_on()
    {
        File.WriteAllText(Path.Combine(_dir, Config.UserFile),
            """{ "SuggestDeckRotation": false }""");
        File.WriteAllText(Path.Combine(_dir, Config.ShippedFile),
            """{ "OpenAfterBuild": true }""");

        Assert.That(Config.Load(_dir).SuggestDeckRotation, Is.False);
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
