using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// Safe accessors for Arena's loosely-typed JSON.
/// <para>
/// <see cref="JsonElement.TryGetInt32"/> is a trap: it returns false only when a
/// number will not fit, and <b>throws</b> when the element is not a number at all.
/// Arena sends fields that are usually numeric as strings or booleans often enough
/// that reading one directly will crash on real logs. Everything here checks
/// <see cref="JsonElement.ValueKind"/> first and returns null rather than throwing.
/// </para>
/// </summary>
internal static class Json
{
    public static int? Int(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    public static int? Int(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var el)
            ? Int(el)
            : null;

    public static long? Long(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    public static string? Str(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var el) &&
        el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static bool Bool(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var el) &&
        el.ValueKind == JsonValueKind.True;

    /// <summary>The named property when it is an array, otherwise an empty enumeration.</summary>
    public static JsonElement.ArrayEnumerator Array(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var el) &&
        el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray()
            : default;

    /// <summary>The named property when it is an object, otherwise null.</summary>
    public static JsonElement? Obj(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var el) &&
        el.ValueKind == JsonValueKind.Object
            ? el
            : null;
}
