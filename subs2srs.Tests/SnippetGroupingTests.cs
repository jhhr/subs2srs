//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// Join vectors, trimmed durations, limit repair, materialisation and the
    /// rule-based grouper. No GTK, no ffmpeg.
    /// </summary>
    public class SnippetGroupingTests
    {
        // ── helpers ─────────────────────────────────────────────────────────

        internal static InfoCombined Line(double start, double end, string text, bool active = true, string actor = "")
        {
            var s1 = new InfoLine(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, actor);
            var s2 = new InfoLine(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text.ToUpperInvariant(), actor);
            return new InfoCombined(s1, s2, active);
        }

        internal static List<InfoCombined> Dialogue() => new()
        {
            Line(1.0, 2.0, "Where are you going?"),
            Line(2.4, 3.4, "To the station."),
            Line(6.0, 7.0, "See you later."),
            Line(7.2, 8.2, "Bye."),
        };

        private static SnippetLimits Limits(int maxMs = 15_000, int keepMs = 500, int padMs = 0) => new(maxMs, keepMs, padMs);

        private static TimeRange R(double s, double e) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e));

        // ── kept lines, gaps ────────────────────────────────────────────────

        [Fact]
        public void KeptIndices_SkipsInactiveLines()
        {
            var lines = new List<InfoCombined> { Line(0, 1, "a"), Line(1, 2, "b", active: false), Line(2, 3, "c") };
            Assert.Equal(new[] { 0, 2 }, SnippetGrouping.KeptIndices(lines));
        }

        [Fact]
        public void GapMs_IsNeverNegative()
        {
            Assert.Equal(400, SnippetGrouping.GapMs(Line(1, 2, "a"), Line(2.4, 3, "b")));
            Assert.Equal(0, SnippetGrouping.GapMs(Line(1, 2.5, "a"), Line(2.0, 3, "b")));
        }

        // ── join vectors ────────────────────────────────────────────────────

        [Fact]
        public void JoinsToRanges_And_Back_RoundTrip()
        {
            bool[] joins = { true, false, true, true, false };
            var ranges = SnippetGrouping.JoinsToRanges(joins, 5);
            Assert.Equal(new[] { (0, 1), (2, 4) }, ranges);
            Assert.Equal(new[] { true, false, true, true, false }, SnippetGrouping.RangesToJoins(ranges, 5));
        }

        [Fact]
        public void JoinsToRanges_HandlesEmptyAndSingle()
        {
            Assert.Empty(SnippetGrouping.JoinsToRanges(Array.Empty<bool>(), 0));
            Assert.Equal(new[] { (0, 0) }, SnippetGrouping.JoinsToRanges(new[] { true }, 1));
        }

        [Fact]
        public void ProjectToKept_UsesTheEarlierKeptLinesSlot()
        {
            // line 1 is inactive; joins[0] means "line 0 attached to the next kept line" (line 2)
            var lines = new List<InfoCombined> { Line(0, 1, "a"), Line(1, 2, "b", active: false), Line(2, 3, "c"), Line(3, 4, "d") };
            bool[] full = { true, true, false, false };
            int[] kept = SnippetGrouping.KeptIndices(lines);

            bool[] keptJoins = SnippetGrouping.ProjectToKept(full, kept);
            Assert.Equal(new[] { true, false, false }, keptJoins);

            keptJoins[1] = true;
            SnippetGrouping.ApplyKeptJoins(full, kept, keptJoins);
            Assert.Equal(new[] { true, true, true, false }, full); // inactive slot untouched, last slot cleared
        }

        // ── durations and trimming ──────────────────────────────────────────

        [Fact]
        public void TrimmedDuration_KeepsAtMostGapKeepPerGap()
        {
            var segs = new List<TimeRange> { R(0, 1), R(1.4, 2.4), R(5, 6) };
            Assert.Equal(1000 + 400 + 1000 + 500 + 1000, SnippetGrouping.TrimmedDurationMs(segs, 500));
            Assert.Equal(3000, SnippetGrouping.TrimmedDurationMs(segs, 0));
        }

        [Fact]
        public void TrimmedDuration_OfLines_IncludesAbsorbedInactiveLinesAndPad()
        {
            var lines = new List<InfoCombined> { Line(10, 11, "a"), Line(11.2, 11.7, "b", active: false), Line(13, 14, "c") };
            // 1000 + 200 + 500 + min(1300, 500) + 1000 + pad 300
            Assert.Equal(3500, SnippetGrouping.TrimmedDurationMs(lines, 0, 2, Limits(keepMs: 500, padMs: 300)));
        }

        [Fact]
        public void TrimmedRanges_ShortensLongGapsHalfOnEachSide_AndPadsTheEdges()
        {
            var segs = new List<TimeRange> { R(10, 11), R(11.4, 12.4), R(15, 16) };
            var ranges = SnippetGrouping.TrimmedRanges(segs, 500, 100, 100);

            Assert.Equal(2, ranges.Count);
            Assert.Equal(R(9.9, 12.65).ToString(), ranges[0].ToString());
            Assert.Equal(R(14.75, 16.1).ToString(), ranges[1].ToString());
            Assert.Equal(TimeSpan.FromMilliseconds(4100), UtilsGapRemoval.TotalDuration(ranges));
        }

        [Fact]
        public void TrimmedRanges_SingleSegment_IsJustThePaddedSegment()
        {
            var ranges = SnippetGrouping.TrimmedRanges(new List<TimeRange> { R(0.1, 2) }, 500, 250, 250);
            Assert.Single(ranges);
            Assert.Equal(TimeSpan.Zero, ranges[0].Start); // clamped at 0
            Assert.Equal(TimeSpan.FromSeconds(2.25), ranges[0].End);
        }

        // ── repair ──────────────────────────────────────────────────────────

        [Fact]
        public void Repair_SplitsOverlongRunsAtTheLargestGap()
        {
            var lines = new List<InfoCombined> { Line(0, 8, "a"), Line(9, 17, "b"), Line(20, 28, "c") };
            int[] kept = SnippetGrouping.KeptIndices(lines);
            var log = new List<string>();

            bool[] repaired = SnippetGrouping.Repair(lines, kept, new[] { true, true, false }, Limits(), log);

            Assert.Equal(new[] { false, false, false }, repaired);
            Assert.Equal(2, log.Count);
            Assert.Contains("gap 3000 ms", log[0]); // first split at the 3 s gap
        }

        [Fact]
        public void Repair_LeavesFittingRunsAlone()
        {
            var lines = Dialogue();
            int[] kept = SnippetGrouping.KeptIndices(lines);
            bool[] repaired = SnippetGrouping.Repair(lines, kept, new[] { true, false, true, false }, Limits());
            Assert.Equal(new[] { true, false, true, false }, repaired);
        }

        // ── materialise ─────────────────────────────────────────────────────

        [Fact]
        public void Materialize_WithNoJoins_IsIdentity()
        {
            var lines = Dialogue();
            var cards = SnippetGrouping.Materialize(lines, SnippetGrouping.NoJoins(lines.Count), "<br>");
            Assert.Equal(lines.Count, cards.Count);
            for (int i = 0; i < lines.Count; i++) Assert.Same(lines[i], cards[i]);
        }

        [Fact]
        public void Materialize_BuildsSnippetWithMergedViewAndAbsorbsInactiveLinesInside()
        {
            var lines = new List<InfoCombined>
            {
                Line(1, 2, "A"), Line(2.2, 2.5, "hm", active: false), Line(3, 4, "C"), Line(6, 7, "D", active: false), Line(8, 9, "E"),
            };
            bool[] joins = { true, false, false, false, false }; // A attached to next kept (C)

            var cards = SnippetGrouping.Materialize(lines, joins, "<br>", (l, f, la) => $"{f}-{la}");

            Assert.Equal(3, cards.Count);
            InfoCombined snippet = cards[0];
            Assert.True(snippet.IsSnippet);
            Assert.Equal(3, snippet.PartCount);
            Assert.Equal("A<br>hm<br>C", snippet.Subs1.Text);
            Assert.Equal("A<br>HM<br>C", snippet.Subs2.Text);
            Assert.Equal(TimeSpan.FromSeconds(1), snippet.Subs1.StartTime);
            Assert.Equal(TimeSpan.FromSeconds(4), snippet.Subs1.EndTime);
            Assert.True(snippet.Active);
            Assert.Equal("0-2", snippet.GroupNote);
            Assert.Same(lines[3], cards[1]); // inactive single passes through
            Assert.Same(lines[4], cards[2]);

            var segs = snippet.Segments();
            Assert.Equal(3, segs.Count);
            Assert.Equal(TimeSpan.FromSeconds(2.2), segs[1].Start);
        }

        [Fact]
        public void Materialize_SkipsEmptyTextsWhenMerging()
        {
            var lines = new List<InfoCombined> { Line(1, 2, "A"), Line(2.1, 3, ""), Line(3.1, 4, "C") };
            var cards = SnippetGrouping.Materialize(lines, new[] { true, true, false }, " ");
            Assert.Single(cards);
            Assert.Equal("A C", cards[0].Subs1.Text);
        }

        [Fact]
        public void Flatten_IsTheInverseOfMaterialize()
        {
            var lines = Dialogue();
            lines[1].Active = false;
            bool[] joins = { true, false, true, false };

            var cards = SnippetGrouping.Materialize(lines, joins, "<br>");
            var (flat, back) = SnippetGrouping.Flatten(cards);

            Assert.Equal(lines.Count, flat.Count);
            for (int i = 0; i < lines.Count; i++) Assert.Same(lines[i], flat[i]);
            Assert.Equal(joins, back);
        }

        [Fact]
        public void CreateSnippet_SinglePart_ReturnsThePartItself()
        {
            var line = Line(1, 2, "a");
            Assert.Same(line, InfoCombined.CreateSnippet(new[] { line }, "<br>", "note"));
            Assert.Equal("note", line.GroupNote);
        }

        // ── segments on plain lines ─────────────────────────────────────────

        [Fact]
        public void Segments_UsesRecordedSentenceJoinRangesWhenConsistent()
        {
            var line = Line(1, 4, "a, b");
            line.Subs1.Segments = new List<TimeRange> { R(1, 2), R(3, 4) };
            Assert.Equal(2, line.Segments().Count);

            line.Subs1.Segments = new List<TimeRange> { R(1, 2), R(3, 5) }; // exceeds EndTime
            var segs = line.Segments();
            Assert.Single(segs);
            Assert.Equal(R(1, 4).ToString(), segs[0].ToString());
        }

        [Fact]
        public void Shift_MovesRecordedSegmentsToo()
        {
            var line = Line(1, 4, "a, b");
            line.Subs1.Segments = new List<TimeRange> { R(1, 2), R(3, 4) };
            line.Subs1.Shift(500);
            Assert.Equal(TimeSpan.FromSeconds(1.5), line.Subs1.StartTime);
            Assert.Equal(TimeSpan.FromSeconds(3.5), line.Subs1.Segments[1].Start);
        }

        [Fact]
        public void Clone_PreservesPartsAndSegments()
        {
            var lines = Dialogue();
            var cards = SnippetGrouping.Materialize(lines, new[] { true, false, false, false }, "<br>");
            var copy = ObjectCopier.Clone(cards);
            Assert.True(copy[0].IsSnippet);
            Assert.Equal(2, copy[0].Segments().Count);
            Assert.Equal(cards[0].Subs1.Text, copy[0].Subs1.Text);
        }

        // ── rule-based grouper ──────────────────────────────────────────────

        [Fact]
        public void Rules_JoinQuestionAndAnswer_ButNotAcrossLongGapOrWithoutCue()
        {
            var lines = Dialogue();
            bool[] joins = RuleBasedGrouper.GroupEpisode(lines, Limits(), new RuleGrouperOptions());
            Assert.Equal(new[] { true, false, false, false }, joins);
        }

        [Fact]
        public void Rules_WithoutCueRequirement_JoinEverythingWithinTheGap()
        {
            var lines = Dialogue();
            bool[] joins = RuleBasedGrouper.GroupEpisode(lines, Limits(), new RuleGrouperOptions { RequireCue = false });
            Assert.Equal(new[] { true, false, true, false }, joins);
        }

        [Fact]
        public void Rules_SpeakerChangeIsACue()
        {
            var lines = new List<InfoCombined> { Line(1, 2, "Hello.", actor: "A"), Line(2.3, 3, "Hi.", actor: "B"), Line(3.2, 4, "Yes.", actor: "B") };
            bool[] joins = RuleBasedGrouper.GroupEpisode(lines, Limits(), new RuleGrouperOptions());
            Assert.Equal(new[] { true, false, false }, joins);

            joins = RuleBasedGrouper.GroupEpisode(lines, Limits(), new RuleGrouperOptions { JoinOnActorChange = false });
            Assert.Equal(new[] { false, false, false }, joins);
        }

        [Fact]
        public void Rules_RespectTheDurationLimit()
        {
            var lines = new List<InfoCombined> { Line(0, 8, "a?"), Line(8.5, 14, "b?"), Line(14.5, 20, "c?") };
            bool[] joins = RuleBasedGrouper.GroupEpisode(lines, Limits(15_000), new RuleGrouperOptions());
            Assert.Equal(new[] { true, false, false }, joins);
        }

        [Fact]
        public void Rules_SkipInactiveLinesWhenLookingForNeighbours()
        {
            var lines = Dialogue();
            lines[1].Active = false; // "To the station." omitted; next kept is 2.6 s later -> no join
            bool[] joins = RuleBasedGrouper.GroupEpisode(lines, Limits(), new RuleGrouperOptions());
            Assert.Equal(new[] { false, false, false, false }, joins);
        }

        [Fact]
        public void EndsWithCue_IgnoresClosingQuotes()
        {
            var opt = new RuleGrouperOptions();
            Assert.True(RuleBasedGrouper.EndsWithCue(Line(0, 1, "「行くの？」"), opt));
            Assert.True(RuleBasedGrouper.EndsWithCue(Line(0, 1, "and then…"), opt));
            Assert.False(RuleBasedGrouper.EndsWithCue(Line(0, 1, "Done."), opt));
            Assert.False(RuleBasedGrouper.EndsWithCue(Line(0, 1, ""), opt));
        }

        [Fact]
        public void Note_DescribesEachJoin()
        {
            var lines = Dialogue();
            string note = RuleBasedGrouper.Note(lines, 0, 1, new RuleGrouperOptions());
            Assert.Equal("rules: cue 400 ms", note);
        }

        [Fact]
        public void FromSettings_ReadsProjectSettings()
        {
            Settings.Instance.Reset();
            Settings.Instance.Snippets.MaxSnippetSeconds = 12;
            Settings.Instance.Snippets.GapKeepMs = 300;
            Settings.Instance.AudioClips.Enabled = true;
            Settings.Instance.AudioClips.PadEnabled = true;
            Settings.Instance.AudioClips.PadStart = 100;
            Settings.Instance.AudioClips.PadEnd = 150;
            try
            {
                var limits = SnippetLimits.FromSettings();
                Assert.Equal(12_000, limits.MaxSnippetMs);
                Assert.Equal(300, limits.GapKeepMs);
                Assert.Equal(250, limits.PadMs);
            }
            finally
            {
                Settings.Instance.Reset();
            }
        }
    }
}
