using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A flag the exe does not know is said out loud on every command line, not only on
/// the one with no command on it.
/// </summary>
/// <remarks>
/// The warning used to be <c>Parse</c>'s last act, after every early return, so a verb
/// on the command line skipped it. That is how a Desktop shortcut whose target read
/// <c>watch ---tray ---open</c> — three dashes — ran a plain windowed watch on the
/// default port without a word, and looked like tray mode not working.
/// </remarks>
[Platform("Win")]
public class UnknownOptionTests
{
    private static int FreshPort() => Random.Shared.Next(40000, 60000);

    [Test]
    public void A_mistyped_flag_beside_a_verb_is_reported()
    {
        // `stop` on a port nothing listens on is the cheapest command that runs to the
        // end: no banner, no archive, no log, and it answers at once.
        var stderr = new StringWriter();
        var kept = Console.Error;
        Console.SetError(stderr);
        try { Program.Main(["stop", FreshPort().ToString(), "---tray"]); }
        finally { Console.SetError(kept); }

        Assert.That(stderr.ToString(), Does.Contain("warning: ignoring unknown option ---tray"));
    }

    [Test]
    public void Each_mistyped_flag_is_named_in_order() =>
        Assert.That(Program.UnknownOptions(["watch", "---tray", "---open"]),
                    Is.EqualTo(new[] { "---tray", "---open" }));

    [Test]
    public void A_dashed_verb_is_not_an_unknown_option() =>
        Assert.That(Program.UnknownOptions(["--watch", "8793", "--open"]), Is.Empty);

    [Test]
    public void Every_recognised_option_passes() =>
        Assert.That(Program.UnknownOptions(["build", "--open", "--rebuild", "--prune", "--tray"]), Is.Empty);

    [Test]
    public void Three_dashes_before_a_command_are_a_typo_not_a_command() =>
        Assert.That(Program.UnknownOptions(["---watch"]), Is.EqualTo(new[] { "---watch" }));

    [Test]
    public void A_three_dash_command_is_reported_rather_than_run()
    {
        var port = FreshPort().ToString();
        var stderr = Run(["---stop", port]);
        Assert.That(stderr, Does.Contain("warning: ignoring unknown option ---stop"));
        Assert.That(stderr, Does.Not.Contain("no watch is running"), "stop must not have run");
    }

    [Test]
    public void The_two_dash_spelling_of_a_command_still_runs_it()
    {
        var port = FreshPort().ToString();
        var stderr = Run(["--stop", port]);
        Assert.That(stderr, Does.Contain($"no watch is running on port {port}"));
        Assert.That(stderr, Does.Not.Contain("warning:"));
    }

    /// <summary>Main's stderr for one command line.</summary>
    private static string Run(string[] args)
    {
        var stderr = new StringWriter();
        var kept = Console.Error;
        Console.SetError(stderr);
        try { Program.Main(args); }
        finally { Console.SetError(kept); }
        return stderr.ToString();
    }
}
