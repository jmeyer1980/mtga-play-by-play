using MtgaPbp.Cli.Tray;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The icon's tooltip is the scoreboard's headline in one line, and it is what a screen
/// reader announces at the icon — so it names the program before the score.
/// </summary>
public class TrayTipTests
{
    private static readonly DateTime At = new(2026, 9, 6, 21, 14, 9);

    private static SessionRow Session(int won, int lost, int drawn = 0) =>
        new(0, "2026-09-06 19:30", won + lost + drawn, won, lost, drawn, [], ["m1"]);

    [Test]
    public void Before_the_first_match_it_says_so() =>
        Assert.That(TrayTip.Compose(null, At), Is.EqualTo("mtga-pbp — watching · no matches yet · updated 21:14"));

    [Test]
    public void The_record_is_the_headline() =>
        Assert.That(TrayTip.Compose(Session(9, 13), At), Is.EqualTo("mtga-pbp — watching · 9-13 tonight · updated 21:14"));

    [Test]
    public void A_draw_shows_in_the_record() =>
        Assert.That(TrayTip.Compose(Session(9, 13, drawn: 1), At), Does.Contain("9-13-1 tonight"));

    [Test]
    public void It_fits_the_shell_s_128_characters()
    {
        var clipped = TrayTip.Clip(new string('x', 300));
        Assert.That(clipped.Length, Is.EqualTo(TrayTip.MaxLength));
        Assert.That(clipped, Does.EndWith("…"));
    }

    [Test]
    public void Nothing_short_is_touched()
    {
        var exact = new string('x', TrayTip.MaxLength);
        Assert.That(TrayTip.Clip(exact), Is.EqualTo(exact));
    }
}
