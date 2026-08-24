using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record CardInfo(
    int GrpId, string Name, string Types, string? Power, string? Toughness, bool IsToken)
{
    /// <summary>
    /// The card's colour identity, exactly as the database stores it: a comma-separated
    /// list of colour ordinals, and the empty string for a colourless card.
    /// <see cref="DeckColors"/> is what reads it.
    /// </summary>
    /// <remarks>
    /// Null means nobody said, which is a different fact from colourless and has to
    /// stay tellable apart from it — a deck of unknown colours renders a blank cell,
    /// a colourless one renders "C". An init property rather than a seventh positional
    /// parameter because the test doubles that construct this record have nothing to
    /// say about colour, and a positional one would have made every one of them
    /// declare an answer.
    /// </remarks>
    public string? ColorIdentity { get; init; }
}

/// <summary>Names <see cref="GameStateTracker.NameOf"/> falls back to when it cannot resolve.</summary>
public static class CardNames
{
    /// <summary>
    /// What an object the client never saw is called. Arena reports that object 348
    /// changed zones without ever having sent its state — genuine fog of war, so
    /// there is nothing to look up. The internal id is not a phrase: on screen it
    /// says nothing, and a synthesiser reads "#348 is put into the graveyard" as
    /// "number three hundred forty-eight is put into the graveyard". This reads as a
    /// card name because every sentence template treats names as proper nouns, so it
    /// needs no article and works mid-sentence as well as at the start.
    /// </summary>
    public const string Unknown = "Unknown card";

    /// <summary>
    /// Arena's face-down card. grpId 3 is the card back rather than a card: it is in no
    /// card database and never will be, and all 245 of the archive's sightings carry
    /// <c>isFacedown: true</c>. It covers foretell, manifest and disguise, an Adventure's
    /// exile, and anything conjured or drafted face down.
    /// </summary>
    /// <remarks>
    /// Deliberately not a placeholder. The others mean "the log did not say", and a line
    /// built on one is worth dropping; this one means "the log said it is face down",
    /// which is a fact about the game and the whole of what there is to know. Treating it
    /// as a gap hid a face-down creature attacking, blocking and dealing damage from the
    /// readable view, leaving life totals that moved with nothing beneath them to say why
    /// — the same failure as #41, arrived at from the other direction (#72).
    /// <para>
    /// Proper-noun shaped for the same reason as <see cref="Unknown"/>: every sentence
    /// template treats a name as a proper noun, so it needs no article and reads at the
    /// start of a line as well as in the middle of one.
    /// </para>
    /// </remarks>
    public const string FaceDown = "Face-down card";

    /// <summary>
    /// True for the three fallbacks: <see cref="Unknown"/> when the object was never
    /// seen, "Card #76729" when its grpId is not in the card database, and a bare
    /// "#123" from any caller predating <see cref="Unknown"/>. Checking only the
    /// first of those let the others leak into search indexes and transcripts.
    /// </summary>
    public static bool IsPlaceholder(string? name) =>
        name is null
        || name.StartsWith('#')
        || name.StartsWith("Card #", StringComparison.Ordinal)
        || string.Equals(name, Unknown, StringComparison.Ordinal);
}

public interface ICardDb
{
    string? NameForLocId(int locId);
    CardInfo? CardForGrpId(int grpId);

    /// <summary>
    /// A localized enum label, e.g. EnumName("Step", 5) is "Declare Attackers".
    /// Null when the value has no label — Phase 0 and Step 0 are both blank.
    /// </summary>
    string? EnumName(string type, int value);

    /// <summary>
    /// The rules text of one ability, by the id <c>AnnotationType_AddAbility</c> grants
    /// under — "First strike", "Ward {o1}", or a whole triggered-ability sentence. Raw
    /// from the database, Arena markup and all; <see cref="AbilityText.Clause"/> is what
    /// turns it into words a transcript can use. Null when the id is in no Abilities row
    /// — grpid 1000001 is granted once in the archive and named nowhere.
    /// </summary>
    string? AbilityText(int abilityGrpId);
}

