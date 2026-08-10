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

    /// <summary>
    /// Only GameStateType_Full carries gameInfo.matchID. In the real log that is 74
    /// lines out of 4,774 — every Diff, which is where the annotations live, has no
    /// match id at all and must inherit the match already in progress.
    /// </summary>
    [Test]
    public void Slice_attributes_diff_states_to_the_match_already_in_progress()
    {
        const string diff = """
            { "timestamp": "2", "greToClientEvent": { "greToClientMessages": [
              { "type": "GREMessageType_GameStateMessage",
                "gameStateMessage": { "type": "GameStateType_Diff", "annotations": [] } } ] } }
            """;

        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, diff),
            Env(3, 300, diff),
        ]);

        Assert.That(slices.Single().RawLines, Has.Count.EqualTo(3));
    }

    [Test]
    public void Slice_switches_context_when_a_new_match_id_appears()
    {
        const string diff = """
            { "timestamp": "2", "greToClientEvent": { "greToClientMessages": [
              { "type": "GREMessageType_GameStateMessage",
                "gameStateMessage": { "type": "GameStateType_Diff" } } ] } }
            """;

        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, diff),
            Env(3, 300, GreWithMatch("bbb")),
            Env(4, 400, diff),
        ]);

        Assert.That(slices.Single(s => s.MatchId == "aaa").RawLines, Has.Count.EqualTo(2));
        Assert.That(slices.Single(s => s.MatchId == "bbb").RawLines, Has.Count.EqualTo(2));
    }

    [Test]
    public void Slice_ignores_orphan_diff_states_seen_before_any_match_starts()
    {
        const string diff = """
            { "timestamp": "2", "greToClientEvent": { "greToClientMessages": [
              { "type": "GREMessageType_GameStateMessage",
                "gameStateMessage": { "type": "GameStateType_Diff" } } ] } }
            """;
        Assert.That(MatchSlicer.Slice([Env(1, 100, diff)]), Is.Empty);
    }
}
