//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <c>subs2srs-cli season</c> through <see cref="CliRunner.RunAsync"/>: its options, its table
  /// (without mkvmerge), the errors before any work, and a generated season (real mkv files,
  /// real mkvmerge and mkvextract, a scripted subsretimer) extracted and retimed, run again,
  /// forced, with <c>--track</c>, <c>--only</c> and <c>--dry-run</c>.
  /// </summary>
  public class CliSeasonTests : IDisposable
  {
    private readonly TestScope scope = new TestScope(" ä 日本");
    private readonly string? path = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
      SubsRetimerLauncher.RunnerOverride = null;
      MkvTracks.RunnerOverride = null;
      MkvExtract.RunnerOverride = null;
      ConstantSettings.MkvToolNixDirsOverride = null;
      Environment.SetEnvironmentVariable("PATH", path);
      scope.Dispose();
    }

    private static string Name(int n) => $"[Grp] 番組 - {n:00}";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "retime", name);

    private static string Arg(ProcessStartInfo psi, string option) => psi.ArgumentList[psi.ArgumentList.IndexOf(option) + 1];

    /// <summary>A Tools Directory holding an empty <c>subsretimer</c> (the scripted runner stands in for it); its path.</summary>
    private string FakeSubsretimer()
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      string exe = Path.Combine(tools, "subsretimer");
      File.WriteAllText(exe, "");
      ConstantSettings.ToolsDir = tools;
      return exe;
    }

    private string Project() => CliTests.SaveProject(scope, s =>
    {
      s.Subs[0].Encoding = "utf-8";
      s.EpisodeStartNumber = 1;
    });

    /// <summary>
    /// A subsretimer that, for each run, records the start and as the real one would: for a JP
    /// file of episode 2 saves nothing (exit 2, below --min-match), for the others writes
    /// <c>--output</c>; and writes the matching report.
    /// </summary>
    private static List<ProcessStartInfo> ScriptRetimer(Action<int>? onRun = null)
    {
      var started = new List<ProcessStartInfo>();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) =>
      {
        started.Add(psi);
        onRun?.Invoke(started.Count);
        ct.ThrowIfCancellationRequested();
        string target = psi.ArgumentList[^1];
        bool below = Path.GetFileName(target).StartsWith(Name(2), StringComparison.Ordinal);
        if (!below) File.WriteAllText(Arg(psi, "--output"), "retimed " + Path.GetFileName(target));
        File.Copy(Fixture(below ? "below-min-match.retime.json" : "saved.retime.json"), Arg(psi, "--report"), overwrite: true);
        return Task.FromResult(below
          ? new CliProcessResult { ExitCode = 2, Stdout = "", Stderr = "subsretimer: below --min-match; nothing saved\n" }
          : new CliProcessResult { ExitCode = 0, Stdout = Arg(psi, "--output") + "\n", Stderr = "" });
      };
      return started;
    }

    private static string Write(string path, string text, DateTime? time = null)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, text, new UTF8Encoding(false));
      if (time != null) File.SetLastWriteTimeUtc(path, time.Value);
      return path;
    }

    private static readonly DateTime AnHourAgo = DateTime.UtcNow.AddHours(-1);

    /// <summary>Every file under <paramref name="dir"/>: its path, length and time.</summary>
    private static string[] Snapshot(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
      .Select(f => $"{Path.GetRelativePath(dir, f)} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f):O}")
      .OrderBy(l => l, StringComparer.Ordinal).ToArray();

    private static string[] S2s(string dir) => Directory.GetFiles(Path.Combine(dir, "s2s"))
      .Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Episode <paramref name="n"/>'s row of the table, cut into its cells (cells hold no two spaces in a row).</summary>
    private static string[] Row(CliTests.Result r, int n)
      => Regex.Split(r.Lines.Single(l => l.StartsWith(Name(n), StringComparison.Ordinal)), " {2,}");

    // ── the command line ────────────────────────────────────────────────

    [Fact]
    public void Parse_Season_TakesItsFolder_AndItsOptions()
    {
      CliOptions o = CliOptions.Parse(new[] { "season", "D:\\Show [S1] 日本", "--project", "p.json", "--track", "3", "--min-match", "0.85",
        "--force", "--dry-run", "--yes" });
      Assert.Equal("season", o.Command);
      Assert.Equal("D:\\Show [S1] 日本", o.SeasonDir);
      Assert.Equal("p.json", o.ProjectPath);
      Assert.Equal(3, o.Track);
      Assert.Equal(0.85, o.MinMatch);
      Assert.True(o.Force && o.DryRun && o.Yes);
      Assert.Equal(SeasonStage.Retime, CliOptions.Parse(new[] { "season", "d", "--project", "p", "--only", "Retime" }).Only);

      CliOptions plain = CliOptions.Parse(new[] { "season", "--project", "p.json", "dir" });
      Assert.Equal("dir", plain.SeasonDir);
      Assert.Null(plain.Track);
      Assert.Null(plain.MinMatch); // not passed to subsretimer: there is no default yet
      Assert.False(plain.Force);
      Assert.Null(plain.Only);
      Assert.Equal(SeasonStage.Extract, CliOptions.Parse(new[] { "season", "d", "--project", "p", "--only", "extract" }).Only);
      Assert.Equal(0, CliOptions.Parse(new[] { "season", "d", "--project", "p", "--min-match", "0" }).MinMatch);
    }

    [Theory]
    [InlineData("season needs the season folder", "season", "--project", "p.json")]
    [InlineData("season needs --project", "season", "dir")]
    [InlineData("not --season", "season", "--season", "dir", "--project", "p.json")]
    [InlineData("unknown argument 'more'", "season", "dir", "more", "--project", "p.json")]
    [InlineData("--track takes a track id", "season", "dir", "--project", "p.json", "--track", "x")]
    [InlineData("--track takes a track id", "season", "dir", "--project", "p.json", "--track", "-1")]
    [InlineData("--min-match takes a number from 0 to 1, like 0.85; not '0,8'", "season", "dir", "--project", "p.json", "--min-match", "0,8")]
    [InlineData("--min-match takes a number from 0 to 1", "season", "dir", "--project", "p.json", "--min-match", "1.5")]
    [InlineData("--only takes extract or retime, not 'cards'", "season", "dir", "--project", "p.json", "--only", "cards")]
    [InlineData("--track chooses the track to extract", "season", "dir", "--project", "p.json", "--only", "retime", "--track", "3")]
    [InlineData("--only extract does not retime", "season", "dir", "--project", "p.json", "--only", "extract", "--min-match", "0.5")]
    [InlineData("--grouping is an option of go, not season", "season", "dir", "--project", "p.json", "--grouping", "rules")]
    [InlineData("--track is an option of season, not go", "go", "--project", "p.json", "--track", "3")]
    [InlineData("--force is an option of season, not go", "go", "--project", "p.json", "--force")]
    [InlineData("unknown argument 'dir'", "go", "dir", "--project", "p.json")]
    public async Task UsageErrors_Exit1_OnStderr(string expected, params string[] args)
    {
      CliTests.Result r = await CliTests.Run(args);
      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains(expected, r.Stderr);
      Assert.Equal("", r.Stdout);
    }

    [Fact]
    public async Task Help_ShowsSeason_AndSaysWhatForceAndMinMatchDo()
    {
      CliTests.Result help = await CliTests.Run("--help");
      Assert.Equal(0, help.Code);
      Assert.Contains("subs2srs-cli season <dir> --project <file>", help.Stdout);
      Assert.Contains("There is no default:", help.Stdout); // --min-match
      Assert.Contains("After changing --track this is needed", help.Stdout); // --force
    }

    // ── the table, without mkvmerge ─────────────────────────────────────

    private static MkvTrackInfo Track(int id, string name, int events) => new MkvTrackInfo
    {
      Id = id, Type = "subtitles", CodecId = "S_TEXT/ASS", Language = "eng", Name = name, Events = events,
    };

    [Fact]
    public void Table_EpisodeTrackRetime_TheTrackNote_TheLastLine_AndTheEditorCommands()
    {
      string dir = Path.Combine(Path.GetTempPath(), "Show 日本");
      SeasonEpisode Ep(int n, MkvTrackPick? pick, RetimeOutcome? retime, bool cancelled = false) =>
        new SeasonEpisode(Path.Combine(dir, Name(n) + ".mkv")) { Pick = pick, Retime = retime, Cancelled = cancelled };
      var english = new MkvTrackPick { Track = Track(3, "English", 312) };
      var episodes = new List<SeasonEpisode>
      {
        Ep(1, english, new RetimeOutcome(RetimeKind.Kept, "out")),
        Ep(2, new MkvTrackPick { Track = Track(3, "English", 305) },
          new RetimeOutcome(RetimeKind.NotSaved, EditorCommand: "subsretimer --output 'a b' 'c' 'd'")),
        Ep(3, MkvTrackPick.None(MkvTracks.NoSubtitleTrack), new RetimeOutcome(RetimeKind.NoEnFile, Reason: SeasonCommand.NoEnTrack)),
        Ep(4, new MkvTrackPick { Track = Track(4, "Full", 400) }, new RetimeOutcome(RetimeKind.Failed, Reason: "cannot decode")),
        Ep(5, english, null, cancelled: true),
        Ep(6, null, null, cancelled: true),
      };
      var stdout = new StringWriter();

      SeasonCommand.PrintReport(episodes, CliOptions.Parse(new[] { "season", dir, "--project", "p" }), 3, stdout);

      Assert.Equal(new[]
      {
        "Episode          EN track            Retime",
        "[Grp] 番組 - 01  3 \"English\" 312 ev  kept",
        "[Grp] 番組 - 02  3 \"English\" 305 ev  not saved",
        "[Grp] 番組 - 03  no subtitle track   no EN track",
        "[Grp] 番組 - 04  4 \"Full\" 400 ev     failed: cannot decode",
        "[Grp] 番組 - 05  3 \"English\" 312 ev  cancelled",
        "[Grp] 番組 - 06  cancelled           cancelled",
        "note: the EN track differs between episodes: 3 \"English\" in 3 episodes ([Grp] 番組 - 01, [Grp] 番組 - 02, [Grp] 番組 - 05), "
          + "4 \"Full\" in 1 episode ([Grp] 番組 - 04); --track <id> extracts the same one from all.",
        "retimed JP files: 1 of 6 episodes; exit 3",
        "align by hand, then run again:",
        "  subsretimer --output 'a b' 'c' 'd'",
      }, stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
      // With --track the ids agree and the names may not; the note stays, without the advice.
      Assert.EndsWith("in 1 episode ([Grp] 番組 - 04).", SeasonCommand.TrackNote(episodes, trackGiven: true));
      Assert.Null(SeasonCommand.TrackNote(episodes.Take(2), trackGiven: false));
    }

    [Fact]
    public void Table_OnlyExtract_ShowsTheEnFile_AndOneTrack_NoNote()
    {
      string dir = Path.Combine(Path.GetTempPath(), "Show");
      var english = new MkvTrackPick { Track = Track(3, "English", 312) };
      SeasonEpisode Ep(int n, MkvTrackPick pick, MkvExtractStatus? status, string? reason = null) =>
        new SeasonEpisode(Path.Combine(dir, $"Ep {n}.mkv"))
        {
          Pick = pick,
          Extraction = status == null ? null : new MkvExtractResult { Status = status.Value, Reason = reason },
          En = status is MkvExtractStatus.Extracted or MkvExtractStatus.Kept ? FoundFile.Of("x") : FoundFile.Missing("y"),
        };
      var episodes = new List<SeasonEpisode>
      {
        Ep(1, english, MkvExtractStatus.Extracted),
        Ep(2, english, MkvExtractStatus.Kept),
        Ep(3, english, MkvExtractStatus.Failed, "mkvextract exited with code 2: boom"),
        Ep(4, MkvTrackPick.None("no subtitle track"), null),
      };
      var stdout = new StringWriter();

      SeasonCommand.PrintReport(episodes, CliOptions.Parse(new[] { "season", dir, "--project", "p", "--only", "extract" }), 3, stdout);

      Assert.Equal(new[]
      {
        "Episode  EN track            EN file",
        "Ep 1     3 \"English\" 312 ev  extracted",
        "Ep 2     3 \"English\" 312 ev  kept",
        "Ep 3     3 \"English\" 312 ev  failed: mkvextract exited with code 2: boom",
        "Ep 4     no subtitle track   -",
        "EN files: 2 of 4 episodes; exit 3",
      }, stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    // ── errors before any work ──────────────────────────────────────────

    [Fact]
    public async Task NoFolder_NoProject_NoVideos_OrNoMkvToolNix_Exit1_BeforeAnyWork()
    {
      FakeSubsretimer();
      string project = Project();
      string dir = Path.Combine(scope.TempDir, "Show S1");
      Directory.CreateDirectory(dir);
      Write(Path.Combine(dir, "Show - 01.srt"), "1\n00:00:01,000 --> 00:00:02,000\nあ\n");
      List<ProcessStartInfo> started = ScriptRetimer();

      CliTests.Result noFolder = await CliTests.Run("season", Path.Combine(scope.TempDir, "missing"), "--project", project);
      Assert.Equal(1, noFolder.Code);
      Assert.Contains("season folder not found", noFolder.Stderr);
      CliTests.Result noProject = await CliTests.Run("season", dir, "--project", Path.Combine(scope.TempDir, "none.s2s.json"));
      Assert.Equal(1, noProject.Code);
      Assert.Contains("project file not found", noProject.Stderr);
      CliTests.Result noVideos = await CliTests.Run("season", dir, "--project", project);
      Assert.Equal(1, noVideos.Code);
      Assert.Contains("no .mkv file in the season folder", noVideos.Stderr);

      // MKVToolNix nowhere: not in the Tools Directory, on PATH or in an install folder.
      File.WriteAllText(Path.Combine(dir, "Show - 01.mkv"), "not really an mkv");
      Write(Path.Combine(dir, "s2s", "Show - 01.en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n", AnHourAgo);
      string empty = Path.Combine(scope.TempDir, "empty");
      Directory.CreateDirectory(empty);
      Environment.SetEnvironmentVariable("PATH", empty);
      ConstantSettings.MkvToolNixDirsOverride = new[] { empty };
      CliTests.Result noTools = await CliTests.Run("season", dir, "--project", project);
      Assert.Equal(1, noTools.Code);
      Assert.Contains("subs2srs-cli: " + MkvTracks.NotFoundMessage, noTools.Stderr);
      Assert.Equal("", noTools.Stdout);
      Assert.Empty(started);
      Assert.Equal(new[] { "Show - 01.en.srt" }, S2s(dir));

      // The retime alone needs none of it.
      CliTests.Result retimeOnly = await CliTests.Run("season", dir, "--project", project, "--only", "retime");
      Assert.True(retimeOnly.Code == 0, retimeOnly.ToString());
      Assert.Single(started);
    }

    // ── --only retime, and a cancel (no mkvmerge needed) ────────────────

    /// <summary>Three episodes as an extraction leaves them: fake videos, EN files in s2s, JP files beside.</summary>
    private string ExtractedSeason()
    {
      string dir = Path.Combine(scope.TempDir, "Show S1 [Grp] 日本");
      Directory.CreateDirectory(dir);
      for (int n = 1; n <= 3; n++)
      {
        File.WriteAllText(Path.Combine(dir, Name(n) + ".mkv"), "not really an mkv");
        Write(Path.Combine(dir, "s2s", Name(n) + ".en.ass"), "[Script Info]\n", AnHourAgo);
        Write(Path.Combine(dir, Name(n) + ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nこんにちは\n", AnHourAgo);
      }
      return dir;
    }

    [Fact]
    public async Task OnlyRetime_TakesTheEnFilesInS2s_WithoutMkvToolNix()
    {
      string exe = FakeSubsretimer();
      string project = Project();
      string dir = ExtractedSeason();
      Write(Path.Combine(dir, "s2s", Name(3) + ".en.srt"), "1\n", AnHourAgo); // a second .en: go would skip it
      MkvTracks.RunnerOverride = (psi, ct) => throw new InvalidOperationException("mkvmerge ran");
      MkvExtract.RunnerOverride = (psi, ct) => throw new InvalidOperationException("mkvextract ran");
      List<ProcessStartInfo> started = ScriptRetimer();

      CliTests.Result r = await CliTests.Run("season", dir, "--project", project, "--only", "retime", "--min-match", "0.8");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      Assert.Equal(new[] { "Episode", "Retime" }, Regex.Split(r.Lines[0], " {2,}"));
      Assert.Equal(new[] { Name(1), "2 segments, 95% of EN covered" }, Row(r, 1));
      Assert.Equal(new[] { Name(2), "below --min-match (50%)" }, Row(r, 2));
      Assert.Equal(new[] { Name(3), $"2 .en files ({Path.Combine("s2s", Name(3) + ".en.ass")}, {Path.Combine("s2s", Name(3) + ".en.srt")})" },
        Row(r, 3));
      Assert.Contains("retimed JP files: 1 of 3 episodes; exit 3", r.Lines);
      Assert.Equal(2, started.Count);
      Assert.Equal("0.8", Arg(started[0], "--min-match"));
      // Both .en files stay: --only retime extracts nothing, so it does not tell which one is right.
      Assert.Contains(Name(3) + ".en.srt", S2s(dir));
      var pair = new SubsRetimerLauncher.Request(Path.Combine(dir, "s2s", Name(2) + ".en.ass"), Path.Combine(dir, Name(2) + ".srt"),
        "utf-8", "utf-8", false, Path.Combine(dir, "s2s", Name(2) + ".ja.srt"));
      Assert.Equal(new[] { "align by hand, then run again:", "  " + SubsRetimerLauncher.EditorCommand(RetimeStage.CommandName(exe), pair) },
        r.Lines.SkipWhile(l => !l.StartsWith("align by hand", StringComparison.Ordinal)));
      Assert.Contains($"[1/3] {Name(1)}: retime: 2 segments, 95% of EN covered", r.Stderr);
    }

    [Fact]
    public async Task Cancel_StopsTheRun_Exits130_TheTableSaysWhichEpisodesWereNotDone()
    {
      FakeSubsretimer();
      string project = Project();
      string dir = ExtractedSeason();
      using var cts = new CancellationTokenSource();
      List<ProcessStartInfo> started = ScriptRetimer(run => { if (run == 2) cts.Cancel(); });
      var stdout = new StringWriter();
      var stderr = new StringWriter();

      int code = await CliRunner.RunAsync(new[] { "season", dir, "--project", project, "--only", "retime" }, stdout, stderr, cts.Token);

      Assert.Equal(CliOptions.ExitCancelled, code);
      Assert.Equal(2, started.Count);
      string[] lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
      Assert.EndsWith("  2 segments, 95% of EN covered", lines[1]);
      Assert.EndsWith("  cancelled", lines[2]);
      Assert.EndsWith("  cancelled", lines[3]);
      Assert.Equal("retimed JP files: 1 of 3 episodes; exit 130", lines[4]);
      Assert.Contains("subs2srs-cli: cancelled.", stderr.ToString());
      Assert.Equal(new[] { Name(1) + ".en.ass", Name(1) + ".ja.srt", Name(1) + ".retime.json", Name(2) + ".en.ass", Name(3) + ".en.ass" },
        S2s(dir));
    }

    // ── a generated season: real mkvmerge and mkvextract ────────────────

    /// <summary>
    /// Four real mkv files muxed from generated subtitles: an English ASS track (3 events) and a
    /// Japanese one; episode 4 also has an English SRT track "Full" with 5 events, which the pick
    /// prefers. JP files beside: episode 1 <c>&lt;name&gt;.srt</c>, 2 <c>.ja.srt</c> (the scripted
    /// subsretimer saves nothing for it), 3 none, 4 <c>.ja.ass</c>. Every file an hour old.
    /// </summary>
    private string GeneratedSeason()
    {
      string dir = Path.Combine(scope.TempDir, "Show S1 [Grp] 日本");
      string src = Path.Combine(scope.TempDir, "src");
      Directory.CreateDirectory(dir);
      string ass = Write(Path.Combine(src, "en.ass"), MkvTracksTests.Ass(3, "English line"));
      string srt = Write(Path.Combine(src, "full.srt"), MkvTracksTests.Srt(5, "English full line"));
      string ja = Write(Path.Combine(src, "ja.srt"), MkvTracksTests.Srt(4, "日本語の台詞"));
      for (int n = 1; n <= 4; n++)
      {
        var inputs = new List<string> { "--language", "0:eng", "--track-name", "0:English", ass };
        if (n == 4) inputs.AddRange(new[] { "--language", "0:eng", "--track-name", "0:Full", srt });
        inputs.AddRange(new[] { "--language", "0:jpn", "--track-name", "0:日本語", ja });
        string made = Path.Combine(src, "made.mkv");
        MkvTracksTests.Mux(made, inputs.ToArray());
        File.Move(made, Path.Combine(dir, Name(n) + ".mkv"));
      }
      Write(Path.Combine(dir, Name(1) + ".srt"), MkvTracksTests.Srt(4, "日本語"));
      Write(Path.Combine(dir, Name(2) + ".ja.srt"), MkvTracksTests.Srt(4, "日本語"));
      Write(Path.Combine(dir, Name(4) + ".ja.ass"), MkvTracksTests.Ass(4, "日本語"));
      SetBack(dir);
      return dir;
    }

    /// <summary>
    /// Every file under <paramref name="dir"/> written an hour ago, the retimes in <c>s2s</c> ten
    /// minutes later, so each is still newer than its files, as a run leaves them; a file made
    /// from now on is newer than all of them, whatever the file system's time resolution.
    /// </summary>
    private static void SetBack(string dir)
    {
      foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
      {
        bool retime = Path.GetFileName(Path.GetDirectoryName(f)) == "s2s" && Path.GetFileName(f).Contains(".ja.", StringComparison.Ordinal);
        File.SetLastWriteTimeUtc(f, retime ? AnHourAgo.AddMinutes(10) : AnHourAgo);
      }
    }

    [RequiresMkvToolnixFact]
    public async Task GeneratedSeason_ExtractsAndRetimes_SkipsWhatItCannot_AndPrintsTheEditorCommand()
    {
      string exe = FakeSubsretimer();
      string project = Project();
      string dir = GeneratedSeason();
      List<ProcessStartInfo> started = ScriptRetimer();

      CliTests.Result r = await CliTests.Run("season", dir, "--project", project, "--min-match", "0.8");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      Assert.Equal(new[] { "Episode", "EN track", "Retime" }, Regex.Split(r.Lines[0], " {2,}"));
      Assert.Equal(new[] { Name(1), "0 \"English\" 3 ev", "2 segments, 95% of EN covered" }, Row(r, 1));
      Assert.Equal(new[] { Name(2), "0 \"English\" 3 ev", "below --min-match (50%)" }, Row(r, 2));
      Assert.Equal(new[] { Name(3), "0 \"English\" 3 ev", "no JP file named like the video" }, Row(r, 3));
      Assert.Equal(new[] { Name(4), "1 \"Full\" 5 ev", "2 segments, 95% of EN covered" }, Row(r, 4));
      Assert.Contains($"note: the EN track differs between episodes: 0 \"English\" in 3 episodes ({Name(1)}, {Name(2)}, {Name(3)}), "
        + $"1 \"Full\" in 1 episode ({Name(4)})", r.Stdout);
      Assert.Contains("retimed JP files: 2 of 4 episodes; exit 3", r.Lines);
      var pair = new SubsRetimerLauncher.Request(Path.Combine(dir, "s2s", Name(2) + ".en.ass"), Path.Combine(dir, Name(2) + ".ja.srt"),
        "utf-8", "utf-8", false, Path.Combine(dir, "s2s", Name(2) + ".ja.srt"));
      Assert.Equal(new[] { "align by hand, then run again:", "  " + SubsRetimerLauncher.EditorCommand(RetimeStage.CommandName(exe), pair) },
        r.Lines.SkipWhile(l => !l.StartsWith("align by hand", StringComparison.Ordinal)));
      Assert.Contains($"[1/4] {Name(1)}: EN track 0 \"English\" 3 ev: extracted", r.Stderr);
      Assert.Contains($"[2/4] {Name(2)}: retime: below --min-match (50%)", r.Stderr);

      // The EN tracks extracted for every episode, the one without a JP file too; a .ja file only where saved.
      Assert.Equal(new[]
      {
        Name(1) + ".en.ass", Name(1) + ".ja.srt", Name(1) + ".retime.json", Name(2) + ".en.ass", Name(2) + ".retime.json",
        Name(3) + ".en.ass", Name(4) + ".en.srt", Name(4) + ".ja.ass", Name(4) + ".retime.json",
      }, S2s(dir));
      Assert.Contains("English line 1.", File.ReadAllText(Path.Combine(dir, "s2s", Name(1) + ".en.ass")));
      Assert.Contains("English full line 5.", File.ReadAllText(Path.Combine(dir, "s2s", Name(4) + ".en.srt")));
      // subsretimer on each pair with a JP file: EN as reference in UTF-8, JP in the project's Subs1 encoding.
      Assert.Equal(new[] { Name(1) + ".srt", Name(2) + ".ja.srt", Name(4) + ".ja.ass" },
        started.Select(p => Path.GetFileName(p.ArgumentList[^1])));
      Assert.All(started, p => Assert.Equal("0.8", Arg(p, "--min-match")));
      Assert.Equal(Path.Combine(dir, "s2s", Name(4) + ".en.srt"), started[2].ArgumentList[^2]);

      // go --season takes episodes 1 and 4 and skips the others.
      EpisodeList list = EpisodeList.ForSeason(dir, EpisodeList.SeasonVideos(dir), S2s(dir), 1, 0);
      Assert.Equal(new[] { false, true, true, false }, list.Episodes.Select(e => e.Skipped));
    }

    [RequiresMkvToolnixFact]
    public async Task GeneratedSeason_RunAgain_KeepsEverything_Force_RedoesEverything()
    {
      FakeSubsretimer();
      string project = Project();
      string dir = GeneratedSeason();
      ScriptRetimer();
      await CliTests.Run("season", dir, "--project", project);
      SetBack(dir); // the first run's files are older now, but each retime still newer than its EN and JP file
      string fixedByHand = Write(Path.Combine(dir, "s2s", Name(1) + ".ja.srt"), "fixed in the editor", DateTime.UtcNow.AddMinutes(-1));
      string[] before = Snapshot(dir);
      List<ProcessStartInfo> started = ScriptRetimer();

      CliTests.Result again = await CliTests.Run("season", dir, "--project", project);

      Assert.True(again.Code == CliOptions.ExitSkipped, again.ToString());
      Assert.Equal("kept", Row(again, 1)[^1]);
      Assert.Equal("kept", Row(again, 4)[^1]);
      Assert.Contains($"[3/4] {Name(3)}: EN track 0 \"English\" 3 ev: kept", again.Stderr);
      // Only episode 2, which has no retime, is asked again (and saves nothing again).
      Assert.Equal(Name(2) + ".ja.srt", Path.GetFileName(Assert.Single(started).ArgumentList[^1]));
      Assert.Equal(before.Where(l => !l.StartsWith(Path.Combine("s2s", Name(2) + ".retime.json"), StringComparison.Ordinal)),
        Snapshot(dir).Where(l => !l.StartsWith(Path.Combine("s2s", Name(2) + ".retime.json"), StringComparison.Ordinal)));
      Assert.Equal("fixed in the editor", File.ReadAllText(fixedByHand));

      started = ScriptRetimer();
      CliTests.Result forced = await CliTests.Run("season", dir, "--project", project, "--force");

      Assert.True(forced.Code == CliOptions.ExitSkipped, forced.ToString());
      Assert.Contains($"[1/4] {Name(1)}: EN track 0 \"English\" 3 ev: extracted", forced.Stderr);
      Assert.Equal(3, started.Count);
      Assert.Equal("retimed " + Name(1) + ".srt", File.ReadAllText(fixedByHand));
      Assert.All(new[] { 1, 2, 3 }, n =>
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(dir, "s2s", Name(n) + ".en.ass")) > AnHourAgo.AddMinutes(30)));
    }

    [RequiresMkvToolnixFact]
    public async Task GeneratedSeason_Track_ExtractsThatTrack_AndDeletesTheOtherEnFile()
    {
      FakeSubsretimer();
      string project = Project();
      string dir = GeneratedSeason();
      ScriptRetimer();
      await CliTests.Run("season", dir, "--project", project);
      SetBack(dir);
      List<ProcessStartInfo> started = ScriptRetimer();

      CliTests.Result r = await CliTests.Run("season", dir, "--project", project, "--track", "0");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      Assert.Equal(new[] { Name(4), "0 \"English\" 3 ev", "2 segments, 95% of EN covered" }, Row(r, 4));
      Assert.DoesNotContain("note:", r.Stdout);
      // Episode 4's earlier extraction of track 1 is gone (go would skip an episode with two .en files),
      // and its retime is done again on the new EN file.
      Assert.Contains(Name(4) + ".en.ass", S2s(dir));
      Assert.DoesNotContain(Name(4) + ".en.srt", S2s(dir));
      Assert.Equal(new[] { Name(2) + ".ja.srt", Name(4) + ".ja.ass" }, started.Select(p => Path.GetFileName(p.ArgumentList[^1])));
      Assert.Equal(Path.Combine(dir, "s2s", Name(4) + ".en.ass"), started[1].ArgumentList[^2]);

      CliTests.Result missing = await CliTests.Run("season", dir, "--project", project, "--track", "7");
      Assert.Equal(new[] { Name(1), "no track 7 in the file", "no EN track" }, Row(missing, 1));
      Assert.True(File.Exists(Path.Combine(dir, "s2s", Name(1) + ".en.ass"))); // no pick: nothing deleted
    }

    [RequiresMkvToolnixFact]
    public async Task GeneratedSeason_DryRun_ShowsThePlan_AndWritesAndDeletesNothing()
    {
      FakeSubsretimer();
      string project = Project();
      string dir = GeneratedSeason();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) => throw new InvalidOperationException("subsretimer ran");
      MkvExtract.RunnerOverride = (psi, ct) => throw new InvalidOperationException("mkvextract ran");
      string[] before = Snapshot(dir);

      CliTests.Result fresh = await CliTests.Run("season", dir, "--project", project, "--dry-run");

      Assert.True(fresh.Code == CliOptions.ExitSkipped, fresh.ToString());
      Assert.Equal(new[] { "Episode", "EN track", "EN file", "JP file", "Retime" }, Regex.Split(fresh.Lines[0], " {2,}"));
      Assert.Equal(new[] { Name(1), "0 \"English\" 3 ev", "to extract", Name(1) + ".srt", "to retime" }, Row(fresh, 1));
      Assert.Equal(new[] { Name(2), "0 \"English\" 3 ev", "to extract", Name(2) + ".ja.srt", "to retime" }, Row(fresh, 2));
      Assert.Equal(new[] { Name(3), "0 \"English\" 3 ev", "to extract", "no JP file named like the video", "no JP file named like the video" },
        Row(fresh, 3));
      Assert.Equal(new[] { Name(4), "1 \"Full\" 5 ev", "to extract", Name(4) + ".ja.ass", "to retime" }, Row(fresh, 4));
      Assert.Contains("note: the EN track differs between episodes", fresh.Stdout);
      Assert.DoesNotContain("exit", fresh.Stdout);
      Assert.Contains("Dry run: nothing was extracted, retimed or deleted.", fresh.Stderr);
      Assert.Equal(before, Snapshot(dir));

      // After a run: what is there and would be kept, and what a run would delete.
      SubsRetimerLauncher.RunnerOverride = null;
      MkvExtract.RunnerOverride = null;
      ScriptRetimer();
      await CliTests.Run("season", dir, "--project", project);
      SetBack(dir);
      Write(Path.Combine(dir, "s2s", Name(1) + ".ja.srt"), "fixed in the editor", DateTime.UtcNow);
      Write(Path.Combine(dir, "s2s", Name(1) + ".en.srt"), "an extraction of another track", AnHourAgo);
      SubsRetimerLauncher.RunnerOverride = (psi, ct) => throw new InvalidOperationException("subsretimer ran");
      MkvExtract.RunnerOverride = (psi, ct) => throw new InvalidOperationException("mkvextract ran");
      before = Snapshot(dir);

      CliTests.Result after = await CliTests.Run("season", dir, "--project", project, "--dry-run");
      CliTests.Result forced = await CliTests.Run("season", dir, "--project", project, "--dry-run", "--force");

      Assert.True(after.Code == CliOptions.ExitSkipped, after.ToString());
      Assert.Equal(new[] { Name(1), "0 \"English\" 3 ev", "kept; deletes " + Name(1) + ".en.srt", Name(1) + ".srt", "kept" }, Row(after, 1));
      Assert.Equal(new[] { Name(2), "0 \"English\" 3 ev", "kept", Name(2) + ".ja.srt", "to retime" }, Row(after, 2)); // no retime there
      Assert.Equal(new[] { Name(4), "1 \"Full\" 5 ev", "kept", Name(4) + ".ja.ass", "kept" }, Row(after, 4));
      Assert.Equal(new[] { Name(4), "1 \"Full\" 5 ev", "to extract again", Name(4) + ".ja.ass", "to retime" }, Row(forced, 4));
      Assert.Equal("to extract again; deletes " + Name(1) + ".en.srt", Row(forced, 1)[2]);
      Assert.Equal(before, Snapshot(dir));
    }
  }
}
