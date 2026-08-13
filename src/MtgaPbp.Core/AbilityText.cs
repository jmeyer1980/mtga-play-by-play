using System.Text.RegularExpressions;

namespace MtgaPbp.Core;

/// <summary>
/// Turns the card database's ability rules text into words a transcript line can carry.
/// </summary>
/// <remarks>
/// The database stores text for Arena's own renderer, which means three kinds of markup
/// a plain-text page must not leak: rich-text tags (<c>&lt;nobr&gt;</c>,
/// <c>&lt;indent=4%&gt;</c>), mana symbols packed as <c>o</c>-runs (<c>{o3oW}</c> is
/// three generic and a white), and <c>CARDNAME</c> standing for whichever card the text
/// is printed on. The shapes handled here are the measured inventory over all 21,119
/// ability rows of the live database, not a guess: symbol runs of digits, single
/// letters, multi-letter names like <c>Si</c>, and parenthesised hybrids like
/// <c>(W/U)</c> cover every brace token but the literal macro <c>{Cost}</c>, which
/// passes through untouched rather than being mangled.
/// </remarks>
public static partial class AbilityText
{
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    // Reminder text: an italic parenthetical. Dropped whole, words and all, before the
    // tag strip — removing only the <i> markers would leave "(This creature deals
    // combat damage before…)" sitting in the clause as if it were rules text, and its
    // full stop would turn a bare keyword into a quoted sentence.
    [GeneratedRegex(@"<i>\s*\([^<]*\)\s*</i>")]
    private static partial Regex Reminder();

    // One o-prefixed symbol inside a brace token: a number, a parenthesised hybrid, a
    // single letter, or snow — the one multi-letter name in the inventory, spelled out
    // rather than patterned, because "[A-Z][a-z]*" reads the o that introduces the NEXT
    // symbol as Si's lowercase tail and {oXoR} came out "{Xo}". An unforeseen name
    // fails the safe way: the run does not match and the token passes through whole,
    // like {Cost}.
    [GeneratedRegex(@"\{(?:o(?:\d+|\([^)]*\)|Si|[A-Z]))+\}")]
    private static partial Regex SymbolRun();

    [GeneratedRegex(@"o(\d+|\([^)]*\)|Si|[A-Z])")]
    private static partial Regex Symbol();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The text as a clause a grant line can end with: markup resolved, and a bare
    /// keyword lowercased so it sits mid-sentence — "Enter the Avatar State gives
    /// Llanowar Elves first strike". A text that is a whole rule rather than a keyword
    /// keeps its capitals and gains quotes, because "gives Toy "When this Class becomes
    /// level 2, …"" is a quotation and reads as one.
    /// </summary>
    public static string Clause(string raw, out bool isKeyword)
    {
        var text = Plain(raw);

        // A keyword names an ability; a rule states one. The difference on the page is
        // that keywords carry no sentence punctuation — "First strike", "Ward {1}" —
        // while a stated rule always does.
        isKeyword = !text.Contains('.') && !text.Contains(':') && !text.Contains(',');
        if (!isKeyword) return $"“{text}”";

        // Mid-sentence, a keyword is not a proper noun. The second letter guards
        // initialisms: none exist in the current inventory, but lowercasing an
        // unforeseen "X" would be worse than leaving it.
        return text.Length >= 2 && char.IsUpper(text[0]) && char.IsLower(text[1])
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;
    }

    /// <summary>Markup resolved, nothing else decided: tags stripped, symbol runs
    /// unpacked, CARDNAME replaced, whitespace collapsed.</summary>
    public static string Plain(string raw)
    {
        var text = Tags().Replace(Reminder().Replace(raw, " "), "");

        text = SymbolRun().Replace(text, run =>
            string.Concat(Symbol().Matches(run.Value)
                .Select(m => $"{{{m.Groups[1].Value.Trim('(', ')')}}}")));

        // The name the text would render on the card it is printed on. On a grant the
        // text now lives on the creature that gained it, and "this creature" is true
        // there without tying the quoted text to a name the label passes may change.
        text = text.Replace("CARDNAME's", "this creature's", StringComparison.Ordinal)
                   .Replace("CARDNAME", "this creature", StringComparison.Ordinal);

        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>
    /// Several granted abilities as one clause — "flying, first strike and lifelink".
    /// One annotation can grant four keywords at once, and four lines saying "gains"
    /// four times is the same fact told worse.
    /// </summary>
    public static string Join(IReadOnlyList<string> clauses) => clauses.Count switch
    {
        0 => "",
        1 => clauses[0],
        _ => $"{string.Join(", ", clauses.Take(clauses.Count - 1))} and {clauses[^1]}"
    };
}
