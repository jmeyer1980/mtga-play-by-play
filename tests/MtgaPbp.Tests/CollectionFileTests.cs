using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Reading a collection exported from somewhere else, in Arena's own decklist text.
/// </summary>
/// <remarks>
/// Every source writes a slightly different dialect of the same format — a tracker's
/// copy button gives bare "4 Hare Apparent", the memory-scanning exporter gives
/// "4x Hare Apparent (FDN) #123 [rare]" under a header block. Both have to read.
/// </remarks>
public class CollectionFileTests
{
    private static IReadOnlyList<OwnedCard> Parse(params string[] lines) =>
        CollectionFile.Parse(lines, out _);

    [Test]
    public void The_bare_form_a_copy_button_produces_reads()
    {
        var owned = Parse("1 Green Sun's Zenith", "4 Monastery Swiftspear", "2 Skulduggery");

        Assert.That(owned, Has.Count.EqualTo(3));
        Assert.That(owned[0], Is.EqualTo(new OwnedCard("Green Sun's Zenith", 1)));
        Assert.That(owned[1].Count, Is.EqualTo(4));
    }

    [Test]
    public void Printing_detail_is_accepted_and_discarded()
    {
        // "Do I own it, and how many" — a card owned in two printings is one card, and
        // the set code is the part that differs between exporters.
        var owned = Parse(
            "4x Hare Apparent (FDN) #123 [rare]",
            "2 Ethereal Armor (DSK)",
            "1 Split Up [uncommon]",
            "3 Caretaker's Talent #45");

        Assert.That(owned.Select(o => o.Name), Is.EqualTo(new[]
        {
            "Hare Apparent", "Ethereal Armor", "Split Up", "Caretaker's Talent"
        }));
    }

    [Test]
    public void The_same_card_in_two_printings_is_one_entry()
    {
        var owned = Parse("2 Llanowar Elves (M19)", "2 Llanowar Elves (DOM)");

        Assert.That(owned, Has.Count.EqualTo(1));
        Assert.That(owned[0].Count, Is.EqualTo(4), "copies add up across printings");
    }

    [Test]
    public void Headers_blank_lines_comments_and_rules_are_skipped()
    {
        var owned = CollectionFile.Parse(
        [
            "MTGA Collection Export",
            "Exported: 2026-08-13 06:00:00",
            "Unique cards: 2",
            "============================================================",
            "",
            "Deck",
            "// a comment",
            "# another",
            "4 Hare Apparent",
            "",
            "Sideboard",
            "1 Split Up"
        ], out var unreadable);

        Assert.That(owned.Select(o => o.Name), Is.EqualTo(new[] { "Hare Apparent", "Split Up" }));
        Assert.That(unreadable, Is.Empty, "the exporters' own preamble is not a complaint");
    }

    /// <summary>
    /// A line that looked like it meant something and could not be read is reported.
    /// </summary>
    /// <remarks>
    /// Silently dropping them would make the collection quietly answer "you do not own
    /// that" about cards that are in the file — the worst possible failure for a feature
    /// whose whole job is telling you what you have.
    /// </remarks>
    [Test]
    public void A_line_that_cannot_be_read_is_reported_rather_than_dropped()
    {
        var owned = CollectionFile.Parse(
            ["4 Hare Apparent", "this is not a card line", "0 Nothing", "2 Split Up",
             "12x", "2 Split Up"],
            out var unreadable);

        Assert.That(owned.Select(o => o.Name), Is.EqualTo(new[] { "Hare Apparent", "Split Up" }));
        Assert.That(owned[1].Count, Is.EqualTo(4), "the repeated entry merged");

        // Only a line beginning with a count could have been a card. Prose that was
        // never an entry is not a card anyone lost, so it is not reported as one.
        Assert.That(unreadable, Is.EqualTo(new[] { "0 Nothing", "12x" }));
    }

    [Test]
    public void Card_names_containing_digits_and_punctuation_survive()
    {
        var owned = Parse(
            "1 Borrowing 100,000 Arrows",
            "4 Ajani's Pridebearer",
            "2 Dollmaker's Shop // Porcelain Gallery",
            "1 Kongming, \"Sleeping Dragon\"");

        Assert.That(owned.Select(o => o.Name), Is.EqualTo(new[]
        {
            "Borrowing 100,000 Arrows",
            "Ajani's Pridebearer",
            "Dollmaker's Shop // Porcelain Gallery",
            "Kongming, \"Sleeping Dragon\""
        }));
    }

    [Test]
    public void A_card_named_like_a_heading_is_still_a_card()
    {
        // Headings only ever match a line with no leading count, so this is safe — but
        // it is the obvious way a heading filter goes wrong, so it is pinned.
        Assert.That(Parse("1 Deck of Cards")[0].Name, Is.EqualTo("Deck of Cards"));
    }
}
