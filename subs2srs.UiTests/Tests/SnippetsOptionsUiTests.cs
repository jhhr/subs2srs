//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Tasks;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// Advanced Subtitle Options → Snippets: the AI mode and its three fields load from and
    /// save to the project settings.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class SnippetsOptionsUiTests
    {
        private readonly GtkFixture _gtk;

        public SnippetsOptionsUiTests(GtkFixture gtk) => _gtk = gtk;

        [Fact]
        public async Task SnippetsPage_RoundTripsTheAiSettings()
        {
            using var scope = new UiTestScope(_gtk);
            Settings.Instance.Snippets.Mode = SnippetMode.AI;
            Settings.Instance.Snippets.AiModel = "gemini-2.5-flash";
            Settings.Instance.Snippets.ChunkTargetLines = 150;
            Settings.Instance.Snippets.AiExtraInstructions = "a workplace comedy";

            var dlg = await scope.OpenAsync(() => new DialogAdvancedSubtitleOptions(null!));

            var loaded = await _gtk.RunOnGtkAsync(async () =>
            {
                dlg._notebook.SetCurrentPage(5); // Snippets
                await Pump.FramesAsync(dlg, 2);
                Screenshot.TrySave(dlg, "DialogAdvancedSubtitleOptions-snippets-ai");
                return (dlg._dropSnippetMode.GetSelected(), dlg._txtAiModel.GetText(), dlg._spinAiChunk.GetValue(), dlg._txtAiInstructions.GetText());
            });
            Assert.Equal(2u, loaded.Item1);
            Assert.Equal("gemini-2.5-flash", loaded.Item2);
            Assert.Equal(150, loaded.Item3);
            Assert.Equal("a workplace comedy", loaded.Item4);

            _gtk.RunOnGtk(() =>
            {
                dlg._dropSnippetMode.SetSelected(2);
                dlg._txtAiModel.SetText(" claude-opus-5 ");
                dlg._spinAiChunk.SetValue(0);
                dlg._txtAiInstructions.SetText("keep jokes with their setup");
                dlg.SaveToSettings();
            });
            Assert.Equal(SnippetMode.AI, Settings.Instance.Snippets.Mode);
            Assert.Equal("claude-opus-5", Settings.Instance.Snippets.AiModel);
            Assert.Equal(0, Settings.Instance.Snippets.ChunkTargetLines);
            Assert.Equal("keep jokes with their setup", Settings.Instance.Snippets.AiExtraInstructions);

            await scope.CloseAsync(dlg);
        }

        [Fact]
        public async Task ModelHint_SaysWhatATerminalModelNeeds()
        {
            using var scope = new UiTestScope(_gtk);
            Settings.Instance.Snippets.Mode = SnippetMode.AI;
            Settings.Instance.Snippets.AiModel = "claude-sonnet-5";
            // A path that does not exist, so the dialog reports the CLI as missing either way.
            ConstantSettings.ClaudeCliPath = "";

            var dlg = await scope.OpenAsync(() => new DialogAdvancedSubtitleOptions(null!));

            var hints = await _gtk.RunOnGtkAsync(async () =>
            {
                dlg._notebook.SetCurrentPage(5); // Snippets
                await Pump.FramesAsync(dlg, 2);
                string api = dlg._lblAiModelHint.GetText();

                dlg._txtAiModel.SetText("terminal-claude-sonnet-5");
                await Pump.FramesAsync(dlg, 2);
                Screenshot.TrySave(dlg, "DialogAdvancedSubtitleOptions-snippets-terminal-model");
                string terminal = dlg._lblAiModelHint.GetText();

                dlg._txtAiModel.SetText("gpt-5");
                await Pump.FramesAsync(dlg, 2);
                return (api, terminal, back: dlg._lblAiModelHint.GetText());
            });

            Assert.Contains("terminal-claude-", hints.api);
            // Either "runs on your Claude subscription…" or the hint to install Claude Code,
            // depending on whether this machine has the CLI; both name the CLI.
            Assert.Contains("claude", hints.terminal);
            Assert.DoesNotContain("gpt-", hints.terminal);
            Assert.Equal(hints.api, hints.back);

            Assert.Contains("terminal-claude-", await _gtk.RunOnGtkAsync(() => Task.FromResult(dlg._txtAiModel.GetTooltipText() ?? "")));

            await scope.CloseAsync(dlg);
        }
    }
}
