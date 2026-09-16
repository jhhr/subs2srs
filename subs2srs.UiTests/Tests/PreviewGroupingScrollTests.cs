//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// Grouping edits in a long Preview list: the list must not scroll, the
    /// selection stays on the edited line, attach on an attached side detaches
    /// that side only, the buttons relabel, and every default key works.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class PreviewGroupingScrollTests
    {
        private readonly GtkFixture _gtk;

        public PreviewGroupingScrollTests(GtkFixture gtk) => _gtk = gtk;

        private static string WriteLongSrt(string dir, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                double start = 1.0 + i * 1.3;
                sb.Append(i + 1).Append("\r\n");
                sb.Append(Srt(start)).Append(" --> ").Append(Srt(start + 1.0)).Append("\r\n");
                sb.Append("Line number ").Append(i + 1).Append(".\r\n\r\n");
            }
            string path = Path.Combine(dir, "long.srt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string Srt(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }

        [RequiresFfmpegFact]
        public async Task GroupingEdits_KeepTheListStill_AndToggleOneSide()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            string srt = WriteLongSrt(scope.TempDir, 150);

            var win = await scope.OpenMainWindowAsync();
            DialogPreview preview = null!;
            const int row = 100;

            await _gtk.RunOnGtkAsync(async () =>
            {
                win._txtSubs1.SetText(srt);
                win._txtSubs2.SetText("");
                win._txtVideo.SetText(TestMedia.VideoPath);
                win._txtOutputDir.SetText(scope.OutputDir);
                win._txtDeckName.SetText("ScrollDeck");
                win._chkGenerateAudio.SetActive(true);
                win._chkGenerateSnapshots.SetActive(false);
                win._chkGenerateVideo.SetActive(false);
                Settings.Instance.Snippets.Mode = SnippetMode.Off;
                ConstantSettings.GroupingKeyAttachAbove = PrefDefaults.GroupingKeyAttachAbove;
                ConstantSettings.GroupingKeyAttachBelow = PrefDefaults.GroupingKeyAttachBelow;
                ConstantSettings.GroupingKeyDetach = PrefDefaults.GroupingKeyDetach;

                preview = win.ShowPreview();
                await Pump.SettleAsync(preview);
                await Pump.WaitUntilAsync(() => !preview.IsRunning && preview.ProgressText == "Preview ready",
                    TimeSpan.FromSeconds(60), "preview ready");
                await Pump.IdleAsync();

                preview.SelectRow(row);
                await Pump.FramesAsync(preview, 3);
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(2));

            double scrolled = _gtk.RunOnGtk(() => preview.ListScrollValueForTests);
            Assert.True(scrolled > 0, $"row {row} should be scrolled into view (scroll value {scrolled})");
            Assert.Equal(("Attach ↑", "Attach ↓"), _gtk.RunOnGtk(() => preview.AttachButtonLabelsForTests));

            // One step: run an edit on the GTK thread, let a few frames render, then check stillness.
            async Task<(double scroll, int selected, bool above, bool below, (string, string) labels, string status)> Step(Func<object> edit)
            {
                return await _gtk.RunOnGtkAsync(async () =>
                {
                    edit();
                    await Pump.FramesAsync(preview, 3);
                    await Pump.IdleAsync();
                    var ed = preview.CurrentEditor;
                    return (preview.ListScrollValueForTests, preview.SelectedRowForTests,
                            ed.IsJoinedAbove(row), ed.IsJoinedBelow(row), preview.AttachButtonLabelsForTests,
                            preview.GroupStatusTextForTests);
                });
            }

            void AssertStill((double scroll, int selected, bool, bool, (string, string), string) s, string what)
            {
                Assert.True(Math.Abs(s.scroll - scrolled) < 0.5, $"{what}: list scrolled from {scrolled} to {s.scroll}");
                Assert.True(s.selected == row, $"{what}: selection moved to row {s.selected}");
            }

            // Button: attach above, then below
            var s1 = await Step(() => preview.ApplyToSelected(JoinAction.AttachAbove));
            AssertStill(s1, "Attach ↑ button");
            Assert.True(s1.above);
            Assert.Equal(("Detach ↑", "Attach ↓"), s1.labels);

            var s2 = await Step(() => preview.ApplyToSelected(JoinAction.AttachBelow));
            AssertStill(s2, "Attach ↓ button");
            Assert.True(s2.above && s2.below);
            Assert.Equal(("Detach ↑", "Detach ↓"), s2.labels);

            // Key "w" on an attached-above line: detach from above only, keep below
            var s3 = await Step(() => preview.OnListKeyPressed((uint)Gdk.Constants.KEY_w, Gdk.ModifierType.NoModifierMask));
            AssertStill(s3, "w key");
            Assert.False(s3.above);
            Assert.True(s3.below);
            Assert.Equal("Detached from the line above.", s3.status);
            Assert.Equal(("Attach ↑", "Detach ↓"), s3.labels);

            // Ctrl+Up attaches again
            var s4 = await Step(() => preview.OnListKeyPressed((uint)Gdk.Constants.KEY_Up, Gdk.ModifierType.ControlMask));
            AssertStill(s4, "Ctrl+Up key");
            Assert.True(s4.above && s4.below);

            // Drag down onto the neighbour below on an attached-below line: detach from below only
            var s5 = await Step(() => preview.HandleRowDrop(row, row + 1));
            AssertStill(s5, "drag onto the line below");
            Assert.True(s5.above);
            Assert.False(s5.below);
            Assert.Equal(("Detach ↑", "Attach ↓"), s5.labels);

            // Plain Backspace detaches completely
            var s6 = await Step(() => preview.OnListKeyPressed((uint)Gdk.Constants.KEY_BackSpace, Gdk.ModifierType.NoModifierMask));
            AssertStill(s6, "BackSpace key");
            Assert.False(s6.above || s6.below);
            Assert.Equal(("Attach ↑", "Attach ↓"), s6.labels);

            // Ctrl+Down attaches below, Ctrl+Backspace detaches again
            var s7 = await Step(() => preview.OnListKeyPressed((uint)Gdk.Constants.KEY_Down, Gdk.ModifierType.ControlMask));
            AssertStill(s7, "Ctrl+Down key");
            Assert.True(s7.below);
            var s8 = await Step(() => preview.OnListKeyPressed((uint)Gdk.Constants.KEY_BackSpace, Gdk.ModifierType.ControlMask));
            AssertStill(s8, "Ctrl+BackSpace key");
            Assert.False(s8.above || s8.below);

            // Undo keeps the list still too, and the selected line's labels follow the restored joins
            var s9 = await Step(() => preview.UndoForTests());
            AssertStill(s9, "Undo");
            Assert.True(s9.below);
            Assert.Equal(("Attach ↑", "Detach ↓"), s9.labels);

            Screenshot.TrySave(preview, "Preview-grouping-scroll");
            await scope.CloseAsync(preview);
            await scope.CloseAsync(win);
        }
    }
}
