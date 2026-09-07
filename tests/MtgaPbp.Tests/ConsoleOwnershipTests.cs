using MtgaPbp.Cli.Tray;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// When <c>watch --tray</c> may let go of its console: only when nothing else is
/// attached to it, so that a shell it was typed into keeps its prompt and its Ctrl+C.
/// </summary>
public class ConsoleOwnershipTests
{
    [Test]
    public void Alone_on_the_console_means_a_shortcut_started_it() =>
        Assert.That(ConsoleOwnership.ShouldDetach(1), Is.True);

    [Test]
    public void A_shell_on_the_console_keeps_it() =>
        Assert.That(ConsoleOwnership.ShouldDetach(3), Is.False);

    [Test]
    public void No_console_at_all_has_nothing_to_let_go_of() =>
        Assert.That(ConsoleOwnership.ShouldDetach(0), Is.False);
}
