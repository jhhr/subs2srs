//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>Animated snapshot settings and the pure ffmpeg argument builder.</summary>
    public class AnimatedSnapshotTests
    {
        private static readonly TimeRange R1 = new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1.0));
        private static readonly TimeRange R2 = new(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(2.4));

        // ── Settings ───────────────────────────────────────────────────────

        [Fact]
        public void Defaults()
        {
            var a = Settings.CreateDefaults().AnimatedSnapshots;
            Assert.False(a.Enabled);
            Assert.Equal(AnimatedSnapshotFormat.Webp, a.Format);
            Assert.Equal(10, a.Fps);
            Assert.Equal(350, a.Height);
            Assert.Equal(20, a.Quality);
            Assert.NotNull(a.Crop);
            Assert.Equal(0, a.Crop.Bottom);
        }

        [Fact]
        public void ProjectFile_RoundTrips_FormatAsString()
        {
            using var scope = new TestScope();
            string path = Path.Combine(scope.TempDir, "p.s2s.json");

            var a = Settings.Instance.AnimatedSnapshots;
            a.Enabled = true;
            a.Format = AnimatedSnapshotFormat.Avif;
            a.Fps = 12;
            a.Height = 240;
            a.Quality = 35;
            a.Crop.Bottom = 40;
            ProjectIO.Save(path, Settings.Instance);

            string json = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("\"animatedSnapshots\"", json);
            Assert.Contains("\"format\": \"Avif\"", json);

            Settings.Instance.Reset();
            Assert.False(Settings.Instance.AnimatedSnapshots.Enabled);

            ProjectIO.Load(path);
            a = Settings.Instance.AnimatedSnapshots;
            Assert.True(a.Enabled);
            Assert.Equal(AnimatedSnapshotFormat.Avif, a.Format);
            Assert.Equal(12, a.Fps);
            Assert.Equal(240, a.Height);
            Assert.Equal(35, a.Quality);
            Assert.Equal(40, a.Crop.Bottom);
        }

        [Fact]
        public void ProjectFile_WithoutSection_LoadsDefaults()
        {
            using var scope = new TestScope();
            string path = Path.Combine(scope.TempDir, "old.s2s.json");
            File.WriteAllText(path, "{ \"deckName\": \"Old\" }", Encoding.UTF8);

            ProjectIO.Load(path);

            Assert.NotNull(Settings.Instance.AnimatedSnapshots);
            Assert.False(Settings.Instance.AnimatedSnapshots.Enabled);
            Assert.Equal(350, Settings.Instance.AnimatedSnapshots.Height);
        }

        [Fact]
        public void Snapshot_And_Restore_CopyAnimatedSettings()
        {
            using var scope = new TestScope();
            Settings.Instance.AnimatedSnapshots.Enabled = true;
            var snap = Settings.Instance.Snapshot();
            Assert.NotSame(Settings.Instance.AnimatedSnapshots, snap.AnimatedSnapshots);

            Settings.Instance.AnimatedSnapshots.Enabled = false;
            Settings.Instance.RestoreFrom(snap);
            Assert.True(Settings.Instance.AnimatedSnapshots.Enabled);

            snap.AnimatedSnapshots = null;
            Settings.Instance.RestoreFrom(snap);
            Assert.NotNull(Settings.Instance.AnimatedSnapshots);
        }

        // ── Encoder probe (pure part) ──────────────────────────────────────

        private const string EncodersOutput =
            "Encoders:\n" +
            " V..... = Video\n" +
            " A..... = Audio\n" +
            " ------\n" +
            " V....D libaom-av1           libaom AV1 (codec av1)\n" +
            " V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)\n" +
            " V....D libwebp_anim         libwebp WebP image (codec webp)\n" +
            " V....D libwebp              libwebp WebP image (codec webp)\n" +
            " A..... aac                  AAC (Advanced Audio Coding)\n" +
            " S..... webvtt               WebVTT subtitle\n";

        [Fact]
        public void ParseEncoderNames_ReadsTheTableAfterTheLegend()
        {
            var names = UtilsAnimatedSnapshot.ParseEncoderNames(EncodersOutput.Replace("\n", "\r\n"));
            Assert.Contains("libaom-av1", names);
            Assert.Contains("libwebp_anim", names);
            Assert.Contains("libwebp", names);
            Assert.Contains("aac", names);
            Assert.DoesNotContain("Video", names);
            Assert.DoesNotContain("=", names);
            Assert.Empty(UtilsAnimatedSnapshot.ParseEncoderNames(""));
            Assert.Empty(UtilsAnimatedSnapshot.ParseEncoderNames("Error."));
        }

        [Fact]
        public void SelectEncoder_PrefersTheAnimationAwareWebpEncoder_AndFallsBack()
        {
            var all = UtilsAnimatedSnapshot.ParseEncoderNames(EncodersOutput);
            Assert.Equal("libwebp_anim", UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Webp, all));
            Assert.Equal("libaom-av1", UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Avif, all));

            var onlyOld = new HashSet<string> { "libwebp", "libsvtav1" };
            Assert.Equal("libwebp", UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Webp, onlyOld));
            Assert.Equal("libsvtav1", UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Avif, onlyOld));

            var none = new HashSet<string> { "libx264" };
            Assert.Null(UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Webp, none));
            Assert.Null(UtilsAnimatedSnapshot.SelectEncoder(AnimatedSnapshotFormat.Avif, none));
        }

        [Fact]
        public void EncoderFor_UsesTheOverriddenProbe()
        {
            try
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(new[] { "libwebp" });
                Assert.Equal("libwebp", UtilsAnimatedSnapshot.EncoderFor(AnimatedSnapshotFormat.Webp));
                Assert.Null(UtilsAnimatedSnapshot.EncoderFor(AnimatedSnapshotFormat.Avif));
            }
            finally
            {
                UtilsAnimatedSnapshot.OverrideAvailableEncoders(null);
            }
        }

        // ── Argument builder ───────────────────────────────────────────────

        [Theory]
        [InlineData(0, 63)]
        [InlineData(20, 50)]
        [InlineData(50, 32)]
        [InlineData(100, 0)]
        [InlineData(150, 0)]
        public void QualityToCrf_MapsHigherQualityToLowerCrf(int quality, int crf)
        {
            Assert.Equal(crf, UtilsAnimatedSnapshot.QualityToCrf(quality));
        }

        [Fact]
        public void Extension_FollowsTheFormat()
        {
            Assert.Equal(".webp", UtilsAnimatedSnapshot.Extension(AnimatedSnapshotFormat.Webp));
            Assert.Equal(".avif", UtilsAnimatedSnapshot.Extension(AnimatedSnapshotFormat.Avif));
        }

        [Fact]
        public void BuildFilter_PlainLine_HasNoSelect_AndNeverUpscales()
        {
            string f = UtilsAnimatedSnapshot.BuildFilter(null, 10, 350, new ImageCrop());
            Assert.Equal("fps=10,scale='trunc(min(350,ih)*dar/2+0.5)*2':'min(350,ih)':flags=lanczos+accurate_rnd,setsar=1", f);

            string single = UtilsAnimatedSnapshot.BuildFilter(new List<TimeRange> { R1 }, 10, 350, null);
            Assert.Equal(f, single);
        }

        [Fact]
        public void BuildFilter_MultiPart_PrefixesSelectAndSetpts()
        {
            string f = UtilsAnimatedSnapshot.BuildFilter(new List<TimeRange> { R1, R2 }, 10, 350, new ImageCrop());
            Assert.StartsWith("select='between(t,0.000,1.000)+between(t,1.200,2.400)',setpts=N/FRAME_RATE/TB,fps=10,", f);
        }

        [Fact]
        public void BuildFilter_Crop_GoesBeforeScale()
        {
            string f = UtilsAnimatedSnapshot.BuildFilter(null, 8, 200, new ImageCrop(10, 40, 0, 0));
            Assert.Equal("fps=8,crop=iw-0:ih-50:0:10,scale='trunc(min(200,ih)*dar/2+0.5)*2':'min(200,ih)':flags=lanczos+accurate_rnd,setsar=1", f);
        }

        [Fact]
        public void CodecArgs_PerEncoder()
        {
            Assert.Equal("-c:v libwebp_anim -lossless 0 -compression_level 6 -quality 20", UtilsAnimatedSnapshot.CodecArgs("libwebp_anim", 20));
            Assert.Equal("-c:v libwebp -lossless 0 -compression_level 6 -quality 100", UtilsAnimatedSnapshot.CodecArgs("libwebp", 120));
            Assert.Equal("-c:v libaom-av1 -crf 50 -b:v 0 -cpu-used 8 -pix_fmt yuv420p", UtilsAnimatedSnapshot.CodecArgs("libaom-av1", 20));
            Assert.Equal("-c:v libsvtav1 -crf 50 -preset 10 -pix_fmt yuv420p", UtilsAnimatedSnapshot.CodecArgs("libsvtav1", 20));
            Assert.Throws<ArgumentException>(() => UtilsAnimatedSnapshot.CodecArgs("libx264", 20));
        }

        [Fact]
        public void BuildArgs_SeeksBeforeTheInput_AndIsCultureInvariant()
        {
            var settings = new AnimatedSnapshots { Fps = 10, Height = 350, Quality = 20 };
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
                string args = UtilsAnimatedSnapshot.BuildArgs("C:\\v\\ep.mkv",
                    TimeSpan.FromSeconds(61.5), TimeSpan.FromSeconds(63.9),
                    new List<TimeRange> { R1, R2 }, settings, "libwebp_anim", "C:\\out\\a.webp");

                Assert.Equal(
                    "-y -an -sn -dn -ss 00:01:01.500 -t 00:00:02.400 -i \"C:\\v\\ep.mkv\" -map_metadata -1 -loop 0 " +
                    "-vf \"select='between(t,0.000,1.000)+between(t,1.200,2.400)',setpts=N/FRAME_RATE/TB,fps=10," +
                    "scale='trunc(min(350,ih)*dar/2+0.5)*2':'min(350,ih)':flags=lanczos+accurate_rnd,setsar=1\" " +
                    "-c:v libwebp_anim -lossless 0 -compression_level 6 -quality 20 -threads 1 \"C:\\out\\a.webp\"",
                    args);
                Assert.DoesNotContain(",5", args.Replace("select='between(t,0.000,1.000)+between(t,1.200,2.400)'", ""));
            }
            finally
            {
                CultureInfo.CurrentCulture = prev;
            }
        }

        [Fact]
        public void BuildArgs_EndBeforeStart_IsClampedToZeroDuration()
        {
            var settings = new AnimatedSnapshots();
            string args = UtilsAnimatedSnapshot.BuildArgs("in.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), null, settings, "libwebp", "o.webp");
            Assert.Contains("-ss 00:00:05.000 -t 00:00:00.000 -i", args);
        }
    }
}
