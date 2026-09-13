//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// The whole pipeline with rule-based grouping and dead-space removal on the
    /// generated test media. Needs ffmpeg/ffprobe.
    /// </summary>
    public class SnippetE2ETests
    {
        private const string Deck = "SnipDeck";

        private static string Configure(TestScope scope, SnippetMode mode, bool gapRemoval, bool video = false)
        {
            string srt = TestMedia.WriteDialogueSrt(scope.TempDir);
            var s = Settings.Instance;
            s.Subs[0].FilePattern = srt;
            s.Subs[0].Files = UtilsSubs.getSubsFiles(srt).ToArray();
            s.Subs[0].Encoding = "utf-8";
            s.Subs[1].FilePattern = "";
            s.Subs[1].Files = Array.Empty<string>();

            s.VideoClips.FilePattern = TestMedia.VideoPath;
            s.VideoClips.Files = UtilsCommon.getNonHiddenFiles(TestMedia.VideoPath);
            s.VideoClips.Enabled = video;
            s.VideoClips.Size = new ImageSize(160, 120);
            s.VideoClips.AudioStream = new InfoStream("0:a:0", "0", "", "Default"); // MainWindow fills this in the app

            s.AudioClips.Enabled = true;
            s.AudioClips.UseAudioFromVideo = true;
            s.AudioClips.AudioFormat = "MP3";
            s.AudioClips.Bitrate = 64;
            s.AudioClips.Normalize = false;
            s.AudioClips.PadEnabled = false;

            s.Snapshots.Enabled = true;
            s.SpanEnabled = false;
            s.TimeShiftEnabled = false;
            s.OutputDir = scope.OutputDir;
            s.DeckName = Deck;
            s.EpisodeStartNumber = 1;

            s.Snippets.Mode = mode;
            s.Snippets.GapRemovalEnabled = gapRemoval;
            s.Snippets.GapKeepMs = 200;
            s.Snippets.Separator = "<br>";

            ConstantSettings.UpdateAudioFilenameFormats();
            return srt;
        }

        private static string[] TsvLines(TestScope scope) =>
            File.ReadAllLines(Path.Combine(scope.OutputDir, Deck + ".tsv"), Encoding.UTF8).Where(l => l.Length > 0).ToArray();

        private static string MediaDir(TestScope scope) => Path.Combine(scope.OutputDir, Deck + ".media");

        /// <summary>Duration in seconds as reported by ffprobe.</summary>
        internal static double ProbeDuration(string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ConstantSettings.ResolveToolOrName("ffprobe"),
                Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return double.Parse(output, CultureInfo.InvariantCulture);
        }

        private static string SoundFile(string tsvLine)
        {
            int a = tsvLine.IndexOf("[sound:", StringComparison.Ordinal) + 7;
            int b = tsvLine.IndexOf(']', a);
            return tsvLine.Substring(a, b - a);
        }

        [RequiresFfmpegFact]
        public async Task RulesGrouping_MergesQuestionAndAnswer_AndRemovesTheGap()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Rules, gapRemoval: true);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            Assert.Equal(3, lines.Length); // 4 lines -> 3 cards
            Assert.Contains("Where are you going?<br>To the station.", lines[0]);
            Assert.Contains("See you later.", lines[1]);
            Assert.Contains("Bye.", lines[2]);
            Assert.DoesNotContain("<br>", lines[1]);

            string media = MediaDir(scope);
            string snippetAudio = Path.Combine(media, SoundFile(lines[0]));
            Assert.True(File.Exists(snippetAudio));
            Assert.Matches(@"\b01\.000-.*03\.400\.", Path.GetFileName(snippetAudio)); // whole span in the name

            // 1.0 s + 0.2 s kept gap + 1.0 s = 2.2 s (was 2.4 s of media)
            double duration = ProbeDuration(snippetAudio);
            Assert.InRange(duration, 2.05, 2.35);

            double single = ProbeDuration(Path.Combine(media, SoundFile(lines[1])));
            Assert.InRange(single, 0.9, 1.15);

            Assert.Equal(3, Directory.GetFiles(media, "*.jpg").Length);
        }

        [RequiresFfmpegFact]
        public async Task RulesGrouping_WithoutGapRemoval_KeepsTheWholeSpan()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Rules, gapRemoval: false);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            Assert.Equal(3, lines.Length);
            double duration = ProbeDuration(Path.Combine(MediaDir(scope), SoundFile(lines[0])));
            Assert.InRange(duration, 2.3, 2.55);
        }

        [RequiresFfmpegFact]
        public async Task ModeOff_IsUnchangedBehaviour()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Off, gapRemoval: true);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            Assert.Equal(4, TsvLines(scope).Length);
        }

        [RequiresFfmpegFact]
        public async Task PreviewJoins_OverrideTheRules_OnGo()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Off, gapRemoval: true);

            // What the preview does: parse + filter, then hand the lines and a manual grouping to Go.
            var wv = new WorkerVars(null, Path.Combine(scope.TempDir, "preview"), WorkerVars.SubsProcessingType.Preview);
            Directory.CreateDirectory(wv.MediaDir);
            var worker = new WorkerSubs();
            wv.CombinedAll = worker.combineAllSubs(wv, new NullProgressReporter());
            wv.CombinedAll = worker.inactivateLines(wv, new NullProgressReporter());
            Assert.Equal(4, wv.CombinedAll[0].Count);

            bool[] joins = { false, false, true, false }; // join "See you later." + "Bye."
            await new SubsProcessor().StartAsync(new NullProgressReporter(), wv.CombinedAll, new() { joins });

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            Assert.Equal(3, lines.Length);
            Assert.Contains("See you later.<br>Bye.", lines[2]);
            // gap 0.2 s is not longer than GapKeepMs, so nothing is cut: 1.0 + 0.2 + 1.0
            double duration = ProbeDuration(Path.Combine(MediaDir(scope), SoundFile(lines[2])));
            Assert.InRange(duration, 2.05, 2.35);
        }

        [RequiresFfmpegFact]
        public async Task VideoClips_OfASnippet_AreConcatenatedFromStreamCopiedParts()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Rules, gapRemoval: true, video: true);
            Settings.Instance.Snapshots.Enabled = false;

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            Assert.Equal(3, lines.Length);

            string media = MediaDir(scope);
            var all = Directory.GetFiles(media).Select(Path.GetFileName).ToArray();
            var avis = all.Where(f => f!.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(avis.Length == 3, "media dir: " + string.Join(", ", all));
            var leftovers = all.Where(f => f!.Contains(".part") || f.EndsWith(".concat.txt") || f.Contains(".tmp")).ToArray();
            Assert.True(leftovers.Length == 0, "leftover temp files: " + string.Join(", ", leftovers));

            string snippetVideo = Path.Combine(media, avis.Single(f => f!.Contains("01.000-") && f.Contains("03.400")));
            double duration = ProbeDuration(snippetVideo);
            // stream-copy cuts land on keyframes (every 6 frames at 25 fps), so allow some slack
            Assert.InRange(duration, 1.8, 2.8);
        }
    }
}
