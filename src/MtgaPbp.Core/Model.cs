using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record CardInfo(
    int GrpId, string Name, string Types, string? Power, string? Toughness, bool IsToken);

public interface ICardDb
{
    string? NameForLocId(int locId);
    CardInfo? CardForGrpId(int grpId);

    /// <summary>
    /// A localized enum label, e.g. EnumName("Step", 5) is "Declare Attackers".
    /// Null when the value has no label — Phase 0 and Step 0 are both blank.
    /// </summary>
    string? EnumName(string type, int value);
}

public sealed record LogEnvelope(long LineNumber, long TimestampMs, JsonElement Root);

public sealed record MatchSlice(
    string MatchId,
    long StartedAtMs,
    long EndedAtMs,
    IReadOnlyList<string> RawLines,
    bool Incomplete);

public enum EventKind
{
    GameStart, Mulligan, TurnStart, PhaseChange,
    LandPlayed, SpellCast, Resolved, Countered,
    Drew, Discarded, Destroyed, Sacrificed, Exiled, Returned,
    StateBasedAction, ZoneMove,
    Damage, LifeChanged, TokenCreated, CounterChanged,
    Scry, Revealed, ManaPaid, Attack, Block, BoardSnapshot, GameEnd, Unknown
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

    /// <summary>Life totals by seat, carried on TurnStart so a turn header can show
    /// the score entering the turn. Zero when not applicable.</summary>
    public int LifeSeat1 { get; init; }
    public int LifeSeat2 { get; init; }
}
