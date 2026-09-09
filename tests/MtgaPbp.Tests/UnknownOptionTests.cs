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
}
