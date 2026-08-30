namespace MtgaPbp.Cli;

/// <summary>
/// Whether applying the retention cap would take a big enough bite out of the archive
/// to look like a mistake rather than like housekeeping.
/// </summary>
/// <remarks>
/// A rule with numbers in it rather than a judgement made at the call site, because
/// this decides whether matches are deleted from the only copy that exists — there is
/// no recycle bin behind <c>File.Delete</c> and no undo behind the archive. Anything
/// answering "is this a lot?" by feel is answering it differently on different days.
/// <para>
/// Both conditions have to hold. The share alone would nag a small archive over a
/// handful of matches — three leaving a twenty-match archive is 15% of it and still
/// three matches. The count alone would wave through a prune that takes most of a large
/// one, which is exactly the case this exists for: a cap of 50 typed against an archive
/// of 1,200 (#133).
/// </para>
/// </remarks>
public static class RetentionGuard
{
    /// <summary>A prune of this many matches or fewer is routine, whatever the share.</summary>
    public const int Routine = 10;

    /// <summary>Above <see cref="Routine"/>, the share of the archive that is too much to take at once.</summary>
    public const double Share = 0.10;

    /// <param name="doomed">How many matches the cap would remove.</param>
    /// <param name="archived">How many the archive holds, favourites included.</param>
    public static bool WouldBeLarge(int doomed, int archived) =>
        doomed > Routine && doomed > archived * Share;
}
