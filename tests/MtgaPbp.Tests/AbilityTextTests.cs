using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The markup shapes here are the measured inventory of the live card database's
/// 21,119 ability rows — symbol runs, hybrids, snow, reminder text — not invented
/// cases. Each was seen in the wild before it was pinned here.
/// </summary>
public class AbilityTextTests
{
    [TestCase("First strike", "first strike")]
    [TestCase("Ward {o1}", "ward {1}")]
    [TestCase("Decayed", "decayed")]
    public void A_keyword_is_lowercased_to_sit_mid_sentence(string raw, string expected)
    {
        Assert.That(AbilityText.Clause(raw, out var keyword), Is.EqualTo(expected));
        Assert.That(keyword, Is.True);
    }

    [Test]
    public void A_whole_rule_keeps_its_capitals_and_gains_quotes()
    {
        var clause = AbilityText.Clause(
            "When this Class becomes level 2, create a token.", out var keyword);
        Assert.That(clause, Is.EqualTo("“When this Class becomes level 2, create a token.”"));
        Assert.That(keyword, Is.False);
    }

    /// <summary>
    /// A Class card's level abilities come wrapped for Arena's own renderer —
    /// <c>CLASSLEVEL [2+] [] [rule]</c> marks the level a rule works from — and the
    /// wrapper is not words (#230). Unwrapped, the rule reads as printed; the level is
    /// already stated by the "{W}: Level 2" line above it on the card.
    /// </summary>
    [TestCase("CLASSLEVEL [2+] [] [When this Class becomes level 2, create a token that's a copy of target token you control.]",
              "When this Class becomes level 2, create a token that's a copy of target token you control.")]
    [TestCase("CLASSLEVEL [3+] [] [Creature tokens you control get <nobr>+2/+2</nobr>.]",
              "Creature tokens you control get +2/+2.")]
    public void A_class_level_wrapper_is_unwrapped_to_its_rule(string raw, string expected)
    {
        Assert.That(AbilityText.Plain(raw), Is.EqualTo(expected));
        Assert.That(AbilityText.IsClassLevel(raw), Is.True);
        Assert.That(AbilityText.IsClassLevel(expected), Is.False);
    }

    /// <summary>
    /// One wrapper can hold several rules, a bracket each — Ranger Class and Ninja Teen
    /// do. Unwrapped they are separate rules; as one clause they are one sentence after
    /// another.
    /// </summary>
    [Test]
    public void A_wrapper_with_several_rules_yields_each_of_them()
    {
        const string raw = "CLASSLEVEL [3+] [] [You may look at the top card of your library any time.] [You may cast creature spells from the top of your library.]";

        Assert.That(AbilityText.Unwrap(raw), Is.EqualTo(new[]
        {
            "You may look at the top card of your library any time.",
            "You may cast creature spells from the top of your library.",
        }));
        Assert.That(AbilityText.Plain(raw), Is.EqualTo(
            "You may look at the top card of your library any time. You may cast creature spells from the top of your library."));
        Assert.That(AbilityText.Unwrap("Flying"), Is.EqualTo(new[] { "Flying" }), "a rule is the one rule it is");
    }

    [TestCase("{oT}: Add {oG}.", "{T}: Add {G}.")]
    [TestCase("{o3oW}, {oT}: Do a thing.", "{3}{W}, {T}: Do a thing.")]
    [TestCase("Pay {oXoR}.", "Pay {X}{R}.")]
    public void A_symbol_run_unpacks_one_brace_per_symbol(string raw, string expected)
    {
        Assert.That(AbilityText.Plain(raw), Is.EqualTo(expected));
    }

    [TestCase("{o(W/U)}", "{W/U}")]
    [TestCase("{o1o(B/G)}", "{1}{B/G}")]
    [TestCase("{oSioSi}", "{Si}{Si}")]
    public void Hybrid_and_snow_symbols_survive_the_unpacking(string raw, string expected)
    {
        Assert.That(AbilityText.Plain(raw), Is.EqualTo(expected));
    }

    [Test]
    public void The_cost_macro_is_not_a_symbol_run_and_passes_through()
    {
        Assert.That(AbilityText.Plain("Pay {Cost}."), Is.EqualTo("Pay {Cost}."));
    }

    [Test]
    public void Cardname_becomes_this_creature_wherever_it_stands()
    {
        Assert.That(
            AbilityText.Plain("Whenever CARDNAME attacks, CARDNAME's power doubles."),
            Is.EqualTo("Whenever this creature attacks, this creature's power doubles."));
    }

    [Test]
    public void Rich_text_tags_are_stripped()
    {
        Assert.That(
            AbilityText.Plain("Put a <nobr>+1/+1</nobr> counter on <i>each</i> creature."),
            Is.EqualTo("Put a +1/+1 counter on each creature."));
    }

    /// <summary>
    /// Reminder text goes whole, words and all. Removing only the &lt;i&gt; markers
    /// would leave the parenthetical in the clause as if it were rules text — and its
    /// full stop would turn a bare keyword into a quoted sentence.
    /// </summary>
    [Test]
    public void Reminder_text_is_dropped_not_unwrapped()
    {
        var clause = AbilityText.Clause(
            "First strike <i>(This creature deals combat damage before creatures " +
            "without first strike.)</i>", out var keyword);
        Assert.That(clause, Is.EqualTo("first strike"));
        Assert.That(keyword, Is.True);
    }

    [Test]
    public void Newlines_collapse_to_spaces()
    {
        Assert.That(AbilityText.Plain("Flying\nVigilance"), Is.EqualTo("Flying Vigilance"));
    }

    [TestCase(new[] { "flying" }, "flying")]
    [TestCase(new[] { "flying", "lifelink" }, "flying and lifelink")]
    [TestCase(new[] { "flying", "first strike", "lifelink" },
              "flying, first strike and lifelink")]
    public void Joined_clauses_read_as_a_list(string[] clauses, string expected)
    {
        Assert.That(AbilityText.Join(clauses), Is.EqualTo(expected));
    }
}
