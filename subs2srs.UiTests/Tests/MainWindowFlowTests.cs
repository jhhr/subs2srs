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
    /// Layer 3: drive the main window like a user would (fill entries, tick boxes,
    /// press Go) and check the generated deck.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class MainWindowFlowTests
    {
        private readonly GtkFixture _gtk;

        public MainWindowFlowTests(GtkFixture gtk) => _gtk = gtk;

        private static void FillBasicRun(MainWindow win, string outputDir, string deckName)
        {
            win._txtSubs1.SetText(TestMedia.SrtPath);
            win._txtSubs2.SetText("");
            win._txtVideo.SetText(TestMedia.VideoPath);
            win._txtOutputDir.SetText(outputDir);
            win._txtDeckName.SetText(deckName);
            win._chkGenerateAudio.SetActive(true);
            win._chkGenerateSnapshots.SetActive(true);
            win._chkGenerateVideo.SetActive(false);
            win._chkNormalize.SetActive(false);
            win._chkSpan.SetActive(false);
        }

        [RequiresFfmpegFact]
        public async Task Go_GeneratesDeckAndRestoresButtons()
        {
            await TestMedia.EnsureAsync();
            using var scope = new UiTestScope(_gtk);
            const string deck = "UiDeck";

            var win = await scope.OpenMainWindowAsync();

            await _gtk.RunOnGtkAsync(async () =>
            {
                FillBasicRun(win, scope.OutputDir, deck);
                await Pump.IdleAsync();
                await win.GoAsync();
                await Pump.IdleAsync();
            }, TimeSpan.FromMinutes(3));

            var ui = _gtk.RunOnGtk(() =>
            {
                Screenshot.TrySave(win, "MainWindow-after-go");
                return (Go: win._btnGo.GetSensitive(),
                        Cancel: win._btnCancel.GetSensitive(),
                        Text: win._progressBar.GetText() ?? "",
                        Fraction: win._progressBar.GetFraction());
            });

            Assert.Empty(scope.Msgs.Errors);
            Assert.Single(scope.Msgs.Infos);
            Assert.True(ui.Go, "Go should be re-enabled");
            Assert.False(ui.Cancel, "Cancel should be disabled");
            Assert.Equal("Finished!", ui.Text);
            Assert.Equal(1.0, ui.Fraction, 3);

            string tsv = Path.Combine(scope.OutputDir, deck + ".tsv");
            Assert.True(File.Exists(tsv));
            var lines = File.ReadAllLines(tsv, Encoding.UTF8).Where(l => l.Length > 0).ToArray();
            Assert.Equal(TestMedia.Lines.Length, lines.Length);

            string media = Path.Combine(scope.OutputDir, deck + ".media");
            Assert.Equal(4, Directory.GetFiles(media, "*.jpg").Length);
            Assert.Equal(4, Directory.GetFiles(media).Count(f => !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));

            await scope.CloseAsync(win);
        }

        [Fact]
        public async Task Go_WithEmptyDeckName_ShowsOneErrorAndWritesNothing()
        {
            using var scope = new UiTestScope(_gtk);
            scope.ExpectErrors();
            if (!FfmpegProbe.IsAvailable)
                return; // Go is disabled without ffmpeg; nothing to validate

            var win = await scope.OpenMainWindowAsync();
            scope.Msgs.Clear();

            await _gtk.RunOnGtkAsync(async () =>
            {
                win._txtSubs1.SetText(Path.Combine(scope.TempDir, "nonexistent.srt"));
                win._txtOutputDir.SetText(scope.OutputDir);
                win._txtDeckName.SetText("");
                await win.GoAsync();
            });

            var errors = scope.Msgs.Errors;
            Assert.Single(errors);
            Assert.Contains("Deck Name", errors[0]);
            Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));

            await scope.CloseAsync(win);
        }
    }
}
