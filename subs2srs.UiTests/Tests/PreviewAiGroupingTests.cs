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
    /// The Preview in AI mode with a fake provider: the pass runs when the preview opens, the
    /// model's note is the Group column's tooltip, Regroup (AI) asks again, the validation file
    /// records the proposal's producer/model/prompt version, and a provider failure shows its
    /// text and falls back to the rules.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class PreviewAiGroupingTests
    {
        private readonly GtkFixture _gtk;

        public PreviewAiGroupingTests(GtkFixture gtk) => _gtk = gtk;

        private async Task<(MainWindow win, DialogPreview preview)> OpenAiPreviewAsync(UiTestScope scope, string srt, string deck)
        {
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
                Settings.Instance.Snippets.Mode = SnippetMode.AI;
                Settings.Instance.Snippets.AiModel = "fake-model";
                Settings.Instance.Snippets.GapKeepMs = 200;

                preview = win.ShowPreview();
                await Pump.SettleAsync(preview);
                await Pump.WaitUntilAsync(() => !preview.IsRunning && preview.ProgressText == "Preview ready",
                    TimeSpan.FromSeconds(60), "preview ready");
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(2));
            return (win, preview);
        }

        [RequiresFfmpegFact]
        public async Task Preview_RunsTheAiPass_ShowsNotes_RegroupsAndExports()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            string srt = TestMedia.WriteDialogueSrt(scope.TempDir);
            const string deck = "AiDeck";
            var fake = new FakeChatProvider(answer: FakeChatProvider.JoinPairs("why together"));
            using var installed = fake.Install();

            var (win, preview) = await OpenAiPreviewAsync(scope, srt, deck);

            // The pass ran on open: pairs 0-1 and 2-3, notes on the rows of each snippet
            Assert.Single(fake.Requests);
            var opened = _gtk.RunOnGtk(() => (preview.CurrentEditor.Joins, preview.GroupNoteAt(0), preview.GroupNoteAt(1),
                preview.GroupStatusText, preview.PreviewVars.ProposalProducer, preview.CancelButtonSensitive));
            Assert.Equal(new[] { true, false, true, false }, opened.Joins);
            Assert.Equal("why together", opened.Item2);
            Assert.Equal("why together", opened.Item3);
            Assert.StartsWith("AI grouping (fake-model): 1 request(s)", opened.Item4);
            Assert.Equal("ai", opened.Item5);
            Assert.False(opened.Item6);

            // Regroup (AI) ignores the cache and asks again; the new answer replaces the grouping
            fake.Answer = FakeChatProvider.JoinAll("one scene");
            await _gtk.RunOnGtkAsync(async () =>
            {
                preview.ClickRegroupAi();
                await Pump.WaitUntilAsync(() => !preview.IsRunning && preview.ProgressText == "Preview ready",
                    TimeSpan.FromSeconds(60), "regroup done");
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(1));
            Assert.Equal(2, fake.Requests.Count);
            var regrouped = _gtk.RunOnGtk(() =>
            {
                Screenshot.TrySave(preview, "Preview-ai-grouping");
                return (preview.CurrentEditor.Joins, preview.GroupNoteAt(3), preview.PreviewVars.Joins[0]);
            });
            Assert.Equal(new[] { true, true, true, false }, regrouped.Joins);
            Assert.Equal("one scene", regrouped.Item2);
            Assert.Equal(new[] { true, true, true, false }, regrouped.Item3);

            // Validation export records the AI proposal
            int written = _gtk.RunOnGtk(() => preview.SaveValidationFiles());
            Assert.Equal(1, written);
            var validation = GroupingValidationFile.Read(Path.Combine(scope.OutputDir, "validation", deck + "_1.grouping.json"));
            Assert.Equal("ai", validation.Proposal!.Producer);
            Assert.Equal("fake-model", validation.Proposal.Model);
            Assert.Equal(AiGroupingPrompt.PromptVersion, validation.Proposal.PromptVersion);
            Assert.Equal(new[] { true, true, true }, validation.Proposal.Joins);

            // The answer went to the scope's cache, never the real one
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(scope.TempDir, "ai-cache"), "*.json"));

            Assert.Empty(scope.Msgs.Errors);
            await scope.CloseAsync(preview);
            await scope.CloseAsync(win);
        }

        [RequiresFfmpegFact]
        public async Task Preview_ProviderFailure_ShowsTheErrorAndUsesTheRules()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            string srt = TestMedia.WriteDialogueSrt(scope.TempDir);
            var fake = new FakeChatProvider { Failure = new ProviderException("fake", 401, "invalid x-api-key") };
            using var installed = fake.Install();

            var (win, preview) = await OpenAiPreviewAsync(scope, srt, "AiFail");

            Assert.Single(fake.Requests);
            var state = _gtk.RunOnGtk(() => (preview.CurrentEditor.Joins, preview.GroupStatusText, preview.GroupNoteAt(0)));
            Assert.Equal(new[] { true, false, false, false }, state.Joins); // the rules on this clip
            Assert.Contains("invalid x-api-key", state.Item2);
            Assert.Contains("grouped with the rules instead", state.Item2);
            Assert.Equal("", state.Item3);

            Assert.Empty(scope.Msgs.Errors);
            await scope.CloseAsync(preview);
            await scope.CloseAsync(win);
        }
    }
}
