using MtgaPbp.Core;

namespace MtgaPbp.Render;

/// <summary>
/// Which of a listed card's other faces the list gives a home to (#221). Shared by
/// the page and the markdown export so that the two — and the clipboard, which walks
/// the page — nest exactly the same names under exactly the same entries.
/// </summary>
/// <remarks>
/// Two rules, both about not saying a thing twice. A face that is itself an entry in
/// the list has its own peek, so it is not nested under another; the opponent's list
/// is built from what was seen and can carry both halves of one card. And a face two
/// entries could claim — a Room's door and the whole Room are both listable, and both
/// know the other door — is homed under the first, in list order; the lists are sorted
/// by name, so which entry that is never changes between builds.
/// </remarks>
internal static class FaceHomes
{
    public static IReadOnlyDictionary<string, IReadOnlyList<CardFace>> Of(
        IEnumerable<string> listed, IReadOnlyDictionary<string, CardFace>? faces)
    {
        var homes = new Dictionary<string, IReadOnlyList<CardFace>>(StringComparer.Ordinal);
        if (faces is null || faces.Count == 0) return homes;

        var names = listed.ToList();
        var taken = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (faces.GetValueOrDefault(name) is not { OtherFaces.Count: > 0 } face) continue;
            var under = new List<CardFace>();
            foreach (var other in face.OtherFaces)
                if (taken.Add(other.Name)) under.Add(other);
            if (under.Count > 0 && !homes.ContainsKey(name)) homes[name] = under;
        }
        return homes;
    }

    /// <summary>The faces homed under one entry — none, for most.</summary>
    public static IReadOnlyList<CardFace> Under(
        this IReadOnlyDictionary<string, IReadOnlyList<CardFace>> homes, string name) =>
        homes.GetValueOrDefault(name) ?? [];
}
