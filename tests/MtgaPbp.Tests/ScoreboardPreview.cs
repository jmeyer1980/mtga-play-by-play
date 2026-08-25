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
            0, "2026-08-24 13:38", Games: 26, Won: 10, Lost: 16, Drawn: 0,
            Decks:
            [
                new SessionDeck("The Unbeatable Squirrel Girl", 6, 5, Streak: 3),
                new SessionDeck("The Notary Hobbits", 3, 5, Streak: 1),
                new SessionDeck("Kitsa, Otterball Elite", 1, 6, Streak: 4)
            ],
            MatchIds: ["m1"]);

        var beats = new List<Beat>
        {
            new("20:57", "The Notary Hobbits", "Lost 0-1"),
            new("20:42", "The Notary Hobbits", "Won 1-0"),
            new("20:37", "The Notary Hobbits", "Lost 0-1")
        };

        foreach (var l in Scoreboard.Lines(session, beats, "The Notary Hobbits",
                     "The Unbeatable Squirrel Girl",
                     "http://127.0.0.1:8787/", new DateTime(2026, 8, 24, 21, 8, 46), 78, 30))
            TestContext.Out.WriteLine(l);
    }
}
