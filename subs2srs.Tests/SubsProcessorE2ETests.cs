using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// End-to-end card generation through SubsProcessor without GTK.
    /// Needs ffmpeg; generates synthetic media once per run.
    /// </summary>
    public class SubsProcessorE2ETests
    {
        private const string DeckName = "E2EDeck";

        /// <summary>Configure Settings.Instance the way MainWindow.SaveSettings would for a basic run.</summary>
        private static void ConfigureBasicRun(TestScope scope, string srtPath, string encoding,
            string audioFormat, string? outputDir = null, string deckName = DeckName)
        {
            var s = Settings.Instance;
            s.Subs[0].FilePattern = srtPath;
            s.Subs[0].Files = UtilsSubs.getSubsFiles(srtPath).ToArray();
            s.Subs[0].Encoding = encoding;
            s.Subs[1].FilePattern = "";
            s.Subs[1].Files = Array.Empty<string>();

            s.VideoClips.FilePattern = TestMedia.VideoPath;
            s.VideoClips.Files = UtilsCommon.getNonHiddenFiles(TestMedia.VideoPath);
            s.VideoClips.Enabled = false;

            s.AudioClips.Enabled = true;
            s.AudioClips.UseAudioFromVideo = true;
            s.AudioClips.AudioFormat = audioFormat;
            s.AudioClips.Bitrate = 64;
            s.AudioClips.Normalize = false;

            s.Snapshots.Enabled = true;

            s.SpanEnabled = false;
            s.TimeShiftEnabled = false;
            s.OutputDir = outputDir ?? scope.OutputDir;
            s.DeckName = deckName;
            s.EpisodeStartNumber = 1;

            ConstantSettings.UpdateAudioFilenameFormats();
        }

        private static string TsvPath(string outputDir, string deck = DeckName) => Path.Combine(outputDir, deck + ".tsv");
        private static string MediaDir(string outputDir, string deck = DeckName) => Path.Combine(outputDir, deck + ".media");

        private static string[] ReadTsvLines(string tsv)
        {
            byte[] bytes = File.ReadAllBytes(tsv);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "TSV should start with a UTF-8 BOM");
            return File.ReadAllLines(tsv, Encoding.UTF8)
                .Where(l => l.Length > 0)
                .ToArray();
        }

        private static void AssertMediaReferencesExist(string[] lines, string mediaDir)
        {
            var sound = new Regex(@"\[sound:([^\]]+)\]");
            var img = new Regex("<img src=\"([^\"]+)\">");
            int soundRefs = 0, imgRefs = 0;
            foreach (var line in lines)
            {
                foreach (Match m in sound.Matches(line))
                {
                    soundRefs++;
                    Assert.True(File.Exists(Path.Combine(mediaDir, m.Groups[1].Value)),
                        $"missing audio file {m.Groups[1].Value}");
                }
                foreach (Match m in img.Matches(line))
                {
                    imgRefs++;
                    Assert.True(File.Exists(Path.Combine(mediaDir, m.Groups[1].Value)),
                        $"missing snapshot file {m.Groups[1].Value}");
                }
            }
            Assert.Equal(lines.Length, soundRefs);
            Assert.Equal(lines.Length, imgRefs);
        }

        [RequiresFfmpegTheory]
        [InlineData("Opus", "opus")]
        [InlineData("MP3", "mp3")]
        public async Task BasicRun_ProducesTsvAudioAndSnapshots(string audioFormat, string audioExt)
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            ConfigureBasicRun(scope, TestMedia.SrtPath, "utf-8", audioFormat);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            Assert.Single(scope.Msgs.Infos);

            string tsv = TsvPath(scope.OutputDir);
            Assert.True(File.Exists(tsv), "deck TSV not written");
            var lines = ReadTsvLines(tsv);
            Assert.Equal(TestMedia.Lines.Length, lines.Length);

            int tabs = lines[0].Count(c => c == '\t');
            Assert.True(tabs > 0);
            Assert.All(lines, l => Assert.Equal(tabs, l.Count(c => c == '\t')));
            for (int i = 0; i < lines.Length; i++)
                Assert.Contains(TestMedia.Lines[i], lines[i]);

            string media = MediaDir(scope.OutputDir);
            var audio = Directory.GetFiles(media, "*." + audioExt);
            var jpgs = Directory.GetFiles(media, "*.jpg");
            Assert.Equal(4, audio.Length);
            Assert.Equal(4, jpgs.Length);
            Assert.All(jpgs, f => Assert.True(new FileInfo(f).Length > 1024, $"snapshot too small: {f}"));
            Assert.All(audio, f => Assert.True(new FileInfo(f).Length > 500, $"audio too small: {f}"));

            AssertMediaReferencesExist(lines, media);
        }

        [RequiresFfmpegFact]
        public async Task ShiftJisSubtitles_AreDecoded()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            ConfigureBasicRun(scope, TestMedia.SrtShiftJisPath, "shift_jis", "Opus");

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = ReadTsvLines(TsvPath(scope.OutputDir));
            Assert.Equal(TestMedia.LinesJapanese.Length, lines.Length);
            for (int i = 0; i < lines.Length; i++)
                Assert.Contains(TestMedia.LinesJapanese[i], lines[i]);
        }

        [RequiresFfmpegFact]
        public async Task CancelledRun_ReportsCancelledAndWritesNoTsv()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            ConfigureBasicRun(scope, TestMedia.SrtPath, "utf-8", "Opus");

            await new SubsProcessor().StartAsync(new CancellingProgressReporter(0));

            Assert.Contains(scope.Msgs.Errors, e => e.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(scope.Msgs.Infos);
            Assert.False(File.Exists(TsvPath(scope.OutputDir)), "TSV must not be written on cancel");
        }

        [RequiresFfmpegFact]
        public async Task OutputDirWithSpaceAndNonAscii_Works()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            string outDir = Path.Combine(scope.TempDir, "out dir ünïcode 日本");
            Directory.CreateDirectory(outDir);
            ConfigureBasicRun(scope, TestMedia.SrtPath, "utf-8", "Opus", outDir);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = ReadTsvLines(TsvPath(outDir));
            Assert.Equal(4, lines.Length);
            AssertMediaReferencesExist(lines, MediaDir(outDir));
        }
    }
}
