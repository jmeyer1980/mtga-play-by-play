using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

[Explicit("Prints the scoreboard for eyeballing; not an assertion.")]
public class ScoreboardPreview
{
    [Test]
    public void Show()
    {
        var session = new SessionRow(
            0, "2026-08-22 10:36", Games: 22, Won: 9, Lost: 13, Drawn: 0,
            Decks:
            [
                new SessionDeck("Elspeth, Storm Slayer", 8, 6),
                new SessionDeck("Hulk, Gamma Goliath", 1, 4),
                new SessionDeck("Lathliss, Dragon Queen", 0, 3)
            ],
            MatchIds: ["m1"]);

        var beats = new List<Beat>
        {
            new("16:52", "Elspeth, Storm Slayer", "Won 1-0"),
            new("16:50", "Elspeth, Storm Slayer", "Lost 0-1"),
            new("16:42", "Elspeth, Storm Slayer", "Won 1-0")
        };

        foreach (var l in Scoreboard.Lines(session, beats, "Elspeth, Storm Slayer",
                     "http://127.0.0.1:8787/", new DateTime(2026, 8, 22, 16, 52, 14), 78, 30))
            TestContext.Out.WriteLine(l);
    }
}
