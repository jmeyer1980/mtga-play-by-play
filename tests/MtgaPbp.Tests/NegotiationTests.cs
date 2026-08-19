using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// What the game asked the player, read out of the request messages.
/// </summary>
/// <remarks>
/// The messages below are the archive's own bytes, copied out of match
/// <c>6481ef46</c> turn 10 — the turn behind issue #20, where a player conceded a game
/// they were ahead in because the client demanded a payment and then refused every way
/// of making it. They carry no screen name, user id or path; a request message is ids
/// and mana and nothing else, which is why they can be quoted here in full.
/// <para>
/// Hand-assembled rather than replayed out of a checked-in match, per CONTRIBUTING:
/// the thing under test is the wording, and <see cref="Negotiations"/> is pure so the
/// wording can be reached without a match around it. The walk that feeds it is covered
/// in <see cref="LogDumpTests"/> against real traffic.
/// </para>
/// </remarks>
public class NegotiationTests
{
    /// <summary>
    /// Ability texts as the real database returns them, markup and all, so the
    /// unpacking of <c>{o1}</c> and <c>CARDNAME</c> is exercised rather than assumed.
    /// </summary>
    private sealed class Cards : ICardDb
    {
        public string? NameForLocId(int locId) => null;
        public CardInfo? CardForGrpId(int grpId) => null;
        public string? EnumName(string type, int value) => null;

        public string? AbilityText(int abilityGrpId) => abilityGrpId switch
        {
            102181 => "As long as CARDNAME is untapped, creatures can't attack you or " +
                      "planeswalkers you control unless their controller pays {o1} for " +
                      "each of those creatures.",
            1962 => "{o1}, {oT}: Add one mana of any color.",
            _ => null
        };
    }

    /// <summary>
    /// The same shape the dump's own resolver produces, including its reading of a
    /// low id as a seat.
    /// </summary>
    private static string Name(int id) => id switch
    {
        442 => "Giant's Boulder #442",
        477 => "Construct #477",
        480 => "Archangel of Tithes #480",
        588 => "Giant's Boulder's ability #588",
        <= 2 and > 0 => $"seat{id}",
        _ => $"Unknown card #{id}"
    };

    private static List<Negotiation> Read(string message) =>
        Negotiations.Describe(
            JsonDocument.Parse(message).RootElement, new Cards(), Name).ToList();

    private static IReadOnlyList<string> Detail(string message) => Read(message).Single().Detail;

    // ---------- the archive's own bytes ----------

    /// <summary>Declaring attackers with an attacker picked, which brings the tax down.</summary>
    private const string TaxedAttack = """
        {
          "type": "GREMessageType_DeclareAttackersReq",
          "systemSeatIds": [ 2 ],
          "declareAttackersReq": {
            "attackers": [
              {
                "attackerInstanceId": 477,
                "legalDamageRecipients": [
                  { "type": "DamageRecType_Player", "playerSystemSeatId": 1 }
                ],
                "selectedDamageRecipient": {
                  "type": "DamageRecType_Player", "playerSystemSeatId": 1
                }
              }
            ],
            "manaCost": [
              {
                "color": [ "ManaColor_Generic" ],
                "count": 1,
                "objectId": 480,
                "abilityGrpId": 102181
              }
            ],
            "qualifiedAttackers": [
              {
                "attackerInstanceId": 477,
                "legalDamageRecipients": [
                  { "type": "DamageRecType_Player", "playerSystemSeatId": 1 }
                ]
              }
            ],
            "canSubmitAttackers": true
          }
        }
        """;

    /// <summary>The same turn before an attacker was picked: no selection, no cost.</summary>
    private const string PlainAttack = """
        {
          "type": "GREMessageType_DeclareAttackersReq",
          "declareAttackersReq": {
            "attackers": [ { "attackerInstanceId": 477 } ],
            "manaCost": [ {} ],
            "qualifiedAttackers": [ { "attackerInstanceId": 477 } ],
            "canSubmitAttackers": true
          }
        }
        """;

