using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Two episodes made from the same subtitles and video, so only the episode number tells
        /// their cards and media apart.
        /// </summary>
        private static void ConfigureTwoEpisodes(TestScope scope, int[]? episodeNumbers, int startNumber)
        {
            ConfigureBasicRun(scope, TestMedia.SrtPath, "utf-8", "Opus");
            var s = Settings.Instance;
            s.Subs[0].Files = new[] { TestMedia.SrtPath, TestMedia.SrtPath };
            s.VideoClips.Files = new[] { TestMedia.VideoPath, TestMedia.VideoPath };
            s.EpisodeStartNumber = startNumber;
            s.EpisodeNumbers = episodeNumbers;
        }

        [RequiresFfmpegTheory]
        [InlineData(new[] { 1, 3 }, 7, 1, 3)] // explicit numbers; the start number plays no part
        [InlineData(null, 5, 5, 6)]           // none: counted from the start number, as before
        public async Task EpisodeNumbers_InTagsSequenceMarkersAndEveryMediaName(
            int[]? episodeNumbers, int startNumber, int first, int second)
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            ConfigureTwoEpisodes(scope, episodeNumbers, startNumber);
            var s = Settings.Instance;
            s.VideoClips.Enabled = true;
            s.VideoClips.Size = new ImageSize(160, 120);
            s.VideoClips.AudioStream = new InfoStream("0:a:0", "0", "", "Default"); // MainWindow fills this in the app
            // Animated snapshots too when this ffmpeg can make them (their own tests skip otherwise)
            bool animated = UtilsAnimatedSnapshot.EncoderFor(AnimatedSnapshotFormat.Webp) != null;
            if (animated)
            {
                s.AnimatedSnapshots.Enabled = true;
                s.AnimatedSnapshots.Format = AnimatedSnapshotFormat.Webp;
                s.AnimatedSnapshots.Fps = 5;
                s.AnimatedSnapshots.Height = 60;
                s.AnimatedSnapshots.Quality = 20;
            }
            int mediaPerCard = animated ? 4 : 3; // audio, snapshot, [animated,] video clip

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = ReadTsvLines(TsvPath(scope.OutputDir));
            Assert.Equal(2 * TestMedia.Lines.Length, lines.Length);

            string media = MediaDir(scope.OutputDir);
            var mediaRef = new Regex("\\[sound:([^\\]]+)\\]|<img src=\"([^\"]+)\">");
            for (int i = 0; i < lines.Length; i++)
            {
                int episode = i < TestMedia.Lines.Length ? first : second;
                string[] cols = lines[i].Split('\t');
                Assert.Equal($"{DeckName}_{episode}", cols[0]); // tag
                Assert.StartsWith($"{episode}_", cols[1]);       // sequence marker

                var names = mediaRef.Matches(lines[i])
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .ToArray();
                Assert.Equal(mediaPerCard, names.Length);
                foreach (string name in names)
                {
                    Assert.StartsWith($"{DeckName}_{episode}_", name);
                    Assert.True(File.Exists(Path.Combine(media, name)), $"missing media file {name}");
                }
            }

            // Every file the workers wrote is one the TSV names: none under another number.
            var files = Directory.GetFiles(media).Select(f => Path.GetFileName(f)).ToArray();
            Assert.Equal(lines.Length * mediaPerCard, files.Length);
            Assert.All(files, f => Assert.Matches($"^{DeckName}_({first}|{second})_", f));
        }

        [RequiresFfmpegFact]
        public async Task TimeShiftRule_IsChosenByTheExplicitEpisodeNumber()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            // Counted from the start number the second episode would be 2, which the rule misses.
            ConfigureTwoEpisodes(scope, new[] { 1, 3 }, 1);
            var s = Settings.Instance;
            s.AudioClips.Enabled = false;
            s.Snapshots.Enabled = false;
            s.TimeShiftEnabled = true;
            s.Subs[0].TimeShift = 0;
            s.Subs[0].TimeShiftRules = new List<TimeShiftRule> { new TimeShiftRule(3, 500) };

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = ReadTsvLines(TsvPath(scope.OutputDir));
            Assert.Equal(2 * TestMedia.Lines.Length, lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                // The sequence marker ends with the line's start time, sec.msec: lines start at 1, 3, 5, 7 s.
                int line = i % TestMedia.Lines.Length;
                string start = i < TestMedia.Lines.Length ? $"{1 + 2 * line:00}.000" : $"{1 + 2 * line:00}.500";
                string marker = lines[i].Split('\t')[1];
                Assert.EndsWith("." + start, marker);
                Assert.Contains(TestMedia.Lines[line], lines[i]);
            }
        }
    }
}
