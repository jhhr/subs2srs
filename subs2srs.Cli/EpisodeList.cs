using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace subs2srs.Cli
{
  /// <summary>One episode of a run: its number and files, or why it is skipped.</summary>
  internal sealed class Episode
  {
    public Episode(int number, string? video, string? subs1, string? subs2, string? audio = null, string? skipReason = null)
    {
      Number = number;
      Video = video;
      Subs1 = subs1;
      Subs2 = subs2;
      Audio = audio;
      SkipReason = skipReason;
    }

    /// <summary>The episode number on the cards: the position among the videos (or files) plus the start number.</summary>
    public int Number { get; }
    /// <summary>Full paths; null when the episode has none (or, in pattern mode, the run needs none).</summary>
    public string? Video { get; }
    public string? Subs1 { get; }
    public string? Subs2 { get; }
    /// <summary>The audio file, in pattern mode when the project takes audio clips from audio files.</summary>
    public string? Audio { get; }
    /// <summary>Null when the episode is ready.</summary>
    public string? SkipReason { get; }
    public bool Skipped => SkipReason != null;
  }


  /// <summary>
  /// The episodes of one <c>go</c>, from a season folder (<see cref="FromSeason"/>) or from the
  /// project's own patterns (<see cref="FromPatterns"/>). Episode numbers never depend on
  /// another episode being skipped.
  /// </summary>
  internal sealed class EpisodeList
  {
    /// <summary>The season folder's subfolder of derived subtitles: the EN extracts and the retimed JP files.</summary>
    public const string SubsFolder = "s2s";
    public const string Subs1Tag = ".ja";
    public const string Subs2Tag = ".en";
    public static readonly string[] SubsExtensions = { ".ass", ".ssa", ".srt" };

    private EpisodeList(List<Episode> episodes, string? seasonDir, int leftOut)
    {
      Episodes = episodes;
      SeasonDir = seasonDir;
      LeftOut = leftOut;
    }

    public List<Episode> Episodes { get; }
    /// <summary>The season folder, full path; null in pattern mode.</summary>
    public string? SeasonDir { get; }
    /// <summary>Videos after the project's Episode End #: not in <see cref="Episodes"/>, and not skipped.</summary>
    public int LeftOut { get; }
    public int SkippedCount => Episodes.Count(e => e.Skipped);

    // ── season folder ───────────────────────────────────────────────────

    /// <summary>
    /// Read a season folder and its <see cref="SubsFolder"/> once and list its episodes
    /// (<see cref="ForSeason"/>). The project's own Subs1, Subs2, video and audio patterns
    /// play no part. Throws <see cref="CliException"/> for a missing folder, a folder without
    /// videos, or a project that takes its audio clips from audio files.
    /// </summary>
    public static EpisodeList FromSeason(string seasonDir, Settings settings)
    {
      if (settings.AudioClips.Enabled && !settings.AudioClips.UseAudioFromVideo)
        throw new CliException("--season takes the audio clips from each video, but the project takes them from audio files "
          + "(Audio tab: use existing audio). Choose audio from the video in the GUI and save the project, or run without --season.");
      string dir = Path.GetFullPath(seasonDir);
      string[] videos = SeasonVideos(seasonDir);
      string[] subsNames = UtilsCommon.getNonHiddenFilesInDir(Path.Combine(dir, SubsFolder))
        .Select(f => Path.GetFileName(f)).ToArray();
      return ForSeason(dir, videos, subsNames, settings.EpisodeStartNumber, settings.EpisodeEndNumber);
    }

    /// <summary>
    /// Every video of a season folder (<c>*.mkv</c>), full paths, in episode order. Throws
    /// <see cref="CliException"/> for a missing folder or a folder without videos.
    /// </summary>
    public static string[] SeasonVideos(string seasonDir)
    {
      string dir = Path.GetFullPath(seasonDir);
      if (!Directory.Exists(dir)) throw new CliException("season folder not found: " + seasonDir);
      // The GUI's own listing, so the order, and with it every episode number, is the one Go gives.
      string[] videos = UtilsCommon.getNonHiddenFiles(Path.Combine(dir, "*.mkv"));
      if (videos.Length == 0) throw new CliException("no .mkv file in the season folder " + dir);
      return videos;
    }

    /// <summary>
    /// The episodes of a season folder from its listing, without touching the disk.
    /// <paramref name="videos"/> are the folder's videos (full paths) in episode order;
    /// <paramref name="subsNames"/> the file names in its <see cref="SubsFolder"/>. Video
    /// <c>k</c> (from 0) is episode <c>k + startNumber</c>; videos past the end number are
    /// left out (<see cref="ProjectFiles.EpisodeLimit"/>, as Go cuts its lists). Subs1 is the
    /// one file named <c>&lt;video name&gt;.ja.&lt;ext&gt;</c> and Subs2 the one named
    /// <c>&lt;video name&gt;.en.&lt;ext&gt;</c>, <c>ext</c> one of <see cref="SubsExtensions"/>;
    /// none or several of either skips the episode with the reason.
    /// </summary>
    public static EpisodeList ForSeason(string seasonDir, IReadOnlyList<string> videos, IReadOnlyCollection<string> subsNames,
      int startNumber, int endNumber)
    {
      int count = ProjectFiles.EpisodeLimit(startNumber, endNumber) is int limit ? Math.Min(limit, videos.Count) : videos.Count;
      string subsDir = Path.Combine(seasonDir, SubsFolder);
      var episodes = new List<Episode>(count);
      for (int i = 0; i < count; i++)
      {
        string name = Path.GetFileNameWithoutExtension(videos[i]);
        List<string> subs1 = Named(subsNames, name, Subs1Tag);
        List<string> subs2 = Named(subsNames, name, Subs2Tag);
        string?[] problems = { Problem(subs1, Subs1Tag), Problem(subs2, Subs2Tag) };
        string reason = string.Join("; ", problems.Where(p => p != null));
        episodes.Add(new Episode(
          i + startNumber,
          videos[i],
          subs1.Count == 1 ? Path.Combine(subsDir, subs1[0]) : null,
          subs2.Count == 1 ? Path.Combine(subsDir, subs2[0]) : null,
          skipReason: reason.Length > 0 ? reason : null));
      }
      return new EpisodeList(episodes, seasonDir, videos.Count - count);
    }

    /// <summary>
    /// The names that are exactly <paramref name="videoName"/> + <paramref name="tag"/> + a
    /// subtitle extension, compared whole and ignoring case (as Windows compares file names),
    /// never as a wildcard pattern: names hold '[', ']' and other pattern characters.
    /// </summary>
    internal static List<string> Named(IEnumerable<string> names, string videoName, string tag)
      => names.Where(n => SubsExtensions.Any(ext => string.Equals(n, videoName + tag + ext, StringComparison.OrdinalIgnoreCase)))
              .ToList();

    internal static string? Problem(List<string> found, string tag) => found.Count switch
    {
      1 => null,
      0 => $"no {tag} file",
      _ => $"{found.Count} {tag} files ({string.Join(", ", found.Select(f => Path.Combine(SubsFolder, f)))})",
    };

    // ── the project's patterns ──────────────────────────────────────────

    /// <summary>
    /// The project's own patterns, expanded and cut as Go does (<see cref="ProjectFiles.Resolve"/>),
    /// then paired by position (<see cref="Pair"/>).
    /// </summary>
    public static EpisodeList FromPatterns(Settings settings)
    {
      ProjectFiles.Resolve();
      return Pair(
        settings.Subs[0].Files,
        (settings.Subs[1].FilePattern ?? "").Length > 0 ? settings.Subs[1].Files : null,
        NeedsVideo(settings) ? settings.VideoClips.Files : null,
        NeedsAudioFiles(settings) ? settings.AudioClips.Files : null,
        settings.EpisodeStartNumber);
    }

    /// <summary>
    /// Pair the files by position as the pipeline does. A null list is not used by the run
    /// (no Subs2 pattern, no media from the video, no audio files). Throws
    /// <see cref="CliException"/> when there is no Subs1 file or the counts differ: the
    /// pipeline would fail part-way with an index out of range.
    /// </summary>
    public static EpisodeList Pair(string[] subs1, string[]? subs2, string[]? videos, string[]? audio, int startNumber)
    {
      if (subs1.Length == 0)
        throw new CliException("the project's Subs1 pattern matches no subtitle file");
      var lists = new List<(string Name, string[] Files)> { ("Subs1", subs1) };
      if (subs2 != null) lists.Add(("Subs2", subs2));
      if (videos != null) lists.Add(("Video", videos));
      if (audio != null) lists.Add(("Audio", audio));
      if (lists.Any(l => l.Files.Length != subs1.Length))
      {
        string counts = string.Join(", ", lists.Select(l => $"{l.Files.Length} {l.Name}"));
        throw new CliException($"the project's patterns give different numbers of files ({counts}); "
          + "they are paired by position, so the counts must be equal:" + Environment.NewLine + SideBySide(lists, startNumber));
      }

      var episodes = new List<Episode>(subs1.Length);
      for (int i = 0; i < subs1.Length; i++)
        episodes.Add(new Episode(i + startNumber, videos?[i], subs1[i], subs2?[i], audio?[i]));
      return new EpisodeList(episodes, null, 0);
    }

    private static string SideBySide(List<(string Name, string[] Files)> lists, int startNumber)
    {
      var table = new TextTable(new[] { "#" }.Concat(lists.Select(l => $"{l.Name} ({l.Files.Length})")).ToArray());
      int rows = lists.Max(l => l.Files.Length);
      for (int i = 0; i < rows; i++)
        table.Add(new[] { (i + startNumber).ToString(CultureInfo.InvariantCulture) }
          .Concat(lists.Select(l => i < l.Files.Length ? Path.GetFileName(l.Files[i]) : "-")).ToArray());
      return table.ToString();
    }

    /// <summary>Whether Go reads the videos: audio clips from the video, snapshots, animated snapshots or video clips.</summary>
    public static bool NeedsVideo(Settings s)
      => (s.AudioClips.Enabled && s.AudioClips.UseAudioFromVideo) || s.Snapshots.Enabled || s.AnimatedSnapshots.Enabled || s.VideoClips.Enabled;

    /// <summary>Whether Go cuts the audio clips from audio files (the Audio tab's "use existing audio").</summary>
    public static bool NeedsAudioFiles(Settings s) => s.AudioClips.Enabled && !s.AudioClips.UseAudioFromVideo;
  }
}
