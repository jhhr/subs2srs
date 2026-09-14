using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// The animated snapshot controls on the Snapshots tab: they follow the
    /// encoder probe, round-trip into Settings, and Go produces the files.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class AnimatedSnapshotUiTests
    {
        private readonly GtkFixture _gtk;

        public AnimatedSnapshotUiTests(GtkFixture gtk) => _gtk = gtk;

        private static void FillBasicRun(MainWindow win, string outputDir, string deckName)
        {
            win._txtSubs1.SetText(TestMedia.SrtPath);
            win._txtSubs2.SetText("");
            win._txtVideo.SetText(TestMedia.VideoPath);
            win._txtOutputDir.SetText(outputDir);
            win._txtDeckName.SetText(deckName);
            win._chkGenerateAudio.SetActive(false);
            win._chkGenerateSnapshots.SetActive(true);
            win._chkGenerateVideo.SetActive(false);
            win._chkNormalize.SetActive(false);
            win._chkSpan.SetActive(false);
        }

        [RequiresFfmpegEncoderFact(AnimatedSnapshotFormat.Webp)]
        public async Task Checkbox_EnablesOptions_AndGoWritesAnimatedWebp()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            const string deck = "AnimDeck";

            var win = await scope.OpenMainWindowAsync();

            var before = _gtk.RunOnGtk(() => new
            {
                Sensitive = win._chkGenerateAnimatedSnapshots.GetSensitive(),
                Active = win._chkGenerateAnimatedSnapshots.GetActive(),
                OptionsSensitive = win._spinAnimatedFps.IsSensitive(),
                HintVisible = win._lblAnimatedHint.GetVisible(),
            });
            Assert.True(before.Sensitive, "checkbox should be enabled when libwebp is available");
            Assert.False(before.Active, "animated snapshots default to off");
            Assert.False(before.OptionsSensitive, "options are greyed out while off");
            Assert.False(before.HintVisible);

            await _gtk.RunOnGtkAsync(async () =>
            {
                FillBasicRun(win, scope.OutputDir, deck);
                win._notebook.SetCurrentPage(2); // Snapshots tab
                win._chkGenerateAnimatedSnapshots.SetActive(true);
                win._spinAnimatedHeight.Value = 120;
                win._spinAnimatedFps.Value = 5;
                await Pump.IdleAsync();
                Assert.True(win._spinAnimatedFps.IsSensitive(), "options follow the checkbox");
                await Pump.SettleAsync(win);
                Screenshot.TrySave(win, "MainWindow-snapshots-tab-animated");
                await win.GoAsync();
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(3));

            Assert.Empty(scope.Msgs.Errors);
            Assert.True(Settings.Instance.AnimatedSnapshots.Enabled);
            Assert.Equal(120, Settings.Instance.AnimatedSnapshots.Height);
            Assert.Equal(5, Settings.Instance.AnimatedSnapshots.Fps);
            Assert.Equal(AnimatedSnapshotFormat.Webp, Settings.Instance.AnimatedSnapshots.Format);

            string media = Path.Combine(scope.OutputDir, deck + ".media");
            Assert.Equal(TestMedia.Lines.Length, Directory.GetFiles(media, "*.webp").Length);
            Assert.Equal(TestMedia.Lines.Length, Directory.GetFiles(media, "*.jpg").Length);

            string tsv = Path.Combine(scope.OutputDir, deck + ".tsv");
            var lines = File.ReadAllLines(tsv, Encoding.UTF8).Where(l => l.Length > 0).ToArray();
            Assert.All(lines, l => Assert.Contains(".webp\">", l));

            await scope.CloseAsync(win);
        }

        [Fact]
        public async Task MissingEncoder_DisablesTheCheckbox_WithAHint()
        {
            using var scope = new UiTestScope(_gtk);
            if (!FfmpegProbe.IsAvailable)
                scope.ExpectErrors(); // startup check reports missing ffmpeg

            try
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(new[] { "libx264" });
                var win = await scope.OpenMainWindowAsync();

                var state = _gtk.RunOnGtk(() =>
                {
                    // CheckExternalTools re-probes; pin the fake result again for the assertions.
                    UtilsAnimatedSnapshot.OverrideAvailableEncoders(new[] { "libx264" });
                    win.UpdateAnimatedSnapshotAvailability();
                    return new
                    {
                        Sensitive = win._chkGenerateAnimatedSnapshots.GetSensitive(),
                        Active = win._chkGenerateAnimatedSnapshots.GetActive(),
                        HintVisible = win._lblAnimatedHint.GetVisible(),
                        Hint = win._lblAnimatedHint.GetText() ?? "",
                        FormatSensitive = win._comboAnimatedFormat.GetSensitive(),
                    };
                });

                Assert.False(state.Sensitive);
                Assert.False(state.Active);
                Assert.True(state.HintVisible);
                Assert.Equal(FfmpegProbe.IsAvailable, state.FormatSensitive);
                if (FfmpegProbe.IsAvailable)
                    Assert.Contains("libwebp", state.Hint);

                await scope.CloseAsync(win);
            }
            finally
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(null);
            }
        }
    }
}
