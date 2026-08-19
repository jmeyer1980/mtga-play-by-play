using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// One prompt the game put to the player, said in words.
/// </summary>
/// <param name="Headline">What was asked — "pay costs", or a request type's bare name.</param>
/// <param name="Detail">
/// The terms of it, indented relative to the headline. Empty for a request nothing here
/// knows how to read, which still names itself.
/// </param>
public readonly record struct Negotiation(string Headline, IReadOnlyList<string> Detail);

/// <summary>
/// What the game asked the player, read out of the request messages <c>why</c> used to
/// walk straight past.
/// </summary>
/// <remarks>
/// A refused action leaves no annotations, because nothing happened — so the whole of
/// <c>why</c> was blind to the one question a confused player most wants answered, which
/// is not "what happened" but "why couldn't I do that". The answer is in the requests:
/// the client asks the player to pay, lists what they may pay with, and the list can be
/// empty. Issue #20 exists because a player conceded a game they were ahead in after
/// three rounds of that, and nothing in the tool could show it to them.
/// <para>
/// Pure on purpose. It takes one message and a way to name an instance id, and returns
/// words; it does no walking and holds no state, so it can be tested against the exact
/// bytes the archive carries without replaying a match to reach them.
/// </para>
/// <para>
/// The vocabulary here was taken from the archive rather than from the one match that
/// prompted the issue: 529 matches, 233 <c>payCostsReq</c> in 84 of them, and every cost
/// shape, action type and mana spec type below was counted before it was worded.
/// </para>
/// </remarks>
public static class Negotiations
{
    /// <summary>
    /// Every request one GRE message carries, in the order it carries them.
    /// </summary>
    /// <param name="message">One entry of <c>greToClientMessages</c>.</param>
    /// <param name="cards">Used only to look ability ids up as rules text.</param>
    /// <param name="name">
    /// How to say an instance id — the same resolver the annotation dump uses, so an id
    /// reads identically in both halves of a <c>why</c> and the reader can match them up.
    /// </param>
    /// <remarks>
    /// Selected by the key rather than by the message's <c>type</c> field, because the
    /// key is the payload and a message with no payload has nothing to say. A request
    /// this does not know is named and left at that: fifteen request types appear in the
    /// archive and only two are read here, so the next confusing refusal is far more
    /// likely to arrive in one of the other thirteen than in these. A reader who sees
    /// <c>castingTimeOptionsReq</c> sitting where their problem was at least knows where
    /// to look next; a reader who sees nothing concludes the log is empty.
    /// </remarks>
    public static IEnumerable<Negotiation> Describe(
        JsonElement message, ICardDb cards, Func<int, string> name)
    {
        if (message.ValueKind != JsonValueKind.Object) yield break;

        foreach (var field in message.EnumerateObject())
        {
            if (!field.Name.EndsWith("Req", StringComparison.Ordinal)) continue;

            // A payload that is not an object still names itself rather than vanishing,
            // which is the same promise made to a request type nothing here reads.
            yield return field.Value.ValueKind is not JsonValueKind.Object
                ? new Negotiation(field.Name, [])
                : field.Name switch
                {
                    "declareAttackersReq" => Attack(field.Value, cards, name),
                    "payCostsReq" => Pay(field.Value, cards, name),
                    _ => new Negotiation(field.Name, [])
                };
        }
    }

