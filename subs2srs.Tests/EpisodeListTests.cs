using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The episode list of subs2srs-cli <c>go</c>: the season folder as a pure function of its
  /// listing, the thin wrapper that reads a real folder, and the project's patterns paired by
  /// position. <see cref="CliTests"/> runs the command itself.
  /// </summary>
  public class EpisodeListTests
  {
    private static readonly string Season = Path.Combine(Path.GetTempPath(), "Show S1 [Grp] 日本");

    private static string[] Videos(params string[] names) => names.Select(n => Path.Combine(Season, n)).ToArray();
    private static string S2s(string name) => Path.Combine(Season, "s2s", name);
    private static EpisodeList List(string[] videos, string[] subs, int start = 1, int end = 0)
      => EpisodeList.ForSeason(Season, videos, subs, start, end);

    // ── the season folder, from its listing ─────────────────────────────

    [Fact]
    public void Season_NumbersFromTheStartNumber_AndASkippedEpisodeKeepsTheLaterNumbers()
    {
      var list = List(
        Videos("Show - 01.mkv", "Show - 02.mkv", "Show - 03.mkv", "Show - 04.mkv"),
        new[] { "Show - 01.ja.srt", "Show - 01.en.srt", "Show - 02.en.srt", "Show - 03.ja.ass", "Show - 03.en.ass", "Show - 04.ja.srt", "Show - 04.en.srt" },
        start: 5);

      Assert.Equal(new[] { 5, 6, 7, 8 }, list.Episodes.Select(e => e.Number));
      Episode skipped = list.Episodes[1];
      Assert.Equal("no .ja file", skipped.SkipReason);
      Assert.Null(skipped.Subs1);
      Assert.Equal(S2s("Show - 02.en.srt"), skipped.Subs2);
      Assert.Equal(Path.Combine(Season, "Show - 02.mkv"), skipped.Video);

      Episode third = list.Episodes[2];
      Assert.False(third.Skipped);
      Assert.Equal(S2s("Show - 03.ja.ass"), third.Subs1);
      Assert.Equal(S2s("Show - 03.en.ass"), third.Subs2);
      Assert.False(list.Episodes[3].Skipped);
      Assert.Equal(1, list.SkippedCount);
      Assert.Equal(0, list.LeftOut);
    }

    [Fact]
    public void Season_NoneOrSeveralCandidates_SkipTheEpisode_NamingTheFiles()
    {
      var list = List(Videos("A.mkv", "B.mkv"), new[] { "B.ja.ass", "B.ja.srt", "B.en.srt" });

      Assert.Equal("no .ja file; no .en file", list.Episodes[0].SkipReason);
      Episode b = list.Episodes[1];
      Assert.Null(b.Subs1);
      Assert.Equal(S2s("B.en.srt"), b.Subs2);
      Assert.Equal($"2 .ja files ({Path.Combine("s2s", "B.ja.ass")}, {Path.Combine("s2s", "B.ja.srt")})", b.SkipReason);
    }

    [Fact]
    public void Season_TagsAndExtensions_MatchIgnoringCase_AndNothingElseMatches()
    {
      var list = List(Videos("Show - 01.mkv"), new[]
      {
        "Show - 01.JA.SRT", "Show - 01.En.Ssa",
        // the original, untagged JP file; other tags; other formats; a backup
        "Show - 01.srt", "Show - 01.jp.srt", "Show - 01.eng.srt", "Show - 01.ja.txt", "Show - 01.en.idx", "Show - 01.ja.srt.bak",
      });

      Episode e = Assert.Single(list.Episodes);
      Assert.Null(e.SkipReason);
      Assert.Equal(S2s("Show - 01.JA.SRT"), e.Subs1);
      Assert.Equal(S2s("Show - 01.En.Ssa"), e.Subs2);
    }

    [Fact]
    public void Season_BracketedApostropheAndJapaneseNames_MatchExactly()
    {
      var list = List(
        Videos("[Grp] 進撃の巨人 - 01 [1080p].mkv", "Tom's Show - 01.mkv", "Show - 01.mkv", "Show - 01.5.mkv"),
        new[]
        {
          "[Grp] 進撃の巨人 - 01 [720p].ja.ass", // a lookalike: a pattern [1080p] would treat as a character class
          "[Grp] 進撃の巨人 - 01 [1080p].ja.ass", "[Grp] 進撃の巨人 - 01 [1080p].en.srt",
          "Tom's Show - 01.ja.srt", "Tom's Show - 01.en.srt",
          "Show - 01.5.ja.srt", "Show - 01.5.en.srt",
        });

      Assert.Equal(S2s("[Grp] 進撃の巨人 - 01 [1080p].ja.ass"), list.Episodes[0].Subs1);
      Assert.Equal(S2s("[Grp] 進撃の巨人 - 01 [1080p].en.srt"), list.Episodes[0].Subs2);
      Assert.False(list.Episodes[1].Skipped);
      // "Show - 01" does not take the files of "Show - 01.5".
      Assert.Equal("no .ja file; no .en file", list.Episodes[2].SkipReason);
      Assert.Equal(S2s("Show - 01.5.ja.srt"), list.Episodes[3].Subs1);
      Assert.Equal(new[] { 1, 2, 3, 4 }, list.Episodes.Select(e => e.Number));
    }

    [Fact]
    public void Season_AnOldTrackExtract_IsNotTheEnglishFile()
    {
      var list = List(Videos("Show - 01.mkv"), new[] { "Show - 01.ja.srt", "Show - 01 - Track 03 - English.srt" });

      Assert.Equal("no .en file", Assert.Single(list.Episodes).SkipReason);
    }

    [Fact]
    public void Season_EndNumber_LeavesLaterVideosOut_WithoutSkippingThem()
    {
      string[] videos = Videos("1.mkv", "2.mkv", "3.mkv", "4.mkv");
      string[] subs = Enumerable.Range(1, 4).SelectMany(i => new[] { $"{i}.ja.srt", $"{i}.en.srt" }).ToArray();

      var cut = List(videos, subs, start: 3, end: 4);
      Assert.Equal(new[] { 3, 4 }, cut.Episodes.Select(e => e.Number));
      Assert.Equal(Path.Combine(Season, "2.mkv"), cut.Episodes[1].Video);
      Assert.Equal(2, cut.LeftOut);
      Assert.Equal(0, cut.SkippedCount);

      // An end number below the start is ignored, as Go ignores it.
      Assert.Equal(4, List(videos, subs, start: 3, end: 2).Episodes.Count);
    }

    // ── the season folder on disk ───────────────────────────────────────

    [Fact]
    public void FromSeason_ReadsTheFolder_InTheGuisOrder()
    {
      using var scope = new TestScope(" ä 日本");
      string dir = Path.Combine(scope.TempDir, "Show S1 [Grp] 'x' 日本");
      Directory.CreateDirectory(Path.Combine(dir, "s2s"));
      // Created out of order; "a" and "B" sort differently by culture and by ordinal.
      string[] names = { "B - 02.mkv", "進撃 - 03.mkv", "a - 01.mkv" };
      foreach (string n in names)
      {
        File.WriteAllText(Path.Combine(dir, n), "");
        string stem = Path.GetFileNameWithoutExtension(n);
        File.WriteAllText(Path.Combine(dir, "s2s", stem + ".ja.srt"), "");
        File.WriteAllText(Path.Combine(dir, "s2s", stem + ".en.ass"), "");
      }
      File.WriteAllText(Path.Combine(dir, "a - 01.srt"), ""); // the original JP file, beside the video
      File.WriteAllText(Path.Combine(dir, "extra.mp4"), "");
      string hidden = Path.Combine(dir, ".hidden.mkv");
      File.WriteAllText(hidden, "");
      if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);
      Settings.Instance.EpisodeStartNumber = 1;

      var list = EpisodeList.FromSeason(dir, Settings.Instance);

      Assert.Equal(UtilsCommon.getNonHiddenFiles(Path.Combine(dir, "*.mkv")), list.Episodes.Select(e => e.Video));
      Assert.Equal(3, list.Episodes.Count);
      Assert.Equal(dir, list.SeasonDir);
      Assert.All(list.Episodes, e =>
      {
        Assert.False(e.Skipped, e.SkipReason);
        Assert.True(File.Exists(e.Subs1), e.Subs1);
        Assert.True(File.Exists(e.Subs2), e.Subs2);
      });
    }

    [Fact]
    public void FromSeason_WithoutTheSubsFolder_SkipsEveryEpisode()
    {
      using var scope = new TestScope();
      File.WriteAllText(Path.Combine(scope.TempDir, "Show - 01.mkv"), "");

      var list = EpisodeList.FromSeason(scope.TempDir, Settings.Instance);

      Assert.Equal("no .ja file; no .en file", Assert.Single(list.Episodes).SkipReason);
    }

    [Fact]
    public void FromSeason_MissingFolder_NoVideo_OrAudioFromFiles_IsAnError()
    {
      using var scope = new TestScope();
      var missing = Assert.Throws<CliException>(() => EpisodeList.FromSeason(Path.Combine(scope.TempDir, "nope"), Settings.Instance));
      Assert.Equal(CliOptions.ExitError, missing.ExitCode);
      Assert.Contains("not found", missing.Message);

      Assert.Contains("no .mkv", Assert.Throws<CliException>(() => EpisodeList.FromSeason(scope.TempDir, Settings.Instance)).Message);

      File.WriteAllText(Path.Combine(scope.TempDir, "Show - 01.mkv"), "");
      Settings.Instance.AudioClips.Enabled = true;
      Settings.Instance.AudioClips.UseAudioFromVideo = false;
      Settings.Instance.AudioClips.UseExistingAudio = true;
      Assert.Contains("audio files", Assert.Throws<CliException>(() => EpisodeList.FromSeason(scope.TempDir, Settings.Instance)).Message);

      Settings.Instance.AudioClips.Enabled = false; // no audio clips: where they would come from does not matter
      Assert.Single(EpisodeList.FromSeason(scope.TempDir, Settings.Instance).Episodes);
    }

    // ── the project's patterns ──────────────────────────────────────────

    [Fact]
    public void Pair_UnequalCounts_AreRefused_WithTheListsSideBySide()
    {
      var ex = Assert.Throws<CliException>(() => EpisodeList.Pair(
        new[] { "/s/ep1.ja.srt", "/s/ep2.ja.srt", "/s/ep3.ja.srt" },
        new[] { "/s/ep1.en.srt", "/s/ep2.en.srt" },
        new[] { "/v/ep1.mkv" },
        null,
        startNumber: 4));

      Assert.Equal(CliOptions.ExitError, ex.ExitCode);
      Assert.Contains("3 Subs1, 2 Subs2, 1 Video", ex.Message);
      string[][] rows = ex.Message.Split(Environment.NewLine).Skip(1).Select(Cells).ToArray();
      Assert.Equal(new[] { "#", "Subs1 (3)", "Subs2 (2)", "Video (1)" }, rows[0]);
      Assert.Equal(new[] { "4", "ep1.ja.srt", "ep1.en.srt", "ep1.mkv" }, rows[1]);
      Assert.Equal(new[] { "5", "ep2.ja.srt", "ep2.en.srt", "-" }, rows[2]);
      Assert.Equal(new[] { "6", "ep3.ja.srt", "-", "-" }, rows[3]);
      Assert.Equal(4, rows.Length);
    }

    /// <summary>The cells of a table line: columns are two or more spaces apart.</summary>
    internal static string[] Cells(string line) => Regex.Split(line.Trim(), " {2,}");

    [Fact]
    public void Pair_ListsTheRunDoesNotUse_AreNotCounted()
    {
      var list = EpisodeList.Pair(new[] { "/s/1.srt", "/s/2.srt" }, null, null, null, startNumber: 1);

      Assert.Equal(new[] { 1, 2 }, list.Episodes.Select(e => e.Number));
      Assert.All(list.Episodes, e => { Assert.Null(e.Subs2); Assert.Null(e.Video); Assert.False(e.Skipped); });
      Assert.Null(list.SeasonDir);
      Assert.Contains("matches no subtitle file",
        Assert.Throws<CliException>(() => EpisodeList.Pair(Array.Empty<string>(), null, null, null, 1)).Message);
    }

    [Fact]
    public void NeedsVideo_AndNeedsAudioFiles_FollowTheMediaSettings()
    {
      using var scope = new TestScope();
      var s = Settings.Instance;
      s.AudioClips.Enabled = false;
      s.Snapshots.Enabled = false;
      s.AnimatedSnapshots.Enabled = false;
      s.VideoClips.Enabled = false;
      Assert.False(EpisodeList.NeedsVideo(s));
      Assert.False(EpisodeList.NeedsAudioFiles(s));

      s.Snapshots.Enabled = true;
      Assert.True(EpisodeList.NeedsVideo(s));
      s.Snapshots.Enabled = false;
      s.AnimatedSnapshots.Enabled = true;
      Assert.True(EpisodeList.NeedsVideo(s));
      s.AnimatedSnapshots.Enabled = false;
      s.VideoClips.Enabled = true;
      Assert.True(EpisodeList.NeedsVideo(s));
      s.VideoClips.Enabled = false;

      s.AudioClips.Enabled = true;
      s.AudioClips.UseAudioFromVideo = true;
      Assert.True(EpisodeList.NeedsVideo(s));
      Assert.False(EpisodeList.NeedsAudioFiles(s));
      s.AudioClips.UseAudioFromVideo = false;
      Assert.False(EpisodeList.NeedsVideo(s));
      Assert.True(EpisodeList.NeedsAudioFiles(s));
    }

    [Fact]
    public void FromPatterns_ExpandsAndCutsAsGoDoes()
    {
      using var scope = new TestScope(" ä 日本");
      var set = PatternSet.Create(scope.TempDir);
      var s = Settings.Instance;
      s.Subs[0].FilePattern = set.Subs1Pattern;
      s.Subs[1].FilePattern = set.Subs2Pattern;
      s.VideoClips.FilePattern = set.VideoPattern;
      s.AudioClips.FilePattern = set.AudioPattern;
      s.EpisodeStartNumber = PatternSet.Start;
      s.EpisodeEndNumber = PatternSet.End;
      s.Snapshots.Enabled = true;               // so the videos are used
      s.AudioClips.Enabled = true;
      s.AudioClips.UseAudioFromVideo = false;   // and the audio files

      var list = EpisodeList.FromPatterns(s);

      Assert.Equal(new[] { 2, 3, 4 }, list.Episodes.Select(e => e.Number));
      Assert.Equal(set.Subs1, list.Episodes.Select(e => e.Subs1));
      Assert.Equal(set.Subs2, list.Episodes.Select(e => e.Subs2));
      Assert.Equal(set.Video, list.Episodes.Select(e => e.Video));
      Assert.Equal(set.Audio, list.Episodes.Select(e => e.Audio));
    }
  }
}
