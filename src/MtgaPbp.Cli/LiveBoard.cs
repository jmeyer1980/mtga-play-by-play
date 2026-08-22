using MtgaPbp.Render;

namespace MtgaPbp.Cli;

/// <summary>
/// Draws the scoreboard at the foot of the terminal and keeps it there.
/// </summary>
/// <remarks>
/// The block is redrawn in place after every match, so the standing state never
/// accumulates. Anything genuinely notable — a nudge, a verdict — is printed
/// <em>above</em> it with <see cref="Say"/>, where it scrolls normally and stays in the
/// scrollback. That split is the whole design: before this, the repetitive half
/// accumulated 41 lines an evening and the rare half was lost in it.
/// <para>
/// Everything degrades to plain appended lines when the cursor cannot be moved — output
/// redirected to a file, a terminal that reports no size, or any environment that throws
/// on <see cref="Console.SetCursorPosition"/>. `mtga-pbp watch &gt; log.txt` has to
/// produce a readable log rather than a screenful of control characters.
/// </para>
/// </remarks>
public sealed class LiveBoard
{
    private bool _canRepaint;
    private int _drawn;

    public LiveBoard()
    {
        // Asked once, and defensively: a redirected stream reports a width of zero on
        // some hosts and throws on others, and both mean the same thing here.
        try
        {
            _canRepaint = !Console.IsOutputRedirected && Console.WindowWidth > 20;
        }
        catch (IOException) { _canRepaint = false; }
    }

    public int Width => Size().Width;

    public int Height => Size().Height;

    /// <summary>
    /// Prints a line that stays: it scrolls with the terminal and survives every later
    /// repaint. For the handful of things worth keeping, never for per-match chatter.
    /// </summary>
    public void Say(string line)
    {
        Erase();
        Console.WriteLine(line);
    }

    /// <summary>Replaces the pinned block with <paramref name="lines"/>.</summary>
    public void Draw(IReadOnlyList<string> lines)
    {
        Erase();
        foreach (var line in lines) Console.WriteLine(line);

        // Only claimed as pinned if it can actually be taken back. Erase may have given
        // up on repainting a moment ago, and a block recorded as pinned when it is not
        // would have the next Erase scroll good output off the top of the window.
        _drawn = _canRepaint ? lines.Count : 0;
    }

    /// <summary>
    /// Takes the block back off the screen, so whatever is written next lands where the
    /// block began. A no-op when nothing is pinned, which is also the redirected case.
    /// </summary>
    /// <remarks>
    /// A failure here is permanent, not transient. If the cursor cannot be moved once,
    /// the position this class believes it is at is no longer trustworthy, and carrying
    /// on would append blocks that later erases would try to remove from the wrong row.
    /// Falling back to plain appended lines is ugly; erasing the wrong rows is
    /// destructive.
    /// </remarks>
    private void Erase()
    {
        if (!_canRepaint || _drawn == 0) return;
        try
        {
            var width = Console.WindowWidth;
            var top = Math.Max(0, Console.CursorTop - _drawn);
            Console.SetCursorPosition(0, top);

            // WriteLine rather than a bare "\n": on a console host that does not
            // translate a line feed into a carriage return as well, the next row of
            // spaces would begin at the end of the previous one and wrap, leaving
            // fragments of the very block being erased. WriteLine emits the platform's
            // own newline, which returns to column zero everywhere.
            //
            // Spaces rather than an escape sequence, because this has to work in a plain
            // console host with no virtual-terminal processing — still what a
            // double-clicked window gets.
            var blank = new string(' ', Math.Max(0, width - 1));
            for (var i = 0; i < _drawn; i++) Console.WriteLine(blank);
            Console.SetCursorPosition(0, top);
        }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException)
        {
            // The window went away or was resized under us. Everything below is now
            // guesswork, so stop guessing for the rest of the run.
            _canRepaint = false;
        }
        _drawn = 0;
    }

    private (int Width, int Height) Size()
    {
        if (!_canRepaint) return (80, 24);
        try { return (Console.WindowWidth, Console.WindowHeight); }
        catch (IOException) { return (80, 24); }
    }
}
