using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>What one player rolled for the right to choose who begins.</summary>
public sealed record DieRoll(int Seat, int Value);

/// <summary>
/// How the game began, before turn one: the die roll, who ended up on the play, and
/// how far each player mulliganed.
/// </summary>
/// <remarks>
/// Winning the roll and being on the play are kept as two separate facts because they
/// are two separate facts. The winner chooses, and choosing to draw is legal — so a
/// transcript that inferred one from the other would state a falsehood the first time
/// somebody took the draw. Across all 152 archived matches the winner did take the
/// play every time, which is exactly why the divergent case has to be handled here
/// rather than left to be discovered later.
/// </remarks>
public sealed record Opening(
    IReadOnlyList<DieRoll> Rolls,

    /// <summary>
    /// The seat active on turn one, which is the seat on the play. Null when the log
    /// never announced a turn, in which case nothing may be claimed about who would
    /// have gone first. All 152 archived matches do announce one — including the match
    /// that is conceded during the mulligan phase, whose <c>turnInfo</c> never carries
    /// a turn number but which announces the turn and its active player regardless.
    /// </summary>
    int? FirstPlayerSeat,

    /// <summary>
    /// How many times each seat mulliganed, keyed by seat. A seat is present only when
    /// its state was actually read before the first turn; a seat that is absent is one
    /// we know nothing about, which is a different thing from one that kept its hand.
    /// </summary>
    IReadOnlyDictionary<int, int> Mulligans)
{
    /// <summary>
    /// Cards dealt at the start of a game, and after each mulligan. Seven in every
    /// Arena format, and the opening hand was seven cards for both seats in all 152
    /// archived matches.
    /// </summary>
    public const int StartingHandSize = 7;

    /// <summary>
    /// The seat that rolled highest. Null unless exactly two rolls were reported and
    /// they differ — a tie has no winner to name, and Arena would re-roll it. All 152
    /// archived matches carry exactly two rolls and none tied.
    /// </summary>
    public int? WinnerSeat => Rolls.Count == 2 && Rolls[0].Value != Rolls[1].Value
        ? (Rolls[0].Value > Rolls[1].Value ? Rolls[0].Seat : Rolls[1].Seat)
        : null;

    /// <summary>
    /// How many cards a seat kept. Every archived match used the London mulligan, which
    /// deals a fresh seven each time and puts one card back for each mulligan taken, so
    /// the kept hand is seven less the count. Clamped because seven mulligans keep
    /// nothing, and one archived opponent really did mulligan to zero.
    /// </summary>
    public int Kept(int seat) => Math.Clamp(
        StartingHandSize - Mulligans.GetValueOrDefault(seat), 0, StartingHandSize);
}

/// <summary>Reads the opening out of Arena's messages.</summary>
public static class Openings
{
    /// <summary>
    /// The rolls carried by one <c>DieRollResultsResp</c>. Present exactly once, with
    /// exactly two rolls, in all 152 archived matches.
    /// </summary>
    public static IReadOnlyList<DieRoll> ReadRolls(JsonElement message)
    {
        if (Json.Obj(message, "dieRollResultsResp") is not { } resp) return [];

        var rolls = new List<DieRoll>();
        foreach (var roll in Json.Array(resp, "playerDieRolls"))
            if (Json.Int(roll, "systemSeatId") is { } seat &&
                Json.Int(roll, "rollValue") is { } value)
                rolls.Add(new DieRoll(seat, value));
        return rolls;
    }

    /// <summary>
    /// Folds one game state's mulligan counts into <paramref name="mulligans"/>.
    /// </summary>
    /// <remarks>
    /// Arena never writes <c>mulliganCount</c> while it is zero — it is a protobuf
    /// default, and it appears in the JSON only once it has been incremented. That was
    /// checked rather than assumed: across the 152 archived matches there are 29
    /// increments and not one explicit zero. So absence really does mean a kept hand,
    /// but only for a seat whose state was read at all, which is why the seat is
    /// recorded here even when there is no count to record against it.
    /// <para>
    /// The count covers both players, unlike <c>MulliganReq</c>, which only ever
    /// reaches the local seat.
    /// </para>
    /// </remarks>
    public static void ReadMulligans(JsonElement gameState, Dictionary<int, int> mulligans)
    {
        foreach (var player in Json.Array(gameState, "players"))
        {
            if (Json.Int(player, "systemSeatNumber") is not { } seat) continue;

            // Highest wins: the counts arrive as a running total across several
            // messages, and a later state message may simply not repeat it.
            mulligans[seat] = Math.Max(
                mulligans.GetValueOrDefault(seat), Json.Int(player, "mulliganCount") ?? 0);
        }
    }
}
