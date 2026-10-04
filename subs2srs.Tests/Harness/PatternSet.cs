using System;
using System.IO;
using System.Linq;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// A folder of empty episode files and the patterns a user would type for them in the main
    /// window, with the <c>Files</c> arrays Go expands them to. Shared by the GUI test of
    /// <c>MainWindow.SaveSettings</c> and the test of <c>ProjectFiles.Resolve</c>, so
    /// both pin the same result. The folder name has brackets, an apostrophe, a space and
    /// Japanese; the files include an unsupported subtitle format and a hidden file, which
    /// the expansion drops.
    /// </summary>
    public sealed class PatternSet
    {
        public const int Start = 2;
        /// <summary>Episodes 2 to 4: three of the four files of each kind are kept.</summary>
        public const int End = 4;

        public string Dir { get; }
        public string Subs1Pattern => Path.Combine(Dir, "*.ja.*");
        public string Subs2Pattern => Path.Combine(Dir, "*.en.srt");
        public string VideoPattern => Path.Combine(Dir, "*.mkv");
        public string AudioPattern => Path.Combine(Dir, "*.mp3");

        /// <summary>What every pattern expands to before the end number truncates it.</summary>
        public string[] AllSubs1 { get; }
        public string[] AllSubs2 { get; }
        public string[] AllVideo { get; }
        public string[] AllAudio { get; }

        public string[] Subs1 => AllSubs1.Take(End - Start + 1).ToArray();
        public string[] Subs2 => AllSubs2.Take(End - Start + 1).ToArray();
        public string[] Video => AllVideo.Take(End - Start + 1).ToArray();
        public string[] Audio => AllAudio.Take(End - Start + 1).ToArray();

        private PatternSet(string dir)
        {
            Dir = dir;
            string[] eps = { "01", "02", "03", "04" };
            AllSubs1 = eps.Select(e => Path.Combine(dir, $"[Grp] Show - {e}" + (e == "02" ? ".ja.ass" : ".ja.srt"))).ToArray();
            AllSubs2 = eps.Select(e => Path.Combine(dir, $"[Grp] Show - {e}.en.srt")).ToArray();
            AllVideo = eps.Select(e => Path.Combine(dir, $"[Grp] Show - {e}.mkv")).ToArray();
            AllAudio = eps.Select(e => Path.Combine(dir, $"[Grp] Show - {e}.mp3")).ToArray();
        }

        public static PatternSet Create(string root)
        {
            var set = new PatternSet(Path.Combine(root, "Show [Grp] 'S1' 日本 ä"));
            Directory.CreateDirectory(set.Dir);
            foreach (string f in set.AllSubs1.Concat(set.AllSubs2).Concat(set.AllVideo).Concat(set.AllAudio))
                File.WriteAllText(f, "");
            // Matched by the Subs1 pattern but not a subtitle format subs2srs reads.
            File.WriteAllText(Path.Combine(set.Dir, "[Grp] Show - 01.ja.txt"), "");
            // Hidden: by its name on Linux and macOS, by its attribute on Windows.
            string hidden = Path.Combine(set.Dir, ".[Grp] Show - 00.ja.srt");
            File.WriteAllText(hidden, "");
            if (OperatingSystem.IsWindows())
                File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            return set;
        }
    }
}