    /// <summary>
    /// The tax coming due, with exactly one way to pay it offered: a filter rock with
    /// no fuel, whose five colours are all forecasts.
    /// </summary>
    private const string PayTheTax = """
        {
          "type": "GREMessageType_PayCostsReq",
          "payCostsReq": {
            "manaCost": [
              {
                "color": [ "ManaColor_Generic" ],
                "count": 1,
                "objectId": 2,
                "abilityGrpId": 102181
              }
            ],
            "paymentActions": {
              "actions": [
                {
                  "actionType": "ActionType_Activate_Mana",
                  "grpId": 103555,
                  "instanceId": 442,
                  "facetId": 442,
                  "abilityGrpId": 1962,
                  "manaPaymentOptions": [
                    { "mana": [ { "manaId": 750, "color": "ManaColor_White",
                                  "srcInstanceId": 442,
                                  "specs": [ { "type": "ManaSpecType_Predictive" } ],
                                  "abilityGrpId": 1962, "count": 1 } ] },
                    { "mana": [ { "manaId": 751, "color": "ManaColor_Blue",
                                  "srcInstanceId": 442,
                                  "specs": [ { "type": "ManaSpecType_Predictive" } ],
                                  "abilityGrpId": 1962, "count": 1 } ] },
                    { "mana": [ { "manaId": 752, "color": "ManaColor_Black",
                                  "srcInstanceId": 442,
                                  "specs": [ { "type": "ManaSpecType_Predictive" } ],
                                  "abilityGrpId": 1962, "count": 1 } ] },
                    { "mana": [ { "manaId": 753, "color": "ManaColor_Red",
                                  "srcInstanceId": 442,
                                  "specs": [ { "type": "ManaSpecType_Predictive" } ],
                                  "abilityGrpId": 1962, "count": 1 } ] },
                    { "mana": [ { "manaId": 754, "color": "ManaColor_Green",
                                  "srcInstanceId": 442,
                                  "specs": [ { "type": "ManaSpecType_Predictive" } ],
                                  "abilityGrpId": 1962, "count": 1 } ] }
                  ],
                  "manaCost": [
                    { "color": [ "ManaColor_Generic" ], "count": 1, "abilityGrpId": 1962 }
                  ],
                  "uniqueAbilityId": 370
                }
              ]
            }
          }
        }
        """;

    /// <summary>The dead end: the rock's own {1} comes due and nothing can pay it.</summary>
    private const string DeadEnd = """
        {
          "type": "GREMessageType_PayCostsReq",
          "payCostsReq": {
            "manaCost": [
              {
                "color": [ "ManaColor_Generic" ],
                "count": 1,
                "objectId": 588,
                "abilityGrpId": 1962
              }
            ],
            "paymentActions": {}
          }
        }
        """;

    // ---------- what it says about them ----------

    /// <summary>
    /// The whole of issue #20's first half: a refusal that produced no annotation at
    /// all still says who taxed the attack and by what rule.
    /// </summary>
    [Test]
    public void An_attack_tax_names_its_source_and_quotes_the_rule_behind_it()
    {
        var prompt = Read(TaxedAttack).Single();

        Assert.That(prompt.Headline, Is.EqualTo("declare attackers"));
        Assert.That(prompt.Detail, Has.One.EqualTo("allowed to attack: Construct #477"));
        Assert.That(prompt.Detail, Has.One.EqualTo("you had picked: Construct #477"));

        var cost = prompt.Detail.Single(d => d.StartsWith("attacking costs", StringComparison.Ordinal));
        Assert.That(cost, Does.Contain("{1}"), "the cost in the notation it is printed in");
        Assert.That(cost, Does.Contain("Archangel of Tithes #480"), "who is charging it");
        Assert.That(cost, Does.Contain("creatures can't attack you"), "why they can");

        // The database's markup must not reach a reader: {o1} is a mana symbol and
        // CARDNAME is a placeholder, and both read as gibberish printed raw.
        Assert.That(cost, Does.Not.Contain("{o1}"));
        Assert.That(cost, Does.Not.Contain("CARDNAME"));
    }

    /// <summary>
    /// An ordinary combat turn stays quiet. The tax is 6 requests of 3578 archive-wide,
    /// so a line about cost on the other 3572 would be noise on every turn but one.
    /// </summary>
    [Test]
    public void An_uncosted_declaration_says_who_could_attack_and_nothing_about_paying()
    {
        var detail = Detail(PlainAttack);

        Assert.That(detail, Is.EqualTo(new[] { "allowed to attack: Construct #477" }));
    }

    /// <summary>
    /// The second half of #20: the client offered exactly one way to pay, that way cost
    /// mana of its own, and the mana it advertised was a forecast rather than a pool.
    /// </summary>
    [Test]
    public void The_only_way_to_pay_says_what_it_costs_and_that_its_mana_is_a_forecast()
    {
        var detail = Detail(PayTheTax);

        Assert.That(detail, Has.One.EqualTo("to pay: {1} — seat2, under the rule " +
            "“As long as this creature is untapped, creatures can't attack you or " +
            "planeswalkers you control unless their controller pays {1} for each of " +
            "those creatures.”"));
        // The line that turns "just use it!" into "it costs the thing I am short of".
        Assert.That(detail, Has.One.EqualTo(
            "  activate Giant's Boulder #442 — which itself costs {1}"));

        Assert.That(detail, Has.One.EqualTo("    adds white, blue, black, red or green"),
            "five alternatives fold into one line rather than five");

        // Said once, above the list. Every mana the archive has ever offered carries
        // the predictive flag — 1191 of 1191 — so a line each would distinguish
        // nothing while burying the sentence that matters under ten copies of itself.
        Assert.That(detail.Count(d => d.Contains("predicted", StringComparison.Ordinal)),
            Is.EqualTo(1));
        Assert.That(detail.Single(d => d.Contains("predicted", StringComparison.Ordinal)),
            Does.StartWith("one way to pay —").And.Contains("mana pool"));
        Assert.That(detail, Has.None.Contains("ManaSpecType_"), "in words, not in enums");
    }

