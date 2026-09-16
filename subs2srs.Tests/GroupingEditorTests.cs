//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>The three per-line actions, validation against the limit, undo/redo.</summary>
    public class GroupingEditorTests
    {
        private static GroupingEditor Editor(List<InfoCombined>? lines = null, bool[]? joins = null, int maxMs = 15_000)
        {
            lines ??= SnippetGroupingTests.Dialogue();
            return new GroupingEditor(lines, joins, new SnippetLimits(maxMs, 500));
        }

        [Fact]
        public void AttachBelow_JoinsWithTheNextKeptLine()
        {
            var ed = Editor();
            var r = ed.Apply(0, JoinAction.AttachBelow);
            Assert.True(r.Ok);
            Assert.True(r.Changed);
            Assert.Equal(new[] { true, false, false, false }, ed.Joins);
            Assert.True(ed.IsJoinedBelow(0));
            Assert.True(ed.IsJoinedAbove(1));
            Assert.Equal((0, 1), ed.SnippetRangeOf(1));
            Assert.Equal(2400, ed.TrimmedDurationMsOf(0)); // 1000 + 400 + 1000
        }

        [Fact]
        public void AttachAbove_OnFirstLine_IsANoOp()
        {
            var ed = Editor();
            var r = ed.Apply(0, JoinAction.AttachAbove);
            Assert.True(r.Ok);
            Assert.False(r.Changed);
            Assert.False(ed.CanUndo);
        }

        [Fact]
        public void Detach_SplitsASnippetIntoUpToThree()
        {
            var ed = Editor(joins: new[] { true, true, true, false });
            Assert.Equal((0, 3), ed.SnippetRangeOf(2));

            var r = ed.Apply(2, JoinAction.Detach);
            Assert.True(r.Changed);
            Assert.Equal(new[] { true, false, false, false }, ed.Joins);
            Assert.Equal((0, 1), ed.SnippetRangeOf(0));
            Assert.Equal((2, 2), ed.SnippetRangeOf(2));
            Assert.Equal((3, 3), ed.SnippetRangeOf(3));

            Assert.False(ed.Apply(2, JoinAction.Detach).Changed);
        }

        [Fact]
        public void AttachAbove_WhenAlreadyAttachedAbove_DetachesTheUpperNeighbourOnly()
        {
            var ed = Editor();
            ed.SetJoins(new[] { true, true, true, false }); // all four lines in one snippet

            var r = ed.Apply(1, JoinAction.AttachAbove);

            Assert.True(r.Ok);
            Assert.True(r.Changed);
            Assert.Equal("Detached from the line above.", r.Message);
            Assert.Equal(new[] { false, true, true, false }, ed.Joins); // lower attachment kept
            Assert.False(ed.IsJoinedAbove(1));
            Assert.True(ed.IsJoinedBelow(1));

            // pressing it again attaches again
            var again = ed.Apply(1, JoinAction.AttachAbove);
            Assert.True(again.Changed);
            Assert.StartsWith("Attached above", again.Message);
            Assert.True(ed.IsJoinedAbove(1));

            Assert.True(ed.Undo());
            Assert.Equal(new[] { false, true, true, false }, ed.Joins);
            Assert.True(ed.Undo());
            Assert.Equal(new[] { true, true, true, false }, ed.Joins);
        }

        [Fact]
        public void AttachBelow_WhenAlreadyAttachedBelow_DetachesTheLowerNeighbourOnly()
        {
            var ed = Editor();
            ed.SetJoins(new[] { true, true, true, false });

            var r = ed.Apply(2, JoinAction.AttachBelow);

            Assert.True(r.Changed);
            Assert.Equal("Detached from the line below.", r.Message);
            Assert.Equal(new[] { true, true, false, false }, ed.Joins); // upper attachment kept
            Assert.True(ed.IsJoinedAbove(2));
            Assert.False(ed.IsJoinedBelow(2));
        }

        [Fact]
        public void AttachToggle_SkipsOmittedLinesLikeAttach()
        {
            var lines = SnippetGroupingTests.Dialogue();
            lines[1].Active = false;
            var ed = Editor(lines);
            Assert.True(ed.Apply(2, JoinAction.AttachAbove).Changed); // attaches to line 0, the kept line above
            Assert.True(ed.IsJoinedAbove(2));
            var r = ed.Apply(2, JoinAction.AttachAbove);
            Assert.Equal("Detached from the line above.", r.Message);
            Assert.False(ed.IsJoinedAbove(2));
        }

        [Fact]
        public void Attach_IsRejectedWhenTheSnippetWouldExceedTheLimit()
        {
            var lines = new List<InfoCombined>
            {
                SnippetGroupingTests.Line(0, 8, "a"), SnippetGroupingTests.Line(8.2, 16, "b"),
            };
            var ed = Editor(lines);
            var r = ed.Apply(1, JoinAction.AttachAbove);
            Assert.False(r.Ok);
            Assert.Equal(1000, r.OvershootMs); // 8000 + 200 + 7800 = 16000 vs 15000
            Assert.Contains("1.0 s over", r.Message);
            Assert.Equal(new[] { false, false }, ed.Joins);
            Assert.False(ed.CanUndo);
        }

        [Fact]
        public void InactiveLine_CannotBeGrouped()
        {
            var lines = SnippetGroupingTests.Dialogue();
            lines[1].Active = false;
            var ed = Editor(lines);
            Assert.False(ed.Apply(1, JoinAction.AttachAbove).Ok);
            Assert.Equal(-1, ed.KeptPosition(1));
            Assert.Null(ed.SnippetRangeOf(1));
        }

        [Fact]
        public void UndoRedo_RestoreEarlierVectors()
        {
            var ed = Editor();
            int changes = 0;
            ed.Changed += () => changes++;

            ed.Apply(0, JoinAction.AttachBelow);
            ed.Apply(2, JoinAction.AttachBelow);
            Assert.Equal(new[] { true, false, true, false }, ed.Joins);

            Assert.True(ed.Undo());
            Assert.Equal(new[] { true, false, false, false }, ed.Joins);
            Assert.True(ed.CanRedo);
            Assert.True(ed.Redo());
            Assert.Equal(new[] { true, false, true, false }, ed.Joins);
            Assert.True(ed.Undo());
            Assert.True(ed.Undo());
            Assert.False(ed.Undo());
            Assert.Equal(new[] { false, false, false, false }, ed.Joins);

            ed.Apply(1, JoinAction.AttachBelow); // clears redo
            Assert.False(ed.CanRedo);
            Assert.Equal(7, changes); // the failed Undo raised nothing
        }

        [Fact]
        public void RefreshKept_AfterDeactivation_SkipsTheLineWhenWalkingNeighbours()
        {
            var lines = SnippetGroupingTests.Dialogue();
            var ed = Editor(lines, new[] { true, false, false, false });
            lines[1].Active = false;
            ed.RefreshKept();

            // line 0's slot still says "attached to next kept", which is now line 2
            Assert.Equal((0, 2), ed.SnippetRangeOf(0));
            Assert.Equal(4000, ed.GapToPreviousKeptMs(2)); // 2.0 s -> 6.0 s, the omitted line does not count
        }

        [Fact]
        public void SetJoins_UngroupAll_And_NextDisagreement()
        {
            var ed = Editor();
            ed.SetJoins(new[] { true, false, true, false });
            Assert.True(ed.CanUndo);

            bool[] proposal = { true, false, false, false };
            Assert.Equal(2, ed.NextDisagreement(proposal, -1));
            Assert.Equal(-1, ed.NextDisagreement(proposal, 2));

            ed.UngroupAll();
            Assert.Equal(new[] { false, false, false, false }, ed.Joins);
            Assert.Equal(0, ed.NextDisagreement(proposal, -1));
        }

        [Fact]
        public void SnippetOrdinal_CountsSnippetsFromTheTop()
        {
            var ed = Editor(joins: new[] { true, false, false, false });
            Assert.Equal(0, ed.SnippetOrdinalOf(1));
            Assert.Equal(1, ed.SnippetOrdinalOf(2));
            Assert.Equal(2, ed.SnippetOrdinalOf(3));
        }
    }
}
