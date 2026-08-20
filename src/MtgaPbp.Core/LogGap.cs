using System.Buffers;
using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>Why a stretch of a match never reached the transcript.</summary>
public enum LogGapKind
{
    /// <summary>
    /// Arena itself refused to write the message. Past 50 game objects or 50
    /// annotations it replaces the whole body with one line of prose —
    /// <c>[Message summarized because one or more GameStateMessages exceeded the 50
    /// GameObject or 50 Annotation limit.]</c> — and the state it stood for is gone
    /// for good. Nothing can recover it, so the only honest response is to say so.
    /// </summary>
    Summarized,

    /// <summary>
    /// A JSON envelope that stops part-way through its line. Never observed in the
    /// logs this was built against, but it is what a genuinely damaged log looks
    /// like, and it is indistinguishable from healthy input to a parser that only
    /// counts parse failures.
    /// </summary>
    Torn
}

/// <summary>
/// One place the log does not account for. <see cref="GameObjects"/> and
/// <see cref="Annotations"/> are what Arena said the withheld message contained, so a
/// reader can tell a trivial gap from a large one; both are zero when it did not say.
/// </summary>
public sealed record LogGap(
    LogGapKind Kind,
    long LineNumber,
    int GameObjects,
    int Annotations,
    IReadOnlyList<string> Messages)
{
    /// <summary>
    /// The turn the match had reached when the log stopped accounting for it, and the
    /// game it belonged to. Zero when it fell before the first turn.
    /// </summary>
    /// <remarks>
    /// Worked out from the envelope's position in the stream rather than stored in it,
    /// which is what lets every match already in the archive gain a location without
    /// being captured again. The envelope records what Arena withheld; where it fell is
    /// a property of the walk, and the walk is repeated on every render.
    /// </remarks>
    public int Turn { get; init; }

    public int Game { get; init; } = 1;
}

/// <summary>
/// How a gap survives the trip through the archive.
/// <para>
/// A gap is discovered while scanning, but the transcript is rebuilt from the archive
/// long afterwards, and the archive stores only lines that parsed as JSON — so a gap
/// found at scan time would be dropped before anything could report it. Rather than
/// add a side-channel, the scanner synthesises a JSON envelope for each gap and lets
/// it flow through slicing and archiving like any other line. The archive stays
/// newline-delimited JSON, a re-render needs no second input, and a match captured
/// before this existed simply has none.
/// </para>
/// <para>
/// The property name is deliberately not one Arena could ever emit, so a gap can never
/// be confused with game traffic.
/// </para>
/// </summary>
public static class LogGaps
{
    public const string Property = "mtgaPbpLogGap";

    private const string SummarizedKind = "summarized";
    private const string TornKind = "torn";

    public static JsonElement ToEnvelope(LogGap gap)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject(Property);
            w.WriteString("kind", gap.Kind == LogGapKind.Summarized ? SummarizedKind : TornKind);
            w.WriteNumber("line", gap.LineNumber);
            if (gap.GameObjects > 0) w.WriteNumber("gameObjects", gap.GameObjects);
            if (gap.Annotations > 0) w.WriteNumber("annotations", gap.Annotations);
            if (gap.Messages.Count > 0)
            {
                w.WriteStartArray("messages");
                foreach (var m in gap.Messages) w.WriteStringValue(m);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>The gap this envelope records, or null when it is ordinary traffic.</summary>
    public static LogGap? Read(JsonElement root)
    {
        if (Json.Obj(root, Property) is not { } g) return null;

        var messages = new List<string>();
        foreach (var m in Json.Array(g, "messages"))
            if (m.ValueKind == JsonValueKind.String && m.GetString() is { } s)
                messages.Add(s);

        return new LogGap(
            Json.Str(g, "kind") == TornKind ? LogGapKind.Torn : LogGapKind.Summarized,
            Json.Long(g, "line") ?? 0,
            Json.Int(g, "gameObjects") ?? 0,
            Json.Int(g, "annotations") ?? 0,
            messages);
    }

    /// <summary>True when this envelope is a gap record rather than Arena traffic.</summary>
    public static bool IsGap(JsonElement root) => Json.Obj(root, Property) is not null;
}