    /// <summary>
    /// An empty payment list is the answer, not the absence of one, so it needs saying
    /// rather than implying.
    /// </summary>
    [Test]
    public void A_payment_prompt_with_no_way_to_pay_says_so_in_words()
    {
        var detail = Detail(DeadEnd);

        Assert.That(detail, Has.One.EqualTo("to pay: {1} — Giant's Boulder's ability #588, " +
            "under the rule “{1}, {T}: Add one mana of any color.”"));
        Assert.That(detail, Has.One.Contains("no way to pay existed"));
        Assert.That(detail, Has.None.Contains("way to pay:"), "there is no list to head");
    }

    /// <summary>
    /// A cost that is not mana. Reading only the mana field called a quarter of every
    /// payment prompt in the archive a cost nobody spelled out, and then called it a
    /// dead end on top — over a list of seven creatures the player could have picked.
    /// </summary>
    /// <remarks>
    /// 64 of the archive's 233 payment prompts are this shape, and every one of them
    /// carries an empty <c>paymentActions</c>, because what is being asked for is not
    /// mana. Only 10 prompts are real dead ends.
    /// </remarks>
    [Test]
    public void A_cost_that_is_not_mana_lists_what_could_be_chosen_and_is_no_dead_end()
    {
        var detail = Detail("""
            { "payCostsReq": {
                "effectCostReq": {
                  "effectCostType": "EffectCostType_Select",
                  "costSelection": {
                    "minSel": 1, "maxSel": 1,
                    "context": "SelectionContext_NonMana_Payment",
                    "ids": [ 477, 442 ],
                    "idType": "IdType_InstanceId" } },
                "paymentActions": {} } }
            """);

        Assert.That(detail, Has.One.EqualTo(
            "to pay: choose 1 of Construct #477, Giant's Boulder #442"));
        Assert.That(detail, Has.None.Contains("no way to pay existed"),
            "a choice of two is a way to pay, whatever the mana list says");
        Assert.That(detail, Has.None.Contains("did not spell out"),
            "and the log spelled it out perfectly well");
    }

    /// <summary>
    /// Fifteen request types appear in the archive and two are read here, so the next
    /// confusing refusal is far likelier to arrive in one of the other thirteen. A
    /// reader who sees its name knows where to look; one who sees nothing concludes
    /// the log is empty.
    /// </summary>
    [Test]
    public void A_request_nothing_here_reads_still_names_itself()
    {
        var prompt = Read("""
            { "type": "GREMessageType_CastingTimeOptionsReq",
              "castingTimeOptionsReq": { "cardTitleId": 1 } }
            """).Single();

        Assert.That(prompt.Headline, Is.EqualTo("castingTimeOptionsReq"));
        Assert.That(prompt.Detail, Is.Empty);
    }

    [Test]
    public void A_message_carrying_no_request_says_nothing()
    {
        Assert.That(Read("""{ "gameStateMessage": { "annotations": [] } }"""), Is.Empty);
        Assert.That(Read("[1, 2, 3]"), Is.Empty);
    }

    /// <summary>
    /// Costs read the way they are printed on a card. Every cost entry in the archive
    /// names exactly one colour and counts from one to ten.
    /// </summary>
    [TestCase("ManaColor_Generic", 2, "{2}")]
    [TestCase("ManaColor_Generic", 1, "{1}")]
    [TestCase("ManaColor_Blue", 1, "{U}")]
    [TestCase("ManaColor_Black", 3, "{B}{B}{B}")]
    [TestCase("ManaColor_Colorless", 1, "{C}")]
    public void A_generic_cost_reads_as_a_number_and_a_coloured_one_repeats_its_symbol(
        string color, int count, string expected)
    {
        var detail = Detail($$"""
            { "payCostsReq": {
                "manaCost": [ { "color": [ "{{color}}" ], "count": {{count}} } ],
                "paymentActions": {} } }
            """);

        Assert.That(detail[0], Is.EqualTo($"to pay: {expected}"));
    }

    /// <summary>
    /// Protobuf JSON omits a false rather than writing it, so an absent flag is a
    /// refusal — true 3570 times across the archive and absent 8, and those 8 are
    /// exactly what somebody would open this page to find.
    /// </summary>
    [Test]
    public void A_declaration_the_client_would_not_accept_says_so()
    {
        var refused = Detail("""
            { "declareAttackersReq": {
                "qualifiedAttackers": [ { "attackerInstanceId": 477 } ] } }
            """);

        Assert.That(refused, Has.One.EqualTo("the client would not accept the attack as declared"));
        Assert.That(Detail(PlainAttack), Has.None.Contains("would not accept"),
            "and an accepted one does not say it");
    }

    /// <summary>
    /// A creature missing from the permission list is the answer to "why couldn't I
    /// attack with that one", so the empty case has to be a sentence rather than a
    /// blank.
    /// </summary>
    [Test]
    public void A_declaration_that_allowed_nothing_says_that_rather_than_listing_nothing()
    {
        var detail = Detail("""
            { "declareAttackersReq": { "canSubmitAttackers": true } }
            """);

        Assert.That(detail, Is.EqualTo(new[] { "nothing you controlled was allowed to attack" }));
    }
}
