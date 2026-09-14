//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// The one range list a grouped card's audio, video and animated snapshot
    /// are cut from: kept lines only, omitted lines cut away, gaps shortened
    /// only when gap removal is on.
    /// </summary>
    public class SnippetMediaTests
    {
        private static InfoCombined Line(double start, double end, string text, bool active = true) =>
            SnippetGroupingTests.Line(start, end, text, active: active);

        private static TimeRange R(double s, double e) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e));

        private static string[] Str(IEnumerable<TimeRange> ranges) => ranges.Select(r => r.ToString()).ToArray();

        /// <summary>A (1-2 s), omitted B (2.4-3.4 s), C (6-7 s); A and C joined.</summary>
        private static List<InfoCombined> AbC() => new()
        {
            Line(1, 2, "A"), Line(2.4, 3.4, "B", active: false), Line(6, 7, "C"),
        };

        private static InfoCombined Card(List<InfoCombined> lines, bool[] joins) =>
            SnippetGrouping.Materialize(lines, joins, "<br>")[0];

        // ── gap removal on ──────────────────────────────────────────────────

        [Fact]
        public void GapRemovalOn_OmittedLineIsOutsideTheKeptRanges()
        {
            var card = Card(AbC(), new[] { true, false, false });
            var ranges = SnippetMedia.RangesFor(card, new CardCutOptions(true, 200));

            // A + 100 ms, 100 ms + C: the 4 s gap (which holds B) shrinks to 200 ms
            Assert.Equal(Str(new[] { R(1, 2.1), R(5.9, 7) }), Str(ranges));
            Assert.Equal(TimeSpan.FromMilliseconds(2200), UtilsGapRemoval.TotalDuration(ranges));
            AssertNeverCovers(ranges, R(2.4, 3.4));
        }

        [Fact]
        public void GapRemovalOn_OmittedLineInsideTheKeptHalfOfAGap_IsStillCutOut()
        {
            // keep 3 s of a 4 s gap: 1.5 s after A would reach 3.5 s and cover B (2.4-3.4)
            var card = Card(AbC(), new[] { true, false, false });
            var ranges = SnippetMedia.RangesFor(card, new CardCutOptions(true, 3000));

            Assert.Equal(Str(new[] { R(1, 2.4), R(3.4, 3.5), R(4.5, 7) }), Str(ranges));
            AssertNeverCovers(ranges, R(2.4, 3.4));
        }

        // ── gap removal off ─────────────────────────────────────────────────

        [Fact]
        public void GapRemovalOff_KeepsTheGaps_ButStillCutsTheOmittedLine()
        {
            var card = Card(AbC(), new[] { true, false, false });
            var ranges = SnippetMedia.RangesFor(card, new CardCutOptions(false, 200));

            Assert.Equal(Str(new[] { R(1, 2.4), R(3.4, 7) }), Str(ranges));
            AssertNeverCovers(ranges, R(2.4, 3.4));
        }

        [Fact]
        public void GapRemovalOff_WithoutOmittedLines_IsOnePaddedRange()
        {
            var lines = new List<InfoCombined> { Line(1, 2, "A"), Line(6, 7, "C") };
            var ranges = SnippetMedia.RangesFor(Card(lines, new[] { true, false }), new CardCutOptions(false, 200, 100, 300));
            Assert.Equal(Str(new[] { R(0.9, 7.3) }), Str(ranges));
        }

        // ── pads, overlaps, plain lines ─────────────────────────────────────

        [Fact]
        public void Pad_ExtendsTheOuterEdgesOnly()
        {
            var card = Card(AbC(), new[] { true, false, false });
            var ranges = SnippetMedia.RangesFor(card, new CardCutOptions(true, 200, 250, 150));
            Assert.Equal(Str(new[] { R(0.75, 2.1), R(5.9, 7.15) }), Str(ranges));
        }

        [Fact]
        public void OmittedLineOverlappingAKeptLine_DoesNotCutTheKeptDialogue()
        {
            // B (1.8-2.6) overlaps A (1-2): only 2.0-2.6 is a hole
            var lines = new List<InfoCombined> { Line(1, 2, "A"), Line(1.8, 2.6, "B", active: false), Line(3, 4, "C") };
            var ranges = SnippetMedia.RangesFor(Card(lines, new[] { true, false, false }), new CardCutOptions(true, 5000));
            Assert.Equal(Str(new[] { R(1, 2), R(2.6, 4) }), Str(ranges));
        }

        [Fact]
        public void OmittedLineAtTheEdgeOfTheGap_LeavesNoEmptyRange()
        {
            // B ends exactly where C starts and starts where A ends
            var lines = new List<InfoCombined> { Line(1, 2, "A"), Line(2, 3, "B", active: false), Line(3, 4, "C") };
            var ranges = SnippetMedia.RangesFor(Card(lines, new[] { true, false, false }), new CardCutOptions(true, 5000));
            Assert.Equal(Str(new[] { R(1, 2), R(3, 4) }), Str(ranges));
            Assert.All(ranges, r => Assert.True(r.End > r.Start));
        }

        [Fact]
        public void PlainLine_HasNoSharedList_UnlessItRecordedSeveralRanges()
        {
            var cut = new CardCutOptions(true, 200);
            var line = Line(1, 4, "a, b");
            Assert.Null(SnippetMedia.RangesFor(line, cut));
            Assert.Null(SnippetMedia.RangesFor(line, cut, 100, 100));

            // sentence-joined line: its own recorded ranges with the worker's own pad
            line.Subs1.Segments = new List<TimeRange> { R(1, 2), R(3, 4) };
            Assert.Null(SnippetMedia.RangesFor(line, cut));
            var ranges = SnippetMedia.RangesFor(line, cut, 100, 50);
            Assert.Equal(Str(new[] { R(0.9, 2.1), R(2.9, 4.05) }), Str(ranges));

            // ... and nothing when gap removal is off
            Assert.Null(SnippetMedia.RangesFor(line, new CardCutOptions(false, 200), 100, 50));
        }

        [Fact]
        public void RangesFor_Lines_MatchesRangesFor_TheMaterializedCard()
        {
            var lines = AbC();
            var opts = new CardCutOptions(true, 200, 100, 100);
            var fromCard = SnippetMedia.RangesFor(Card(lines, new[] { true, false, false }), opts);
            var fromLines = SnippetMedia.RangesFor(lines, 0, 2, opts);
            Assert.Equal(Str(fromCard), Str(fromLines));

            // the worker overload gives the grouped card the shared pad, not the plain one
            var worker = SnippetMedia.RangesFor(Card(lines, new[] { true, false, false }), opts, 999, 999);
            Assert.Equal(Str(fromCard), Str(worker));
        }

        // ── subtraction ─────────────────────────────────────────────────────

        [Fact]
        public void Subtract_HandlesHolesAtEveryPosition()
        {
            var ranges = new List<TimeRange> { R(0, 10) };
            Assert.Equal(Str(new[] { R(0, 10) }), Str(SnippetMedia.Subtract(ranges, new List<TimeRange>())));
            Assert.Equal(Str(new[] { R(2, 10) }), Str(SnippetMedia.Subtract(ranges, new List<TimeRange> { R(-1, 2) })));
            Assert.Equal(Str(new[] { R(0, 8) }), Str(SnippetMedia.Subtract(ranges, new List<TimeRange> { R(8, 12) })));
            Assert.Equal(Str(new[] { R(0, 2), R(3, 5), R(6, 10) }),
                Str(SnippetMedia.Subtract(ranges, new List<TimeRange> { R(2, 3), R(5, 6) })));
            Assert.Empty(SnippetMedia.Subtract(ranges, new List<TimeRange> { R(0, 10) }));
            Assert.Equal(Str(new[] { R(0, 1), R(4, 5) }),
                Str(SnippetMedia.Subtract(new List<TimeRange> { R(0, 2), R(3, 5) }, new List<TimeRange> { R(1, 4) })));
        }

        // ── settings ────────────────────────────────────────────────────────

        [Fact]
        public void FromSettings_UsesTheAudioClipPad_OnlyWhenAudioClipsArePadded()
        {
            Settings.Instance.Reset();
            try
            {
                var s = Settings.Instance;
                s.Snippets.GapRemovalEnabled = false;
                s.Snippets.GapKeepMs = 300;
                s.AudioClips.Enabled = true;
                s.AudioClips.PadEnabled = true;
                s.AudioClips.PadStart = 100;
                s.AudioClips.PadEnd = 150;
                s.VideoClips.PadEnabled = true;
                s.VideoClips.PadStart = 900;
                s.VideoClips.PadEnd = 900;

                var cut = CardCutOptions.FromSettings();
                Assert.False(cut.GapRemoval);
                Assert.Equal(300, cut.GapKeepMs);
                Assert.Equal((100, 150), (cut.PadStartMs, cut.PadEndMs));
                Assert.Equal(250, SnippetLimits.FromSettings().PadMs);

                s.AudioClips.Enabled = false;
                Assert.Equal((0, 0), SnippetMedia.GroupedCardPadMs());
                Assert.Equal(0, SnippetLimits.FromSettings().PadMs);
            }
            finally
            {
                Settings.Instance.Reset();
            }
        }

        private static void AssertNeverCovers(IEnumerable<TimeRange> ranges, TimeRange forbidden)
        {
            foreach (TimeRange r in ranges)
                Assert.True(r.End <= forbidden.Start || r.Start >= forbidden.End, $"{r} overlaps the omitted line {forbidden}");
        }
    }
}
