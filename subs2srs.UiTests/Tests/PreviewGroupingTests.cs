//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// Drive the Preview's grouping editor: the rules propose a grouping, the
    /// three actions (buttons, keys, drag) edit it, and the validation export writes it.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class PreviewGroupingTests
    {
        private readonly GtkFixture _gtk;

        public PreviewGroupingTests(GtkFixture gtk) => _gtk = gtk;

        [RequiresFfmpegFact]
        public async Task Preview_ProposesEditsAndExportsGrouping()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            string srt = TestMedia.WriteDialogueSrt(scope.TempDir);
            const string deck = "PrevDeck";

            var win = await scope.OpenMainWindowAsync();
            DialogPreview preview = null!;

            await _gtk.RunOnGtkAsync(async () =>
            {
                win._txtSubs1.SetText(srt);
                win._txtSubs2.SetText("");
                win._txtVideo.SetText(TestMedia.VideoPath);
                win._txtOutputDir.SetText(scope.OutputDir);
                win._txtDeckName.SetText(deck);
                win._chkGenerateAudio.SetActive(true);
                win._chkGenerateSnapshots.SetActive(false);
                win._chkGenerateVideo.SetActive(false);
                Settings.Instance.Snippets.Mode = SnippetMode.Rules;
                Settings.Instance.Snippets.GapKeepMs = 200;

                preview = win.ShowPreview();
                await Pump.SettleAsync(preview);
                await Pump.WaitUntilAsync(() => !preview.IsRunning && preview.ProgressText == "Preview ready",
                    TimeSpan.FromSeconds(60), "preview ready");
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(2));

            // Rules: "Where are you going?" + "To the station." joined, the rest single
            var proposed = _gtk.RunOnGtk(() => preview.CurrentEditor.Joins);
            Assert.Equal(new[] { true, false, false, false }, proposed);

            // Buttons / keys / drag all go through the same editor
            var afterEdits = _gtk.RunOnGtk(() =>
            {
                preview.SelectRow(3);
                var r1 = preview.ApplyToSelected(JoinAction.AttachAbove);
                bool joined = preview.CurrentEditor.IsJoinedAbove(3);

                var r2 = preview.ApplyToSelected(JoinAction.Detach);
                bool detached = !preview.CurrentEditor.IsJoinedAbove(3);

                // "w" is the second default binding for Attach Above
                bool handled = preview.OnListKeyPressed((uint)Gdk.Constants.KEY_w, Gdk.ModifierType.NoModifierMask);
                bool joinedByKey = preview.CurrentEditor.IsJoinedAbove(3);

                bool dropped = preview.HandleRowDrop(3, 3); // onto itself = detach
                bool detachedByDrop = !preview.CurrentEditor.IsJoinedAbove(3);

                // Not a neighbour: ignored
                bool farDrop = preview.HandleRowDrop(0, 3);

                Screenshot.TrySave(preview, "Preview-grouping");
                return (r1.Ok, joined, r2.Ok, detached, handled, joinedByKey, dropped, detachedByDrop, farDrop,
                        preview.CurrentEditor.Joins);
            });

            Assert.True(afterEdits.Item1, "attach above should succeed");
            Assert.True(afterEdits.joined);
            Assert.True(afterEdits.Item3, "detach should succeed");
            Assert.True(afterEdits.detached);
            Assert.True(afterEdits.handled, "key binding should be handled");
            Assert.True(afterEdits.joinedByKey);
            Assert.True(afterEdits.dropped);
            Assert.True(afterEdits.detachedByDrop);
            Assert.False(afterEdits.farDrop);
            Assert.Equal(new[] { true, false, false, false }, afterEdits.Item10);

            // The edited vectors are what Go would use
            var joins = _gtk.RunOnGtk(() => preview.PreviewVars.Joins[0]);
            Assert.Equal(new[] { true, false, false, false }, joins);

            // Validation export
            int written = _gtk.RunOnGtk(() => preview.SaveValidationFiles());
            Assert.Equal(1, written);
            string file = Path.Combine(scope.OutputDir, "validation", deck + "_1.grouping.json");
            Assert.True(File.Exists(file), "validation file not written");
            var validation = GroupingValidationFile.Read(file);
            Assert.Equal(4, validation.Lines.Count);
            Assert.Equal(new[] { true, false, false }, validation.Joins);
            Assert.Equal("rules", validation.Proposal!.Producer);

            await scope.CloseAsync(preview);
            await scope.CloseAsync(win);
        }
    }
}
