//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
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
        internal const string Deck = "SnipDeck";

        internal static string Configure(TestScope scope, SnippetMode mode, bool gapRemoval, bool video = false)
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

        internal static string[] TsvLines(TestScope scope) =>
            File.ReadAllLines(Path.Combine(scope.OutputDir, Deck + ".tsv"), Encoding.UTF8).Where(l => l.Length > 0).ToArray();

        internal static string MediaDir(TestScope scope) => Path.Combine(scope.OutputDir, Deck + ".media");

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

        // ── omitted line inside a snippet: B appears nowhere ────────────────

        /// <summary>
        /// Parse + filter like the preview, omit "To the station." (line 1) and
        /// join "Where are you going?" to the next kept line "See you later.",
        /// so the omitted line B (2.4-3.4 s) lies inside the snippet A + C.
        /// </summary>
        private static (WorkerVars wv, List<bool[]> joins) OmitBAndJoinAC(TestScope scope)
        {
            var wv = new WorkerVars(null, Path.Combine(scope.TempDir, "preview"), WorkerVars.SubsProcessingType.Preview);
            Directory.CreateDirectory(wv.MediaDir);
            var worker = new WorkerSubs();
            wv.CombinedAll = worker.combineAllSubs(wv, new NullProgressReporter());
            wv.CombinedAll = worker.inactivateLines(wv, new NullProgressReporter());
            Assert.Equal(4, wv.CombinedAll[0].Count);
            wv.CombinedAll[0][1].Active = false;
            return (wv, new List<bool[]> { new[] { true, false, false, false } });
        }

        private static void AssertBAppearsNowhereInTheText(string[] lines)
        {
            Assert.Equal(2, lines.Length); // A+C, Bye.
            Assert.Contains("Where are you going?<br>See you later.", lines[0]);
            Assert.Contains("Wohin gehst du?<br>Bis später.", lines[0]);
            foreach (string line in lines)
            {
                Assert.DoesNotContain("To the station.", line);
                Assert.DoesNotContain("Zum Bahnhof.", line);
            }
        }

        [RequiresFfmpegFact]
        public async Task OmittedLineInsideASnippet_AppearsNowhere_TextAudioVideo()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Off, gapRemoval: true, video: true);
            string srt2 = TestMedia.WriteDialogueTranslationSrt(scope.TempDir);
            Settings.Instance.Subs[1].FilePattern = srt2;
            Settings.Instance.Subs[1].Files = UtilsSubs.getSubsFiles(srt2).ToArray();
            Settings.Instance.Subs[1].Encoding = "utf-8";
            Settings.Instance.Snapshots.Enabled = false;

            var (wv, joins) = OmitBAndJoinAC(scope);
            await new SubsProcessor().StartAsync(new NullProgressReporter(), wv.CombinedAll, joins);

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            AssertBAppearsNowhereInTheText(lines);

            string media = MediaDir(scope);
            string audio = Path.Combine(media, SoundFile(lines[0]));
            Assert.True(File.Exists(audio));
            Assert.Matches(@"\b01\.000-.*07\.000\.", Path.GetFileName(audio)); // A's start to C's end in the name

            // A (1.0 s) + 200 ms kept of the A->C gap + C (1.0 s) = 2.2 s; B's 1.0 s is gone
            double duration = ProbeDuration(audio);
            Assert.InRange(duration, 2.05, 2.35);

            // the video clip is cut from the same list; keyframe snapping only approximates it
            var avis = Directory.GetFiles(media, "*.avi").Select(Path.GetFileName).ToArray();
            Assert.Equal(2, avis.Length);
            string video = Path.Combine(media, avis.Single(f => f!.Contains("01.000-") && f.Contains("07.000")));
            Assert.InRange(ProbeDuration(video), 1.5, 3.4);
        }

        [RequiresFfmpegFact]
        public async Task OmittedLineInsideASnippet_IsCutEvenWithGapRemovalOff()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            Configure(scope, SnippetMode.Off, gapRemoval: false);
            string srt2 = TestMedia.WriteDialogueTranslationSrt(scope.TempDir);
            Settings.Instance.Subs[1].FilePattern = srt2;
            Settings.Instance.Subs[1].Files = UtilsSubs.getSubsFiles(srt2).ToArray();
            Settings.Instance.Subs[1].Encoding = "utf-8";

            var (wv, joins) = OmitBAndJoinAC(scope);
            await new SubsProcessor().StartAsync(new NullProgressReporter(), wv.CombinedAll, joins);

            Assert.Empty(scope.Msgs.Errors);
            var lines = TsvLines(scope);
            AssertBAppearsNowhereInTheText(lines);

            // the whole 1.0-7.0 s span (6.0 s) minus B's 2.4-3.4 s = 5.0 s
            double duration = ProbeDuration(Path.Combine(MediaDir(scope), SoundFile(lines[0])));
            Assert.InRange(duration, 4.85, 5.2);
        }
    }
}
