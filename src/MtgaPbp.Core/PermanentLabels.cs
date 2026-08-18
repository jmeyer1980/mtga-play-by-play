using System.Globalization;

namespace MtgaPbp.Core;

/// <summary>
/// Names a permanent the way a reader needs it named: bare while it is interchangeable
/// with its identical twins, specific the moment it stops being.
/// </summary>
/// <remarks>
/// <para>
/// The transcript folds runs of the identical line into "… ×5", and that fold is what
/// keeps a game with five 1/1 Rabbits attacking every turn down to one line. Numbering
/// every permanent would turn that one good line back into five noisy ones, so the rule
/// here is deliberately stingy, and it is the same rule at every level:
/// <em>say only what tells two permanents apart.</em>
/// </para>
/// <list type="number">
/// <item>A creature standing at its printed power and toughness is named bare. Five
/// identical Rabbits therefore still produce five identical lines, and the collapse
/// still fires. This is the case that covers almost every line in almost every match.
/// </item>
/// <item>A creature whose statline has been changed carries it — "Rabbit 5/5". The
/// difference is what names it, and a creature that is not interchangeable is worth the
/// line it costs.</item>
/// <item>A letter is added only where the statline, having once told two permanents of
/// the same name apart, stops doing so: they stood side by side at a turn boundary
/// showing different statlines, and at a <em>later</em> boundary showed the same one.
/// Those two get a stable letter, so two 6/6 Rabbits that were 5/5 and 1/1 the turn
/// before read as "Rabbit A 6/6" and "Rabbit B 6/6".</item>
/// </list>
/// <para>
/// That last rule is the one worth being careful about, because it is where the noise
/// would come from, and the order of the two observations is what keeps it quiet. A pack
/// that splits needs nothing: eight Dogs at 2/2, two of which grow to 4/4, are completely
/// described by "Dog 2/2" and "Dog 4/4", and lettering all eight would cost eight lines
/// every time they attack in exchange for nothing. A pair that splits and then converges
/// is the opposite case — a reader who followed "Rabbit 5/5" through turn 27 arrives at
/// two 6/6 Rabbits on turn 29 with no way to know which one they were following, and
/// nothing on the page can tell them. Copies that were never apart in the first place
/// stay anonymous, and a lone Hare Apparent stays "Hare Apparent".
/// </para>
/// <para>
/// The turn boundary is doing real work here too, and not as a fudge. It is the point
/// where "until end of turn" has expired, so what is left is what a permanent actually is
/// rather than what a combat trick briefly made it. Compared instant by instant instead,
/// two Leonin Vanguards whose own triggers pump them in consecutive messages are
/// different for the width of one message and equal for the rest of combat — split, then
/// converged — and would earn permanent names for it. The transcript would pay four extra
/// lines every time they block, to tell apart two creatures that are 2/2 all combat and
/// die together.
/// </para>
/// <para>
/// Letters are assigned across the whole match rather than turn by turn, so the Rabbit
/// that reads "Rabbit A 5/5" on turn 27 is still "Rabbit A" when it is 6/6 on turn 29.
/// That is the whole point of a name: without it the reader can see that something was
/// buffed but not that it was the same something.
/// </para>
/// <para>
/// A letter and not "#2", because a screen reader reads "#" as either nothing or the
/// word "number", and this transcript already goes out of its way to keep bare ids away
/// from a synthesiser. Letters are handed out in instance-id order, which the log fixes,
/// so re-rendering the same match always produces the same letters.
/// </para>
/// </remarks>
public sealed class PermanentLabels
{
    private readonly GameStateTracker _tracker;
    private readonly ICardDb _cards;

    /// <summary>Statline timelines, folded onto one id per physical card.</summary>
    private readonly Dictionary<int, IReadOnlyList<StatSample>> _history = [];

    /// <summary>Name timelines, folded onto one id per physical card.</summary>
    private readonly Dictionary<int, IReadOnlyList<NameSample>> _names = [];

