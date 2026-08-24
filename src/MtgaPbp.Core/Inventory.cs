using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// What a player owns that is not a card: the two currencies, vault progress, and the
/// four wildcard counts.
/// </summary>
/// <remarks>
/// Deliberately only the scalars. A real <c>InventoryInfo</c> object runs to tens of
/// kilobytes — <c>Cosmetics</c> alone carries every art style the account has ever been
/// granted — and none of it is a number that moves in a way worth watching.
/// <para>
/// It cannot name a single card, and that is not a gap this can close.
/// <c>GetPlayerCardsV3</c> was removed from the client in August 2021 and never replaced,
/// and the <c>Changes</c> array that would carry deltas is present and empty on all 30
/// snapshots in the logs this was built against. The log can prove thirteen uncommon
/// wildcards were spent and cannot tell you one of their names (#51).
/// </para>
/// </remarks>
/// <param name="FirstSeenUtc">
/// When capture first saw this state, not when Arena wrote it. The snapshot carries no
/// timestamp of its own — the log's own clock line sits two lines above it, outside the
/// JSON — and <c>SeqId</c> is no help either, because it restarts at 1 every session.
/// With <c>watch</c> running this is within seconds of the truth; capturing a week-old
/// log in one go stamps everything it finds with the moment it was read.
/// </param>
public sealed record InventorySnapshot(
    DateTimeOffset FirstSeenUtc,
    int Gems,
    int Gold,
    int VaultProgress,
    int TrackPosition,
    int Commons,
    int Uncommons,
    int Rares,
    int Mythics)
{
    /// <summary>Whether two snapshots describe the same holdings, ignoring when each was seen.</summary>
    public bool SameHoldings(InventorySnapshot other) =>
        (Gems, Gold, VaultProgress, TrackPosition, Commons, Uncommons, Rares, Mythics)
        == (other.Gems, other.Gold, other.VaultProgress, other.TrackPosition,
            other.Commons, other.Uncommons, other.Rares, other.Mythics);
}

public static class Inventory
{
    /// <summary>
    /// The holdings an envelope carries, or null when it is ordinary traffic.
    /// </summary>
    /// <remarks>
    /// Arena writes the object both on its own and hanging off a <c>Course</c> payload,
    /// and most of them arrive the second way — reading only the bare line would miss
    /// them. Both are the same object under the same property, so one lookup covers it.
    /// </remarks>
    public static InventorySnapshot? TryRead(JsonElement root, DateTimeOffset? seenUtc = null)
    {
        if (Json.Obj(root, "InventoryInfo") is not { } info) return null;

        return new InventorySnapshot(
            seenUtc ?? DateTimeOffset.UtcNow,
            Json.Int(info, "Gems") ?? 0,
            Json.Int(info, "Gold") ?? 0,
            Json.Int(info, "TotalVaultProgress") ?? 0,
            Json.Int(info, "wcTrackPosition") ?? 0,
            Json.Int(info, "WildCardCommons") ?? 0,
            Json.Int(info, "WildCardUnCommons") ?? 0,
            Json.Int(info, "WildCardRares") ?? 0,
            Json.Int(info, "WildCardMythics") ?? 0);
    }
}