    /// <summary>
    /// Being asked to declare attackers, and told what it would cost.
    /// </summary>
    /// <remarks>
    /// <c>qualifiedAttackers</c> is the list that answers "why couldn't I attack with
    /// that one": across 3578 of these it either equals <c>attackers</c> (3571) or is a
    /// strict superset of it (7), never a subset, so it is the permission and
    /// <c>attackers</c> is the selection.
    /// <para>
    /// The cost is the rare half and the reason this is read at all — 6 of those 3578
    /// carry one, all six in the match that prompted the issue. An uncosted request
    /// prints the permission and nothing else, which is what keeps an ordinary combat
    /// turn quiet.
    /// </para>
    /// </remarks>
    private static Negotiation Attack(JsonElement req, ICardDb cards, Func<int, string> name)
    {
        var detail = new List<string>();

        var qualified = Ids(req, "qualifiedAttackers", "attackerInstanceId");
        if (qualified.Count == 0) qualified = Ids(req, "attackers", "attackerInstanceId");
        detail.Add(qualified.Count == 0
            ? "nothing you controlled was allowed to attack"
            : $"allowed to attack: {string.Join(", ", qualified.Select(name))}");

        // Which ones the player had actually picked. Worth saying because picking is
        // what brings the tax down: the same turn sends this request both with an
        // empty selection and no cost, and with a selection and a cost, and seeing the
        // pair next to each other is seeing the trap close.
        var picked = Json.Array(req, "attackers")
            .Where(a => Json.Obj(a, "selectedDamageRecipient") is not null)
            .Select(a => Json.Int(a, "attackerInstanceId"))
            .Where(id => id is not null)
            .Select(id => name(id!.Value))
            .ToList();
        if (picked.Count > 0) detail.Add($"you had picked: {string.Join(", ", picked)}");

        detail.AddRange(Costs(req, cards, name, "attacking costs "));

        // Protobuf JSON leaves a false out rather than writing it, so an absent flag is
        // a refusal. True 3570 times across the archive and absent 8 — rare, and exactly
        // the kind of rare that a reader is looking at this page to find.
        if (!Json.Bool(req, "canSubmitAttackers"))
            detail.Add("the client would not accept the attack as declared");

        return new Negotiation("declare attackers", detail);
    }

    /// <summary>
    /// Being asked to pay, and told what may be paid with — including nothing.
    /// </summary>
    /// <remarks>
    /// An empty <c>paymentActions</c> is only a dead end when the cost was mana. 74 of
    /// the archive's 233 payment prompts carry an empty one, but 64 of those are asking
    /// the player to choose something rather than to spend something, and the choice is
    /// the way to pay — announcing "no way to pay existed" over a list of seven
    /// creatures the player could pick would be flatly untrue about a prompt they went
    /// on to answer. That leaves 10 real dead ends: 9 that demanded mana and offered
    /// none, and one that named no cost at all. The match behind issue #20 is one of
    /// the 9.
    /// </remarks>
    private static Negotiation Pay(JsonElement req, ICardDb cards, Func<int, string> name)
    {
        var detail = Costs(req, cards, name, "to pay: ").ToList();
        var choices = NonMana(req, name).ToList();
        detail.AddRange(choices);
        if (detail.Count == 0) detail.Add("to pay: a cost the log did not spell out");

        var actions = Json.Obj(req, "paymentActions") is { } offered
            ? Json.Array(offered, "actions").ToList()
            : [];

        if (actions.Count == 0)
        {
            if (choices.Count == 0)
                detail.Add("no way to pay existed — the client offered nothing");
            return new Negotiation("pay costs", detail);
        }

        // Said once above the list rather than on every line of it. Every mana the
        // archive has ever offered is predicted — 1191 of 1191, across 159 prompts,
        // never a mix — so per line it distinguishes nothing, while above the list it
        // is the sentence the player needed. The per-line marking below still happens
        // if a prompt ever arrives where only some of it is a forecast.
        var mana = actions.SelectMany(Offered).ToList();
        var allPredicted = mana.Count > 0 && mana.TrueForAll(Predicted);

        var ways = actions.Count == 1 ? "one way to pay" : $"{actions.Count} ways to pay";
        detail.Add(allPredicted
            ? $"{ways} — what these would add is predicted, and the client shows it in " +
              "your mana pool as though you had it already:"
            : $"{ways}:");

        foreach (var action in actions)
            detail.AddRange(Payment(action, name, sayPredicted: !allPredicted));
        return new Negotiation("pay costs", detail);
    }