    /// <summary>The permanents that earned a distinguishing letter, and which one.</summary>
    private readonly Dictionary<int, string> _letters = [];

    private PermanentLabels(GameStateTracker tracker, ICardDb cards)
    {
        _tracker = tracker;
        _cards = cards;
    }

    /// <summary>
    /// Works out which permanents need telling apart. Whether they do is a fact about
    /// the whole match, not about any one message, so this can only run once the log has
    /// been read to the end.
    /// </summary>
    /// <param name="boundaries">
    /// Sequence numbers of the turn boundaries, where the board is compared for
    /// differences worth naming. See the remarks on this class for why it is not
    /// compared everywhere.
    /// </param>
    public static PermanentLabels Build(
        GameStateTracker tracker, ICardDb cards, IReadOnlyList<int> boundaries)
    {
        var labels = new PermanentLabels(tracker, cards);

        // Samples are recorded under whatever id Arena was using at the time. Fold them
        // onto the id the alias chain ends at, so a card that changed ids keeps one
        // timeline instead of two half ones.
        var merged = new Dictionary<int, List<StatSample>>();
        foreach (var (id, samples) in tracker.StatHistory)
        {
            var canonical = tracker.Resolve(id);
            if (!merged.TryGetValue(canonical, out var list)) merged[canonical] = list = [];
            list.AddRange(samples);
        }
        foreach (var (id, list) in merged)
            labels._history[id] = list.OrderBy(s => s.Stamp).ToList();

        // Names fold the same way and for the same reason: a rename and an id change can
        // both happen to one card, and two half timelines would answer neither question.
        var mergedNames = new Dictionary<int, List<NameSample>>();
        foreach (var (id, samples) in tracker.NameHistory)
        {
            var canonical = tracker.Resolve(id);
            if (!mergedNames.TryGetValue(canonical, out var list)) mergedNames[canonical] = list = [];
            list.AddRange(samples);
        }
        foreach (var (id, list) in mergedNames)
            labels._names[id] = list.OrderBy(s => s.Stamp).ToList();

        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var id in labels._history.Keys)
        {
            var name = tracker.NameOf(id);
            if (CardNames.IsPlaceholder(name)) continue;
            if (!byName.TryGetValue(name, out var group)) byName[name] = group = [];
            group.Add(id);
        }

        foreach (var group in byName.Values)
        {
            // One of a kind needs no letter — nothing shares its name to confuse it with.
            if (group.Count < 2) continue;

            // Sorted, so the letters follow instance-id order and a re-render of the
            // same log always produces the same ones.
            var confusable = new SortedSet<int>();
            for (var a = 0; a < group.Count; a++)
            {
                for (var b = a + 1; b < group.Count; b++)
                {
                    if (!labels.Confusable(group[a], group[b], boundaries)) continue;
                    confusable.Add(group[a]);
                    confusable.Add(group[b]);
                }
            }

            var next = 0;
            foreach (var id in confusable) labels._letters[id] = Ordinal(next++);
        }

