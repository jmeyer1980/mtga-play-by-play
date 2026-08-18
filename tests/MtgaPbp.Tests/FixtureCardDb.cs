using System.Text.Json;
using System.Text.Json.Serialization;
using MtgaPbp.Core;

namespace MtgaPbp.Tests;

/// <summary>
/// The card names the sample match needs, captured from Arena's real card database
/// into a small checked-in file.
/// </summary>
/// <remarks>
/// The real database is 237 MB and cannot be committed, which meant the golden-file
/// tests only ran on a machine with Arena installed — the end-to-end check never ran
/// in CI. This holds only the entries that match actually asks for, so the same test
/// runs everywhere.
/// <para>
/// Regenerate with <c>CardNameFixtureGenerator</c> after changing the sample match.
/// </para>
/// </remarks>
public sealed class FixtureCardDb : ICardDb
{
    public sealed record Card(
        string Name, string Types, string? Power, string? Toughness, bool IsToken)
    {
        /// <summary>
        /// Mirrors <see cref="CardInfo.ColorIdentity"/>, and an init property for the
        /// same reason <see cref="Data.Abilities"/> is one: a card-names.json written
        /// before colours existed still deserializes, and reads back as "nobody said".
        /// </summary>
        public string? ColorIdentity { get; init; }
    }

    public sealed record Data(
        Dictionary<int, string> Locs,
        Dictionary<int, Card> Cards,
        Dictionary<string, string> Enums)
    {
        /// <summary>
        /// Ability rules texts by grpid, for the grants the fixture matches carry. An
        /// init property with a default rather than a fourth positional parameter so a
        /// card-names.json written before abilities existed still deserializes.
        /// </summary>
        public Dictionary<int, string> Abilities { get; init; } = [];
    }

    private readonly Data _data;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private FixtureCardDb(Data data) => _data = data;

    public static string Path(string fixtureDir) =>
        System.IO.Path.Combine(fixtureDir, "card-names.json");

    public static FixtureCardDb Load(string fixtureDir)
    {
        var path = Path(fixtureDir);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Card name fixture missing at {path}. Run the CardNameFixtureGenerator " +
                "test on a machine with MTG Arena installed to regenerate it.", path);

        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException($"{path} is empty or malformed.");
        return new FixtureCardDb(data);
    }

    public string? NameForLocId(int locId) =>
        _data.Locs.TryGetValue(locId, out var name) ? name : null;

    public CardInfo? CardForGrpId(int grpId) =>
        _data.Cards.TryGetValue(grpId, out var c)
            ? new CardInfo(grpId, c.Name, c.Types, c.Power, c.Toughness, c.IsToken)
              { ColorIdentity = c.ColorIdentity }
            : null;

    public string? EnumName(string type, int value) =>
        _data.Enums.TryGetValue($"{type}:{value}", out var name) ? name : null;

    public string? AbilityText(int abilityGrpId) =>
        _data.Abilities.TryGetValue(abilityGrpId, out var text) ? text : null;
}
