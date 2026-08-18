using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Regenerates <c>Fixtures/card-names.json</c> from the real card database.
/// Explicit, because it needs MTG Arena installed and writes into the source tree.
/// </summary>
/// <remarks>
/// Run after changing the sample match:
/// <code>dotnet test --filter "FullyQualifiedName~CardNameFixtureGenerator" -- NUnit.Explicit=true</code>
/// or run it from the IDE.
/// </remarks>
[Explicit("Regenerates a checked-in fixture; needs MTG Arena installed.")]
public class CardNameFixtureGenerator
{
    /// <summary>
    /// Wraps the real database and records every lookup, so the fixture contains
    /// exactly what the sample match asks for — no more, and provably no less.
    /// </summary>
    private sealed class RecordingCardDb(ICardDb inner) : ICardDb
    {
        public readonly Dictionary<int, string> Locs = [];
        public readonly Dictionary<int, FixtureCardDb.Card> Cards = [];
        public readonly Dictionary<string, string> Enums = [];
        public readonly Dictionary<int, string> Abilities = [];

        public string? NameForLocId(int locId)
        {
            var name = inner.NameForLocId(locId);
            if (name is not null) Locs[locId] = name;
            return name;
        }

        public CardInfo? CardForGrpId(int grpId)
        {
            var card = inner.CardForGrpId(grpId);
            if (card is not null)
                Cards[grpId] = new FixtureCardDb.Card(
                    card.Name, card.Types, card.Power, card.Toughness, card.IsToken)
                {
                    ColorIdentity = card.ColorIdentity
                };
            return card;
        }

        public string? EnumName(string type, int value)
        {
            var name = inner.EnumName(type, value);
            if (name is not null) Enums[$"{type}:{value}"] = name;
            return name;
        }

        public string? AbilityText(int abilityGrpId)
        {
            var text = inner.AbilityText(abilityGrpId);
            if (text is not null) Abilities[abilityGrpId] = text;
            return text;
        }
    }

    [Test]
    public void Regenerate()
    {
        var dbPath = CardDb.FindDatabase(null);
        Assert.That(dbPath, Is.Not.Null, "MTG Arena card database not found.");

        using var real = new CardDb(dbPath!);
        var recorder = new RecordingCardDb(real);

        // Every match fixture, into one file. They ask for overlapping cards and the
        // lookups are by id, so a shared file cannot answer either of them differently
        // from the real database — and one file is one thing to keep in step.
        foreach (var (file, matchId) in new[]
        {
            (GoldenFileTests.SampleFixture, GoldenFileTests.SampleMatchId),
            (GoldenFileTests.Bo3Fixture, GoldenFileTests.Bo3MatchId),
        })
        {
            var transcript = new EventExtractor(recorder)
                .Extract(matchId, GoldenFileTests.ReadFixture(file));

            Assert.That(transcript.Events, Is.Not.Empty,
                $"extracting {file} produced nothing to record");
        }

        var data = new FixtureCardDb.Data(
            recorder.Locs.OrderBy(k => k.Key).ToDictionary(k => k.Key, v => v.Value),
            recorder.Cards.OrderBy(k => k.Key).ToDictionary(k => k.Key, v => v.Value),
            recorder.Enums.OrderBy(k => k.Key, StringComparer.Ordinal)
                          .ToDictionary(k => k.Key, v => v.Value))
        {
            Abilities = recorder.Abilities.OrderBy(k => k.Key)
                                          .ToDictionary(k => k.Key, v => v.Value)
        };

        // Write into the source tree, not the copied output directory.
        var sourceFixtures = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Fixtures"));
        var target = FixtureCardDb.Path(sourceFixtures);

        File.WriteAllText(target,
            JsonSerializer.Serialize(data, FixtureCardDb.JsonOptions).ReplaceLineEndings("\n"));

        TestContext.Out.WriteLine(
            $"wrote {target}\n  locs={data.Locs.Count} cards={data.Cards.Count} " +
            $"enums={data.Enums.Count} abilities={data.Abilities.Count} " +
            $"size={new FileInfo(target).Length:N0} bytes");
    }
}
