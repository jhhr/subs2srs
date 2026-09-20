//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// Animated snapshots through the whole pipeline on the generated test media.
    /// Needs ffmpeg with libwebp (and an AV1 encoder for the avif test).
    /// </summary>
    public class AnimatedSnapshotE2ETests
    {
        /// <summary>
        /// Number of animation frames (ANMF chunks) in a WebP file, read from the
        /// RIFF container: the ffmpeg 5.x webp demuxer cannot count them.
        /// </summary>
        internal static int WebpFrameCount(string file)
        {
            byte[] d = File.ReadAllBytes(file);
            Assert.True(d.Length > 12, "too short to be a WebP: " + file);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(d, 0, 4));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(d, 8, 4));
            int frames = 0;
            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                string tag = System.Text.Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                if (tag == "ANMF") frames++;
                pos += 8 + size + (size & 1);
            }
            return frames;
        }

        /// <summary>
        /// Frames counted by decoding with ffprobe (works for avif). An animated avif
        /// carries a one-frame still primary item next to the animation track; ffmpeg
        /// 7.1+ exposes that item as its own (first) video stream, so every video
        /// stream is counted and the largest count is the animation's.
        /// </summary>
        internal static int ProbeFrameCount(string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ConstantSettings.ResolveToolOrName("ffprobe"),
                Arguments = $"-v error -count_frames -select_streams v -show_entries stream=nb_read_frames -of csv=p=0 \"{file}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var counts = new System.Collections.Generic.List<int>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim().TrimEnd(','), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    counts.Add(n);
            }
            Assert.True(counts.Count > 0, "ffprobe reported no decodable video stream for " + file + ": " + output);
            return counts.Max();
        }

        private static string AnimatedFile(string tsvLine, string ext)
        {
            string marker = ext + "\">";
            int end = tsvLine.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(end >= 0, "no animated snapshot column in: " + tsvLine);
            int start = tsvLine.LastIndexOf("<img src=\"", end, StringComparison.Ordinal) + 10;
            return tsvLine.Substring(start, end + ext.Length - start);
        }

        private static void EnableAnimated(AnimatedSnapshotFormat format)
        {
            var a = Settings.Instance.AnimatedSnapshots;
            a.Enabled = true;
            a.Format = format;
            a.Fps = 10;
            a.Height = 120;
            a.Quality = 20;
        }

        [RequiresFfmpegEncoderFact(AnimatedSnapshotFormat.Webp)]
        public async Task Webp_FilesExist_AreAnimated_AndGetTheirOwnColumn()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            SnippetE2ETests.Configure(scope, SnippetMode.Rules, gapRemoval: true);
            Settings.Instance.Snippets.GapKeepMs = 0;
            EnableAnimated(AnimatedSnapshotFormat.Webp);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = SnippetE2ETests.TsvLines(scope);
            Assert.Equal(3, lines.Length);

            string media = SnippetE2ETests.MediaDir(scope);
            var webps = Directory.GetFiles(media, "*.webp");
            Assert.Equal(3, webps.Length);
            Assert.Empty(Directory.GetFiles(media, "*.tmp.webp"));

            // Column order: ... snapshot, animated snapshot, video, subs1
            foreach (string line in lines)
            {
                var cols = line.Split('\t');
                int jpg = Array.FindIndex(cols, c => c.EndsWith(".jpg\">", StringComparison.Ordinal));
                int webp = Array.FindIndex(cols, c => c.EndsWith(".webp\">", StringComparison.Ordinal));
                Assert.True(jpg >= 0 && webp == jpg + 1, "columns: " + line);
                Assert.True(File.Exists(Path.Combine(media, AnimatedFile(line, ".webp"))), line);
            }

            // Snippet "Where are you going?" + "To the station." spans 1.0-3.4 s; with the
            // 0.4 s gap removed it is 2.0 s of frames at 10 fps. A plain 1 s line has ~10.
            int snippetFrames = WebpFrameCount(Path.Combine(media, AnimatedFile(lines[0], ".webp")));
            int singleFrames = WebpFrameCount(Path.Combine(media, AnimatedFile(lines[1], ".webp")));
            Assert.InRange(singleFrames, 8, 12);
            Assert.InRange(snippetFrames, 18, 22);
            Assert.Matches(@"\b01\.000-.*03\.400\.webp$", Path.GetFileName(AnimatedFile(lines[0], ".webp"))); // whole span in the name
        }

        [RequiresFfmpegEncoderFact(AnimatedSnapshotFormat.Webp)]
        public async Task Webp_WithoutGapRemoval_HasMoreFrames()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            SnippetE2ETests.Configure(scope, SnippetMode.Rules, gapRemoval: false);
            Settings.Instance.Snapshots.Enabled = false;
            Settings.Instance.AudioClips.Enabled = false;
            EnableAnimated(AnimatedSnapshotFormat.Webp);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = SnippetE2ETests.TsvLines(scope);
            string media = SnippetE2ETests.MediaDir(scope);
            int snippetFrames = WebpFrameCount(Path.Combine(media, AnimatedFile(lines[0], ".webp")));
            Assert.InRange(snippetFrames, 22, 26); // the whole 2.4 s span
            Assert.Empty(Directory.GetFiles(media, "*.jpg"));
        }

        [RequiresFfmpegEncoderFact(AnimatedSnapshotFormat.Avif)]
        public async Task Avif_FilesExist_AndAreAnimated()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            SnippetE2ETests.Configure(scope, SnippetMode.Off, gapRemoval: true);
            Settings.Instance.AudioClips.Enabled = false;
            EnableAnimated(AnimatedSnapshotFormat.Avif);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            var lines = SnippetE2ETests.TsvLines(scope);
            Assert.Equal(4, lines.Length);
            string media = SnippetE2ETests.MediaDir(scope);
            Assert.Equal(4, Directory.GetFiles(media, "*.avif").Length);
            Assert.Empty(Directory.GetFiles(media, "*.webp"));

            int frames = ProbeFrameCount(Path.Combine(media, AnimatedFile(lines[0], ".avif")));
            Assert.InRange(frames, 8, 12);
        }

        [RequiresFfmpegFact]
        public async Task Off_ProducesNoFilesAndNoColumn()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            SnippetE2ETests.Configure(scope, SnippetMode.Off, gapRemoval: true);
            Settings.Instance.AudioClips.Enabled = false;
            Assert.False(Settings.Instance.AnimatedSnapshots.Enabled);

            await new SubsProcessor().StartAsync(new NullProgressReporter());

            Assert.Empty(scope.Msgs.Errors);
            string media = SnippetE2ETests.MediaDir(scope);
            Assert.Empty(Directory.GetFiles(media, "*.webp"));
            Assert.Empty(Directory.GetFiles(media, "*.avif"));
            foreach (string line in SnippetE2ETests.TsvLines(scope))
            {
                Assert.DoesNotContain(".webp", line);
                Assert.Equal(1, line.Split('\t').Count(c => c.StartsWith("<img src=", StringComparison.Ordinal)));
            }
        }

        [RequiresFfmpegFact]
        public async Task MissingEncoder_IsReportedAsAnError_NotACrash()
        {
            await TestMedia.EnsureAsync();
            using var scope = new TestScope();
            SnippetE2ETests.Configure(scope, SnippetMode.Off, gapRemoval: true);
            Settings.Instance.AudioClips.Enabled = false;
            Settings.Instance.Snapshots.Enabled = false;
            EnableAnimated(AnimatedSnapshotFormat.Webp);
            try
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(new[] { "libx264" });

                await new SubsProcessor().StartAsync(new NullProgressReporter());

                Assert.NotEmpty(scope.Msgs.Errors);
                Assert.Contains(scope.Msgs.Errors, e => e.Contains("libwebp", StringComparison.Ordinal));
                Assert.Empty(Directory.GetFiles(SnippetE2ETests.MediaDir(scope), "*.webp"));
            }
            finally
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(null);
            }
        }
    }
}