    /// <summary>
    /// A cost that is not mana — sacrifice one of these, tap one of these.
    /// </summary>
    /// <remarks>
    /// 65 of the archive's 233 payment prompts carry no mana cost at all, and 64 of
    /// those carry this instead. Reading only the mana said "a cost the log did not
    /// spell out" about a quarter of every payment prompt ever recorded, which is the
    /// same failure this whole issue is about, one field along.
    /// <para>
    /// All 64 are <c>EffectCostType_Select</c> over instance ids, so the ids are named.
    /// Any other shape says what kind it was rather than guessing at its contents.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> NonMana(JsonElement req, Func<int, string> name)
    {
        if (Json.Obj(req, "effectCostReq") is not { } cost) yield break;

        var kind = Tail(Json.Str(cost, "effectCostType"), "EffectCostType_");
        var lead = kind is "Select" or "" ? "to pay: " : $"to pay: {kind}, ";

        if (Json.Obj(cost, "costSelection") is not { } selection)
        {
            yield return $"to pay: {(kind.Length == 0 ? "a cost with no terms given" : kind)}";
            yield break;
        }

        var least = Json.Int(selection, "minSel") ?? 0;
        var most = Json.Int(selection, "maxSel") ?? least;
        var how = least == most ? $"{least}" : $"{least} to {most}";

        var ids = Json.Array(selection, "ids").Select(e => Json.Int(e))
            .Where(id => id is not null).Select(id => id!.Value).ToList();

        yield return Json.Str(selection, "idType") == "IdType_InstanceId" && ids.Count > 0
            ? $"{lead}choose {how} of {string.Join(", ", ids.Select(name))}"
            : $"{lead}choose {how} of {ids.Count} the log named only by id";
    }

    private static IEnumerable<JsonElement> Offered(JsonElement action) =>
        Json.Array(action, "manaPaymentOptions").SelectMany(o => Json.Array(o, "mana"));

    private static bool Predicted(JsonElement mana) =>
        Json.Array(mana, "specs").Any(s => Json.Str(s, "type") == "ManaSpecType_Predictive");

    /// <summary>
    /// One offered way to pay: what it is, what it costs, and what it would add.
    /// </summary>
    /// <remarks>
    /// The line that matters most is the second one. Only 22 of the archive's 695
    /// payment actions carry a <c>manaCost</c> of their own, and an offer that itself
    /// costs mana is precisely how a player ends up staring at a payment prompt they
    /// cannot satisfy while the client insists there is a way.
    /// </remarks>
    private static IEnumerable<string> Payment(
        JsonElement action, Func<int, string> name, bool sayPredicted)
    {
        var verb = Tail(Json.Str(action, "actionType"), "ActionType_") switch
        {
            "Activate_Mana" => "activate",
            "Make_Payment" => "pay with",
            var other => other.Length == 0 ? "use" : other
        };

        var source = Json.Int(action, "instanceId") is { } id
            ? name(id)
            : "something the log did not name";

        var own = string.Concat(Json.Array(action, "manaCost").Select(Symbols));
        yield return own.Length == 0
            ? $"  {verb} {source}"
            : $"  {verb} {source} — which itself costs {own}";

        // Options are alternatives, and each carries exactly one mana: 1191 of 1191
        // across the archive. So they fold into one "or" list rather than a line each,
        // which is the difference between one line and five for a filter land.
        var colors = new List<string>();
        var specs = new List<string>();
        foreach (var option in Json.Array(action, "manaPaymentOptions"))
            foreach (var mana in Json.Array(option, "mana"))
            {
                var count = Json.Int(mana, "count") ?? 1;
                var color = Tail(Json.Str(mana, "color"), "ManaColor_");
                var word = color.Length == 0 ? "mana" : color.ToLowerInvariant();
                if (count > 1) word = $"{count} {word}";
                if (!colors.Contains(word)) colors.Add(word);

                foreach (var spec in Json.Array(mana, "specs"))
                    if (Tail(Json.Str(spec, "type"), "ManaSpecType_") is { Length: > 0 } t &&
                        !specs.Contains(t))
                        specs.Add(t);
            }

        if (!sayPredicted) specs.Remove("Predictive");
        if (colors.Count == 0) yield break;
        yield return $"    adds {Either(colors)}{Caveats(specs)}";
    }

    /// <summary>
    /// What is worth saying about the mana an offer would produce.
    /// </summary>
    /// <remarks>
    /// Only <c>Predictive</c> is put into words, because only <c>Predictive</c> is known
    /// to have misled anybody: the client renders predicted mana in the same pool as
    /// real mana, which is how a player came to believe they had white available when
    /// what they had was a forecast of what an unpayable ability would have produced.
    /// The other seven spec types in the archive print their bare names — inventing an
    /// explanation for an enum nobody has been caught out by would be guessing, and a
    /// name at least tells a reader what to search the log for.
    /// <para>
    /// The caller removes <c>Predictive</c> before this when every offer in the prompt
    /// carries it, which so far is always, and says it once above the list instead. This
    /// keeps the wording in one place for the day a prompt arrives that mixes the two.
    /// </para>
    /// </remarks>
    private static string Caveats(List<string> specs)
    {
        var said = new List<string>();
        if (specs.Remove("Predictive"))
            said.Add("a prediction of what this would make, shown in the mana pool as " +
                     "though you already had it");
        said.AddRange(specs);
        return said.Count == 0 ? "" : $" — {string.Join("; ", said)}";
    }

