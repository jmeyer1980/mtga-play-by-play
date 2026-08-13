namespace MtgaPbp.Render;

/// <summary>
/// The matches either side of this one in time, for the navigation on a game page.
/// </summary>
/// <remarks>
/// "Newer" and "older" rather than "next" and "previous". The index lists matches
/// newest first, so "next" means the older one there and the opposite everywhere else;
/// naming the direction in time removes the ambiguity, and it survives being read out
/// of context by a screen reader, which is where a bare "Next" says nothing at all.
/// <para>
/// Built from the archive's ledger rather than from rendered summaries. The ledger
/// already carries every match's start time, so the whole ordering is known before the
/// first transcript is extracted — which is what lets a page know its neighbours
/// without a second pass over 212 matches or holding them all in memory.
/// </para>
/// </remarks>
/// <param name="NewerId">Match id of the next match played, or null at the newest.</param>
/// <param name="NewerWhen">When that match was played, for the accessible name.</param>
/// <param name="OlderId">Match id of the previous match played, or null at the oldest.</param>
/// <param name="OlderWhen">When that match was played.</param>
public sealed record Neighbours(
    string? NewerId, string? NewerWhen,
    string? OlderId, string? OlderWhen);