        return labels;
    }

    /// <summary>
    /// True when the statline stopped being able to tell these two apart: at one turn
    /// boundary they were in play together printing different things, and at a later one
    /// they printed the same thing and it was not the bare card name. Pairs that were
    /// never apart, and pairs that split and stayed split, both need nothing — see the
    /// remarks on this class for why the order matters.
    /// </summary>
    private bool Confusable(int a, int b, IReadOnlyList<int> boundaries)
    {
        var toldApart = false;

        foreach (var stamp in boundaries)
        {
            if (StateAt(a, stamp) is not { InPlay: true } first) continue;
            if (StateAt(b, stamp) is not { InPlay: true } second) continue;

            var left = Rendered(a, first);
            var right = Rendered(b, second);

            if (left != right) toldApart = true;
            else if (left is not null && toldApart) return true;
        }
        return false;
    }

    /// <summary>The statline this sample would print, or null for the bare card name.</summary>
    private string? Rendered(int id, StatSample sample) =>
        Printed(id) is { } printed &&
        (sample.Power != printed.Power || sample.Toughness != printed.Toughness)
            ? $"{sample.Power}/{sample.Toughness}"
            : null;

    /// <summary>
    /// The last thing known about the permanent at <paramref name="stamp"/>, in play or
    /// not — unlike <see cref="SampleAt"/>, which answers what it was when last in play.
    /// </summary>
    private StatSample? StateAt(int id, int stamp)
    {
        if (!_history.TryGetValue(id, out var samples)) return null;

        StatSample? found = null;
        foreach (var sample in samples)
        {
            if (sample.Stamp > stamp) break;
            found = sample;
        }
        return found;
    }

    /// <summary>
    /// What this permanent was called at <paramref name="stamp"/>, rather than what it
    /// ended the game called.
    /// </summary>
    /// <remarks>
    /// Only a recorded transition that still resolves to a real name is trusted here.
    /// Everything else defers to <see cref="GameStateTracker.NameOf(int)"/>, whose chain
    /// of card, source and parent links is what names emblems, abilities and tokens that
    /// localize to nothing — none of which this is trying to second-guess.
    /// </remarks>
    public string NameAt(int instanceId, int stamp)
    {
        var id = _tracker.Resolve(instanceId);
        if (_names.TryGetValue(id, out var samples))
        {
            int? loc = null;
            foreach (var sample in samples)
            {
                if (sample.Stamp > stamp) break;
                loc = sample.NameLocId;
            }
            if (loc is { } at && _cards.NameForLocId(at) is { Length: > 0 } named) return named;
        }
        return _tracker.NameOf(instanceId);
    }

    /// <summary>
    /// What to call this permanent on a line emitted at <paramref name="stamp"/>:
    /// "Rabbit", "Rabbit 5/5" or "Rabbit A 6/6", whichever is the least the reader
    /// needs.
    /// </summary>
    public string Label(int instanceId, int stamp)
    {
        var name = NameAt(instanceId, stamp);
        if (CardNames.IsPlaceholder(name)) return name;

        var statline = Statline(instanceId, stamp);
        return statline is null ? name : $"{name}{Letter(instanceId)} {statline}";
    }

    /// <summary>
    /// The distinguishing letter on its own, for the end-of-turn board line — which
    /// prints every creature's statline itself, so <see cref="Label"/> would say it
    /// twice.
    /// </summary>
    public string Suffix(int instanceId, int stamp) =>
        Statline(instanceId, stamp) is null ? "" : Letter(instanceId);

    /// <summary>
    /// What a spell did to what it targeted: "Rabbit A (1/1 → 6/6)". Falls back to the
    /// plain label when the target's statline did not move, which is most spells — a
    /// removal spell's target is not more legible for being told it stayed 1/1.
    /// </summary>
    /// <param name="castStamp">When the spell was cast, giving the "before".</param>
    /// <param name="settledStamp">
    /// When it finished resolving, giving the "after" — Arena applies the layers in a
    /// later message than the cast. Equal to <paramref name="castStamp"/> when the spell
    /// never resolved, which reports no change, because none of it happened.
    /// </param>
    public string Buff(int instanceId, int castStamp, int settledStamp)
    {
        // As of the cast, not the resolution: this line reports what was targeted, and
        // a spell that renames what it enchants would otherwise report the name its own
        // resolution produced — "targeting Legitimate Businessperson" for a creature
        // that was a Phyrexian Germ at the moment the player pointed at it.
        var name = NameAt(instanceId, castStamp);
        if (CardNames.IsPlaceholder(name)) return name;

        var id = _tracker.Resolve(instanceId);
        if (SampleAt(id, castStamp) is { } before && SampleAt(id, settledStamp) is { } after &&
            (before.Power != after.Power || before.Toughness != after.Toughness))
        {
            return $"{name}{Letter(instanceId)} " +
                   $"({before.Power}/{before.Toughness} → {after.Power}/{after.Toughness})";
        }

        return Label(instanceId, settledStamp);
    }

    /// <summary>
    /// The statline to print, or null when it would only repeat what the card says.
    /// </summary>
    private string? Statline(int instanceId, int stamp)
    {
        var id = _tracker.Resolve(instanceId);
        return SampleAt(id, stamp) is { } sample ? Rendered(id, sample) : null;
    }

    /// <summary>
    /// The label carrying the size a permanent had <em>before</em> the change this event
    /// is reporting, for events that are themselves the change.
    /// </summary>
    /// <remarks>
    /// A counter is applied to the tracker before the annotation announcing it is read,
    /// so the plain <see cref="Label"/> reports the size afterwards — "Ajani's Pridemate
    /// 4/4 gets 1 +1/+1 counter", where 4/4 is what it became. That reads as the
    /// starting value, which is the opposite of what it means. Stepping back one sample
    /// rather than one stamp is deliberate: every sample from a single message shares a
    /// stamp, so <c>stamp - 1</c> would still select the post-change size.
    /// </remarks>
    public string LabelBefore(int instanceId, int stamp)
    {
        var name = NameAt(instanceId, stamp);
        if (CardNames.IsPlaceholder(name)) return name;

        var id = _tracker.Resolve(instanceId);
        var statline = SampleBefore(id, stamp) is { } sample ? Rendered(id, sample) : null;
        return statline is null ? name : $"{name}{Letter(id)} {statline}";
    }

    /// <summary>The in-play sample preceding the one <see cref="SampleAt"/> would pick.</summary>
    private StatSample? SampleBefore(int id, int stamp)
    {
        if (!_history.TryGetValue(id, out var samples)) return null;

        StatSample? previous = null, found = null;
        foreach (var sample in samples)
        {
            if (sample.Stamp > stamp) break;
            if (!sample.InPlay) continue;
            previous = found;
            found = sample;
        }
        return previous;
    }

    private string Letter(int instanceId) =>
        _letters.TryGetValue(_tracker.Resolve(instanceId), out var letter) ? " " + letter : "";

    /// <summary>
    /// The statline the permanent had in play at or before <paramref name="stamp"/>.
    /// Samples from off the battlefield are skipped rather than preferred, so the line
    /// that kills a creature still reports what it was when it died instead of whatever
    /// numbers the graveyard copy happens to carry.
    /// </summary>
    private StatSample? SampleAt(int id, int stamp)
    {
        if (!_history.TryGetValue(id, out var samples)) return null;

        StatSample? found = null;
        foreach (var sample in samples)
        {
            if (sample.Stamp > stamp) break;
            if (sample.InPlay) found = sample;
        }
        return found;
    }

    /// <summary>
    /// The card's printed power and toughness. Null when the database has no pair of
    /// numbers for it — an unknown grpId, or a creature printed with "*". Without a
    /// baseline there is nothing to call a change, so those permanents are never
    /// annotated at all; saying less beats guessing.
    /// </summary>
    private (int Power, int Toughness)? Printed(int id)
    {
        if (_tracker.Get(id) is not { } obj) return null;
        if (_cards.CardForGrpId(obj.GrpId) is not { } card) return null;

        return Number(card.Power) is { } power && Number(card.Toughness) is { } toughness
            ? (power, toughness)
            : null;
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>
    /// A, B, … Z, AA, AB — spreadsheet columns, for the board that somehow has more than
    /// twenty-six individually modified copies of one card.
    /// </summary>
    private static string Ordinal(int index)
    {
        var text = "";
        for (var n = index; ; n = n / 26 - 1)
        {
            text = (char)('A' + n % 26) + text;
            if (n < 26) break;
        }
        return text;
    }
}
