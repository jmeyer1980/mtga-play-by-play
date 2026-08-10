using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class MatchSlicerTests
{
    private static LogEnvelope Env(long line, long ts, string json) =>
        new(line, ts, JsonDocument.Parse(json).RootElement.Clone());

    private static string GreWithMatch(string matchId) => $$"""
        { "timestamp": "1", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_GameStateMessage",
            "gameStateMessage": { "gameInfo": { "matchID": "{{matchId}}" } } } ] } }
        """;

    private static string RoomFinal(string matchId) => $$"""
        { "timestamp": "9", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
            "gameRoomConfig": { "matchId": "{{matchId}}" },
            "finalMatchResult": { "matchId": "{{matchId}}", "resultList": [
              { "scope": "MatchScope_Match", "winningTeamId": 2 } ] } } } }
        """;

    [Test]
    public void Slice_groups_envelopes_by_match_id()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, GreWithMatch("bbb")),
            Env(3, 300, GreWithMatch("aaa")),
        ]);

        Assert.That(slices.Select(s => s.MatchId), Is.EquivalentTo(new[] { "aaa", "bbb" }));
        Assert.That(slices.Single(s => s.MatchId == "aaa").RawLines, Has.Count.EqualTo(2));
    }

    [Test]
    public void Slice_handles_interleaved_matches()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 150, GreWithMatch("bbb")),
            Env(3, 200, GreWithMatch("aaa")),
            Env(4, 250, GreWithMatch("bbb")),
        ]);
        Assert.That(slices, Has.Count.EqualTo(2));
        Assert.That(slices.All(s => s.RawLines.Count == 2), Is.True);
    }

    [Test]
    public void Slice_reads_match_id_from_room_state_event()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, RoomFinal("ccc"))]);
        Assert.That(slices.Single().MatchId, Is.EqualTo("ccc"));
    }

    [Test]
    public void Slice_marks_match_complete_when_final_result_present()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 900, RoomFinal("aaa")),
        ]);
        Assert.That(slices.Single().Incomplete, Is.False);
    }

    [Test]
    public void Slice_marks_match_incomplete_when_log_was_truncated()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, GreWithMatch("aaa"))]);
        Assert.That(slices.Single().Incomplete, Is.True);
    }

    [Test]
    public void Slice_records_first_and_last_timestamps()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 555, GreWithMatch("aaa")),
        ]);
        var s = slices.Single();
        Assert.That(s.StartedAtMs, Is.EqualTo(100));
        Assert.That(s.EndedAtMs, Is.EqualTo(555));
    }

    [Test]
    public void Slice_drops_envelopes_with_no_match_id()
    {
        var slices = MatchSlicer.Slice([Env(1, 100, """{ "timestamp": "1", "Courses": [] }""")]);
        Assert.That(slices, Is.Empty);
    }
}