public sealed record LogEnvelope(long LineNumber, long TimestampMs, JsonElement Root);

public sealed record MatchSlice(
    string MatchId,
    long StartedAtMs,
    long EndedAtMs,
    IReadOnlyList<string> RawLines,
    bool Incomplete,
    /// <summary>
    /// How many <see cref="LogGap"/> records this slice carries. Kept alongside the
    /// lines so the archive can tell that a re-capture learned something a stored copy
    /// does not know, without re-parsing thousands of lines to find out.
    /// </summary>
    int Gaps = 0,

    /// <summary>
    /// Whether this slice carries the local player's registered deck. Kept for the
    /// same reason as <see cref="Gaps"/>: deck capture arrived after matches had
    /// already been archived, and this is what tells a stored copy from a better one
    /// without decompressing and re-parsing it.
    /// </summary>
    bool HasDeck = false);

public enum EventKind
{
    GameStart, Mulligan, TurnStart, PhaseChange,
    LandPlayed, SpellCast, Resolved, Countered,
    Drew, Discarded, Destroyed, Sacrificed, Exiled, Returned,
    StateBasedAction, ZoneMove, Milled, Surveilled, StatsModified, StatsExpired, DoorUnlocked,
    Damage, LifeChanged, TokenCreated, CounterChanged,
    Scry, Revealed, ManaPaid, Attack, Block, BoardSnapshot, Triggered, Activated,
    Attached, LevelUp, AbilityGained, AbilityExpired, Copied,
    GameEnd,

    /// <summary>
    /// A place the log does not account for, said where it happened. The warning above
    /// the transcript reports that something is missing; this reports where, which is
    /// the part a reader can act on.
    /// </summary>
    LogGap,

    Unknown
}

/// <summary>
/// One transcript-relevant occurrence. Wide-and-nullable by design: these are
/// structured log lines sharing a context envelope, and a flat shape keeps the
/// narrator a single switch and serializes without polymorphic converters.
/// </summary>
public sealed record GameEvent
{
    public int Seq { get; init; }
    public long TimestampMs { get; init; }
    public int GameNumber { get; init; }
    public int Turn { get; init; }
    public int ActiveSeat { get; init; }
    public int Phase { get; init; }
    public int Step { get; init; }
    public EventKind Kind { get; init; }

    public int? ActorSeat { get; init; }
    public int? SourceInstanceId { get; init; }
    public string? SourceName { get; init; }
    public int? TargetInstanceId { get; init; }
    public string? TargetName { get; init; }
    public int? TargetSeat { get; init; }
    public int Amount { get; init; }
    public string? Detail { get; init; }
    public string? RawType { get; init; }

    /// <summary>
    /// What caused this to happen, when the log says so — the spell that destroyed a
    /// creature, the ability that exiled it. Distinct from the actor, who is the
    /// player, and from declared targets, which Arena never sends.
    /// </summary>
    public int? CauseInstanceId { get; init; }
    public string? CauseName { get; init; }

    /// <summary>
    /// Where a zone transfer went, and where it came from, as Arena's zone type names.
    /// Only set on <see cref="EventKind.ZoneMove"/> — every other transfer category
    /// already says where it went by being a destroy, an exile, a draw or a mill.
    /// </summary>
    /// <remarks>
    /// Null when the log moved a card through a zone it never described. The narrator
    /// falls back to naming the raw category there, which is worse to read but is at
    /// least still true.
    /// </remarks>
    public string? FromZone { get; init; }
    public string? ToZone { get; init; }

    /// <summary>Life totals by seat, carried on TurnStart so a turn header can show
    /// the score entering the turn. Zero when not applicable.</summary>
    public int LifeSeat1 { get; init; }
    public int LifeSeat2 { get; init; }
}
