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

    /// <summary>
    /// A gap stands in for a message the engine sent and the log did not keep, so it
    /// has to inherit the match in progress exactly as a Diff does. Its own line says
    /// nothing about which match it belongs to — and the one match that needs the
    /// warning is precisely the one that would never get it if this were dropped.
    /// </summary>
    [Test]
    public void Slice_attributes_a_gap_to_the_match_in_progress()
    {
        var gap = LogGaps.ToEnvelope(
            new LogGap(LogGapKind.Summarized, 42, 77, 3, ["GameStateMessage"]));

        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            new LogEnvelope(2, 0, gap),
            Env(3, 300, RoomFinal("aaa")),
        ]);

        var s = slices.Single();
        Assert.That(s.Gaps, Is.EqualTo(1));
        Assert.That(s.RawLines, Has.Count.EqualTo(3), "the gap is archived like any other line");

        // A gap carries no timestamp, and must not drag the match's start back to 1970.
        Assert.That(s.StartedAtMs, Is.EqualTo(100));
    }

    [Test]
    public void Slice_drops_a_gap_seen_while_no_match_is_in_progress()
    {
        // Arena summarizes messages outside matches too. One that belongs to no
        // transcript should not be pinned to whichever match happens to be nearby.
        var gap = LogGaps.ToEnvelope(new LogGap(LogGapKind.Summarized, 1, 0, 0, []));
        Assert.That(MatchSlicer.Slice([new LogEnvelope(1, 0, gap)]), Is.Empty);
    }

    // ---------- the deck message, which arrives before its own match ----------

    private const string ConnectWithDeck = """
        { "timestamp": "1", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_ConnectResp", "systemSeatIds": [ 1 ],
            "connectResp": { "deckMessage": { "deckCards": [ 7, 7, 9 ] } } } ] } }
        """;

    /// <summary>The same message with a deck that can be told apart from the others.</summary>
    private static string ConnectWithCard(int grpId) => $$"""
        { "timestamp": "1", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_ConnectResp", "systemSeatIds": [ 1 ],
            "connectResp": { "deckMessage": { "deckCards": [ {{grpId}} ] } } } ] } }
        """;

    /// <summary>
    /// The whole reason the buffer exists. Arena writes the ConnectResp about three
    /// lines before it first names the match that connection opens, so at the moment
    /// it goes past there is no match in progress to attribute it to — 29 of the 35
    /// occurrences in the two current logs look exactly like this, and every one of
    /// them was being dropped.
    /// </summary>
    [Test]
    public void Slice_gives_a_deck_message_to_the_match_that_opens_after_it()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, RoomFinal("aaa")),
            Env(3, 300, ConnectWithDeck),
            Env(4, 400, GreWithMatch("bbb")),
        ]);

        var next = slices.Single(s => s.MatchId == "bbb");
        Assert.That(next.RawLines, Has.Count.EqualTo(2));
        Assert.That(next.RawLines[0], Does.Contain("deckCards"),
            "the buffered line is flushed first, so the slice stays in log order");
        Assert.That(next.HasDeck, Is.True);
        Assert.That(slices.Single(s => s.MatchId == "aaa").HasDeck, Is.False);
    }

    /// <summary>
    /// The failure this replaces. A GameStateType_Diff can carry gameInfo.matchID and
    /// arrive after finalMatchResult, which re-arms the sticky id for a match that has
    /// already ended; the ConnectResp for the <em>next</em> match then inherited it.
    /// That is how one of the 24 decks in the archive ended up filed against the wrong
    /// match, addressed to seat 1 in a match whose local player sat in seat 2.
    /// </summary>
    [Test]
    public void Slice_keeps_a_deck_message_out_of_a_match_that_has_already_ended()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, RoomFinal("aaa")),
            Env(3, 250, GreWithMatch("aaa")),   // a trailing Diff re-arms the sticky id
            Env(4, 300, ConnectWithDeck),
            Env(5, 400, GreWithMatch("bbb")),
        ]);

        Assert.That(slices.Single(s => s.MatchId == "aaa").HasDeck, Is.False);
        Assert.That(slices.Single(s => s.MatchId == "bbb").HasDeck, Is.True);
    }

    /// <summary>
    /// The bound that keeps unrelated traffic out. Anything still waiting when a match
    /// we already know speaks up was not the herald of a new match after all — a
    /// reconnect in the middle of one match must not hand its decklist to the next.
    /// </summary>
    [Test]
    public void Slice_discards_a_buffered_deck_message_once_a_known_match_speaks_again()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, GreWithMatch("aaa")),
            Env(2, 200, ConnectWithDeck),
            Env(3, 300, GreWithMatch("aaa")),   // aaa is still going; the buffer is stale
            Env(4, 400, RoomFinal("aaa")),
            Env(5, 500, GreWithMatch("bbb")),
        ]);

        Assert.That(slices.Single(s => s.MatchId == "aaa").HasDeck, Is.False);
        Assert.That(slices.Single(s => s.MatchId == "bbb").HasDeck, Is.False);
    }

    /// <summary>
    /// The other bound. The longest run of un-attributed engine traffic observed in
    /// the 67 MB of logs this was measured against is one envelope; the cap is two, so
    /// a log full of connection attempts and no matches cannot accumulate.
    /// </summary>
    [Test]
    public void Slice_holds_at_most_two_deck_messages_and_keeps_the_most_recent()
    {
        var slices = MatchSlicer.Slice([
            Env(1, 100, ConnectWithCard(101)),
            Env(2, 200, ConnectWithCard(102)),
            Env(3, 300, ConnectWithCard(103)),
            Env(4, 400, GreWithMatch("aaa")),
        ]);

        // Two held plus the match's own line, and it is the oldest that was dropped:
        // the message nearest the match is the one that belongs to it.
        var lines = slices.Single().RawLines;
        Assert.That(lines, Has.Count.EqualTo(3));
        Assert.That(lines[0], Does.Contain("102"));
        Assert.That(lines[1], Does.Contain("103"));
        Assert.That(lines.Any(l => l.Contains("101", StringComparison.Ordinal)), Is.False);
    }

    /// <summary>
    /// Six of the 35 occurrences share an envelope with a message that names the match.
    /// That name is authoritative, so those must not go through the buffer at all.
    /// </summary>
    [Test]
    public void Slice_leaves_a_deck_message_that_names_its_own_match_where_it_is()
    {
        const string connectAndFull = """
            { "timestamp": "1", "greToClientEvent": { "greToClientMessages": [
              { "type": "GREMessageType_ConnectResp",
                "connectResp": { "deckMessage": { "deckCards": [ 7 ] } } },
              { "type": "GREMessageType_GameStateMessage",
                "gameStateMessage": { "gameInfo": { "matchID": "aaa" } } } ] } }
            """;

        var slices = MatchSlicer.Slice([Env(1, 100, connectAndFull)]);
        Assert.That(slices.Single().MatchId, Is.EqualTo("aaa"));
        Assert.That(slices.Single().HasDeck, Is.True);
    }

    [Test]
    public void Slice_drops_a_deck_message_that_no_match_ever_follows()
    {
        // A log that ends on a connection attempt has nothing to attribute it to, and
        // guessing at the next session's first match is exactly what this must not do.
        Assert.That(MatchSlicer.Slice([Env(1, 100, ConnectWithDeck)]), Is.Empty);
    }
}