    /// <summary>
    /// A mana cost, what it is attached to, and the rule it arises under.
    /// </summary>
    /// <remarks>
    /// The rules text is looked up rather than paraphrased, and it is the part that
    /// actually answers the question: "Archangel of Tithes" names the culprit, but
    /// "creatures can't attack you unless their controller pays {1}" is the reason.
    /// </remarks>
    private static IEnumerable<string> Costs(
        JsonElement req, ICardDb cards, Func<int, string> name, string lead)
    {
        foreach (var cost in Json.Array(req, "manaCost"))
        {
            // An uncosted prompt sends manaCost as a single empty object rather than as
            // an empty array, so a cost of nothing has to be recognised, not counted.
            if (Symbols(cost) is not { Length: > 0 } symbols) continue;

            var owed = Json.Int(cost, "objectId") is { } id ? $" — {name(id)}" : "";
            yield return $"{lead}{symbols}{owed}{Rule(Json.Int(cost, "abilityGrpId"), cards)}";
        }
    }

    /// <summary>
    /// The rule the cost arises under, which is not always the named object's own text.
    /// </summary>
    /// <remarks>
    /// "Under the rule" and not a bare quotation, because a bare quotation next to a
    /// name reads as that card's text and it often is not. Paying {1}{G} for Paradise
    /// Druid in one archived match carries ability 171842, "whenever this creature deals
    /// combat damage to a player, you may cast target nonland permanent card from that
    /// player's graveyard" — the Gix trigger that made casting it possible at all. The
    /// id is right and the text is right; only the reading of them as one thing is
    /// wrong. No card in the database claims 171842 in its own ability list, so there
    /// is nothing to name it by, and guessing at one would be worse than the quote.
    /// </remarks>
    private static string Rule(int? abilityGrpId, ICardDb cards) =>
        abilityGrpId is { } id && cards.AbilityText(id) is { } raw &&
        AbilityText.Plain(raw) is { Length: > 0 } text
            ? $", under the rule “{text}”"
            : "";

    /// <summary>
    /// A cost in the notation it is printed in on the card.
    /// </summary>
    /// <remarks>
    /// Every cost entry in the archive names exactly one colour, generic included, and
    /// counts from one to ten. Generic collapses to its number — <c>{2}</c>, not
    /// <c>{Generic}{Generic}</c> — and a coloured cost repeats its symbol, which is how
    /// a player reads it everywhere else. More than one colour on an entry has never
    /// been seen; it renders as a hybrid rather than silently dropping all but the first.
    /// </remarks>
    private static string Symbols(JsonElement cost)
    {
        if (cost.ValueKind != JsonValueKind.Object) return "";
        if (Json.Int(cost, "count") is not { } count || count <= 0) return "";

        var colors = Json.Array(cost, "color")
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => Letter(c.GetString()!))
            .ToList();
        if (colors.Count == 0) return "";

        var joined = string.Join("/", colors);
        return joined == "Generic"
            ? $"{{{count}}}"
            : string.Concat(Enumerable.Repeat($"{{{joined}}}", count));
    }

    private static string Letter(string color) => Tail(color, "ManaColor_") switch
    {
        "White" => "W",
        "Blue" => "U",
        "Black" => "B",
        "Red" => "R",
        "Green" => "G",
        "Colorless" => "C",
        var other => other
    };

    private static List<int> Ids(JsonElement req, string array, string property) =>
        Json.Array(req, array)
            .Select(e => Json.Int(e, property))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

    /// <summary>"a, b or c" — alternatives, because a payment option is a choice.</summary>
    private static string Either(IReadOnlyList<string> words) => words.Count switch
    {
        0 => "",
        1 => words[0],
        _ => $"{string.Join(", ", words.Take(words.Count - 1))} or {words[^1]}"
    };

    /// <summary>An Arena enum with its type prefix taken off, or "" when there was none.</summary>
    private static string Tail(string? value, string prefix) =>
        value is null ? "" : value.Replace(prefix, "", StringComparison.Ordinal);
}
