using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// Generates small synthetic test media once per test run into
    /// %TEMP%\subs2srs-testmedia\: a 10 s test.mp4 (testsrc + 440 Hz sine),
    /// a 4-line UTF-8 test.srt and a Shift-JIS test-sjis.srt.
    /// Requires ffmpeg.
    /// </summary>
    public static class TestMedia
    {
        public static readonly string Dir =
            Path.Combine(Path.GetTempPath(), "subs2srs-testmedia");

        public static string VideoPath => Path.Combine(Dir, "test.mp4");
        public static string SrtPath => Path.Combine(Dir, "test.srt");
        public static string SrtShiftJisPath => Path.Combine(Dir, "test-sjis.srt");

        /// <summary>The four subtitle lines, in order, as written to test.srt.</summary>
        public static readonly string[] Lines =
        {
            "Line one.",
            "Line two, with a comma.",
            "Line three has \"quotes\".",
            "Line four — with a dash.",
        };

        /// <summary>The four Japanese lines written to test-sjis.srt (Shift-JIS).</summary>
        public static readonly string[] LinesJapanese =
        {
            "こんにちは。",
            "元気ですか。",
            "今日はいい天気ですね。",
            "さようなら。",
        };

        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static bool _ready;

        /// <summary>Ensure the media exists. Safe to call from many tests.</summary>
        public static async Task EnsureAsync()
        {
            if (_ready) return;
            await _gate.WaitAsync();
            try
            {
                if (_ready) return;
                Directory.CreateDirectory(Dir);

                if (!File.Exists(VideoPath) || new FileInfo(VideoPath).Length < 10_000)
                    await GenerateVideoAsync();

                File.WriteAllText(SrtPath, BuildSrt(Lines), new UTF8Encoding(false));

                // Same registration path the app uses (Program.Main / SubsProcessor).
                UtilsCommon.RegisterEncodings();
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(SrtShiftJisPath, sjis.GetBytes(BuildSrt(LinesJapanese)));

                _ready = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// A short dialogue for the snippet tests, timed so that the rule-based
        /// grouper joins lines 1+2 (question, 0.4 s gap) and leaves 3 and 4 alone
        /// (2.6 s gap before 3; 3 ends with a full stop, 0.2 s gap to 4).
        /// </summary>
        public static readonly (double start, double end, string text)[] DialogueLines =
        {
            (1.0, 2.0, "Where are you going?"),
            (2.4, 3.4, "To the station."),
            (6.0, 7.0, "See you later."),
            (7.2, 8.2, "Bye."),
        };

        /// <summary>Write <see cref="DialogueLines"/> as a UTF-8 SRT file and return its path.</summary>
        public static string WriteDialogueSrt(string dir)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < DialogueLines.Length; i++)
            {
                var (start, end, text) = DialogueLines[i];
                sb.Append(i + 1).Append("\r\n");
                sb.Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append("\r\n");
                sb.Append(text).Append("\r\n\r\n");
            }
            string path = Path.Combine(dir, "dialogue.srt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string SrtTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }

        /// <summary>1→3 s, 3→5 s, 5→7 s, 7→9 s.</summary>
        public static string BuildSrt(string[] lines)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                int start = 1 + i * 2;
                int end = start + 2;
                sb.Append(i + 1).Append("\r\n");
                sb.Append($"00:00:{start:00},000 --> 00:00:{end:00},000\r\n");
                sb.Append(lines[i]).Append("\r\n\r\n");
            }
            return sb.ToString();
        }

        private static async Task GenerateVideoAsync()
        {
            string tmp = VideoPath + ".tmp.mp4";
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegProbe.Exe,
                Arguments =
                    "-nostdin -y -f lavfi -i testsrc=duration=10:size=320x240:rate=25 "
                    + "-f lavfi -i sine=frequency=440:duration=10 "
                    + "-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "
                    + $"\"{tmp}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg failed to generate test video (exit {p.ExitCode}):\n{await stderr}");
            File.Move(tmp, VideoPath, overwrite: true);
        }
    }
}
