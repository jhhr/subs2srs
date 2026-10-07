//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The season's retime stage (<see cref="RetimeStage"/>): the JP file found by name (pure), the
  /// EN file in <c>s2s</c>, one episode retimed on a scripted subsretimer (keep-if-newer, force,
  /// the stale and sibling <c>.ja</c> files deleted, each outcome and its table text, the editor
  /// command), and with <c>SUBSRETIMER_EXE</c> set, the real tool on a two-episode season.
  /// </summary>
  public class RetimeStageTests : IDisposable
  {
    private readonly TestScope scope = new TestScope();

    public void Dispose()
    {
      SubsRetimerLauncher.RunnerOverride = null;
      scope.Dispose();
    }

    private static readonly string Season = Path.Combine(Path.GetTempPath(), "Show S1 [Grp] 日本");

    private static string[] Videos(params string[] names) => names.Select(n => Path.Combine(Season, n + ".mkv")).ToArray();

    // ── the JP file (pure) ───────────────────────────────────────────────

    [Theory]
    [InlineData("Show - 01.srt", true)]
    [InlineData("Show - 01.ja.srt", true)]
    [InlineData("show - 01.JA.ASS", true)]
    [InlineData("Show - 01.jpn.ssa", true)]
    [InlineData("Show - 01.en.srt", false)] // the English file beside the video
    [InlineData("Show - 01.ENG.ass", false)]
    [InlineData("Show - 01.ja.cc.srt", true)] // a tag of several words: closed captions
    [InlineData("Show - 01.JPN.SDH.ass", true)]
    [InlineData("Show - 01.en.sdh.srt", false)] // English when any word is
    [InlineData("Show - 01.cc.En.srt", false)]
    [InlineData("Show - 01..srt", false)] // an empty word
    [InlineData("Show - 01.ja..srt", false)]
    [InlineData("Show - 01.ja.cc..srt", false)]
    [InlineData("Show - 01.ja.txt", false)]
    [InlineData("Show - 01.ja.srt.bak", false)]
    [InlineData("Show - 01.mkv", false)]
    [InlineData("Show - 01 - Track 03 - English.srt", false)] // an older extract
    [InlineData("Show - 010.srt", false)]
    [InlineData("Show - 0.srt", false)]
    public void IsJpFileOf_TheVideosName_OrItAndATag_NoWordEnglish(string fileName, bool expected)
      => Assert.Equal(expected, RetimeStage.IsJpFileOf(fileName, "Show - 01"));

    [Fact]
    public void FindJp_DottedNames_NamesThatStartAnother_AndBrackets_EachFindOnlyTheirOwn()
    {
      string[] videos = Videos("Show.S01E01.1080p", "Ep 1", "Ep 10", "Movie", "Movie.Extended", "[Grp] 第1話 (1080p) [AB12]", "It's 1");
      string[] names =
      {
        "Ep 1.srt", "Ep 10.ja.ass", "It's 1.srt", "Movie.Extended.ja.cc.srt", "Movie.ja.srt", "Show.S01E01.1080p.en.srt",
        "Show.S01E01.1080p.ja.srt", "Show.S01E01.srt", "[Grp] 第1話 (1080p) [AB12].jpn.srt", "[Grp] 第1話 (1080p) [AB12].en.ass",
      };

      FoundFile[] found = RetimeStage.FindJpFiles(Season, videos, names);

      Assert.Equal(new[]
        {
          "Show.S01E01.1080p.ja.srt", "Ep 1.srt", "Ep 10.ja.ass", "Movie.ja.srt", "Movie.Extended.ja.cc.srt",
          "[Grp] 第1話 (1080p) [AB12].jpn.srt", "It's 1.srt",
        }.Select(n => Path.Combine(Season, n)),
        found.Select(f => f.Path));
      Assert.All(found, f => Assert.Null(f.Problem));
    }

    [Fact]
    public void FindJp_NoneOrSeveral_SayWhy_NamingTheFiles()
    {
      FoundFile[] found = RetimeStage.FindJpFiles(Season, Videos("A", "B"), new[] { "A.en.srt", "B.ja.srt", "B.srt" });

      Assert.Equal(FoundFile.Missing("no JP file named like the video"), found[0]);
      Assert.Equal(FoundFile.Missing("2 JP files (B.ja.srt, B.srt)"), found[1]);
    }

    [Fact]
    public void FindEn_TheOneEnFileInS2s_OrGosOwnReason()
    {
      FoundFile[] found = RetimeStage.FindEnFiles(Season, Videos("A", "B", "C"),
        new[] { "A.en.ass", "A.ja.srt", "B.ja.srt", "C.en.srt", "C.EN.ass", "B - Track 3 - English.srt" });

      Assert.Equal(FoundFile.Of(Path.Combine(Season, "s2s", "A.en.ass")), found[0]);
      Assert.Equal(FoundFile.Missing("no .en file"), found[1]);
      Assert.Equal(FoundFile.Missing($"2 .en files ({Path.Combine("s2s", "C.en.srt")}, {Path.Combine("s2s", "C.EN.ass")})"), found[2]);
    }

    // ── one episode, on a scripted subsretimer ───────────────────────────

    private static readonly DateTime T0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Exe = "/opt/sub retimer/subsretimer";

    private sealed record Ep(string Dir, string Video, FoundFile Jp, FoundFile En)
    {
      public string S2s(string name) => Path.Combine(Dir, "s2s", name);
      public string Output => S2s("番組 [01].ja.srt");
    }

    /// <summary>
    /// A season folder holding one episode: a (fake) video, its JP file <c>番組 [01].srt</c> beside
    /// it and its EN extract in <c>s2s</c>, both written at <see cref="T0"/>; found as the stage finds them.
    /// </summary>
    private Ep Episode()
    {
      string dir = Path.Combine(scope.TempDir, "シーズン 1 [Grp]");
      Directory.CreateDirectory(Path.Combine(dir, "s2s"));
      string video = Path.Combine(dir, "番組 [01].mkv");
      File.WriteAllText(video, "not really an mkv");
      Write(Path.Combine(dir, "番組 [01].srt"), "1\n00:00:01,000 --> 00:00:02,000\nこんにちは\n", T0);
      Write(Path.Combine(dir, "s2s", "番組 [01].en.ass"), "[Script Info]\n", T0);
      string[] videos = { video };
      return new Ep(dir, video, RetimeStage.FindJpFiles(dir, videos)[0], RetimeStage.FindEnFiles(dir, videos)[0]);
    }

    private static string Write(string path, string text, DateTime time)
    {
      File.WriteAllText(path, text);
      File.SetLastWriteTimeUtc(path, time);
      return path;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "retime", name);

    private static string Arg(ProcessStartInfo psi, string option) => psi.ArgumentList[psi.ArgumentList.IndexOf(option) + 1];

    /// <summary>What the scripted subsretimer was started with, and the <c>s2s</c> folder's files at that moment.</summary>
    private sealed record Started(ProcessStartInfo Psi, string[] S2sFiles);

    /// <summary>
    /// A runner that records each start and, as subsretimer would, writes <c>--output</c> on
    /// exit 0, copies the report fixture (when given) to <c>--report</c>, and returns the result.
    /// </summary>
    private static List<Started> Script(int exitCode, string? reportFixture, string stderr = "")
    {
      var started = new List<Started>();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) =>
      {
        string output = Arg(psi, "--output");
        started.Add(new Started(psi, Directory.GetFiles(Path.GetDirectoryName(output)!).Select(f => Path.GetFileName(f)).OrderBy(n => n).ToArray()!));
        if (exitCode == 0) File.WriteAllText(output, "1\n00:00:31,000 --> 00:00:32,000\nこんにちは\n");
        if (reportFixture != null) File.Copy(Fixture(reportFixture), Arg(psi, "--report"), overwrite: true);
        return Task.FromResult(new CliProcessResult { ExitCode = exitCode, Stdout = exitCode == 0 ? output + "\n" : "", Stderr = stderr });
      };
      return started;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.8)]
    public async Task Retime_RunsSubsretimer_AsThePlanSays_AndReadsItsReport(double? minMatch)
    {
      Ep ep = Episode();
      Write(ep.S2s("番組 [01].retime.json"), "{}", T0); // an earlier run's
      List<Started> started = Script(0, "saved.retime.json");

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(Exe, "shift_jis", minMatch));

      Assert.Equal(RetimeKind.Retimed, outcome.Kind);
      Assert.True(outcome.Ready);
      Assert.Equal(ep.Output, outcome.OutputPath);
      Assert.True(File.Exists(ep.Output));
      Assert.Equal("2 segments, 95% of EN covered", outcome.Column);
      Assert.Null(outcome.EditorCommand);

      Started run = Assert.Single(started);
      Assert.Equal(Exe, run.Psi.FileName);
      var expected = new List<string> { "--print-output", "--auto", "--output", ep.Output };
      if (minMatch != null) expected.AddRange(new[] { "--min-match", "0.8" });
      expected.AddRange(new[]
      {
        "--report", ep.S2s("番組 [01].retime.json"), "--ref-encoding", "utf-8", "--target-encoding", "shift_jis",
        "--", ep.S2s("番組 [01].en.ass"), Path.Combine(ep.Dir, "番組 [01].srt"),
      });
      Assert.Equal(expected, run.Psi.ArgumentList);
      // The earlier report went before the run, so the one read is this run's.
      Assert.Equal(new[] { "番組 [01].en.ass" }, run.S2sFiles);
    }

    [Fact]
    public async Task Retime_ARetimeNewerThanBoth_IsKept_WithoutSubsretimer_AndAnotherJaFileOfTheEpisodeDeleted()
    {
      Ep ep = Episode();
      string fix = Write(ep.Output, "fixed in the editor", T0.AddMinutes(1));
      string other = Write(ep.S2s("番組 [01].ja.ass"), "from an earlier JP file", T0.AddMinutes(1));
      string anotherEpisode = Write(ep.S2s("番組 [010].ja.srt"), "episode 10", T0);
      List<Started> started = Script(0, "saved.retime.json");

      // Exe null: a re-run where every retime is kept needs no subsretimer.
      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(null, "utf-8"));

      Assert.Equal(new RetimeOutcome(RetimeKind.Kept, ep.Output), outcome);
      Assert.Equal("kept", outcome.Column);
      Assert.Empty(started);
      Assert.Equal("fixed in the editor", File.ReadAllText(fix));
      Assert.Equal(T0.AddMinutes(1), File.GetLastWriteTimeUtc(fix));
      Assert.False(File.Exists(other)); // go skips an episode with two .ja files
      Assert.True(File.Exists(anotherEpisode));
    }

    [Theory]
    [InlineData("jp newer")]
    [InlineData("en newer")]
    [InlineData("same time")]
    [InlineData("empty")]
    [InlineData("force")]
    public async Task Retime_ARetimeNotNewerThanBoth_OrForced_IsDeleted_AndDoneAgain(string which)
    {
      Ep ep = Episode();
      Write(ep.Output, which == "empty" ? "" : "an earlier retime", which == "same time" ? T0 : T0.AddMinutes(1));
      Write(ep.S2s("番組 [01].ja.ass"), "from an earlier JP file", T0.AddMinutes(1));
      if (which == "jp newer") File.SetLastWriteTimeUtc(ep.Jp.Path!, T0.AddMinutes(2));
      if (which == "en newer") File.SetLastWriteTimeUtc(ep.En.Path!, T0.AddMinutes(2));
      List<Started> started = Script(0, "saved.retime.json");

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En,
        new RetimeOptions(Exe, "utf-8", Force: which == "force"));

      Assert.Equal(RetimeKind.Retimed, outcome.Kind);
      Assert.Equal(new[] { "番組 [01].en.ass" }, Assert.Single(started).S2sFiles);
      Assert.Contains("00:00:31,000", File.ReadAllText(ep.Output));
      Assert.False(File.Exists(ep.S2s("番組 [01].ja.ass")));
    }

    [Theory]
    [InlineData("below-min-match.retime.json", "BelowMinMatch", "below --min-match (50%)")]
    [InlineData("no-timed-lines.retime.json", "NoTimedLines", "no timed lines")]
    [InlineData(null, "NotSaved", "not saved")]
    public async Task Retime_ExitTwo_GivesTheEditorCommand_AndLeavesNoJaFile(string? report, string kind, string column)
    {
      Ep ep = Episode();
      Write(ep.Output, "an earlier retime", T0.AddMinutes(-1));
      Script(2, report, "subsretimer: reference covered 50% is below --min-match 0.8; nothing saved\n");

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(Exe, "shift_jis", 0.8));

      Assert.Equal(Enum.Parse<RetimeKind>(kind), outcome.Kind);
      Assert.Equal(column, outcome.Column);
      Assert.False(outcome.Ready);
      Assert.Null(outcome.OutputPath);
      Assert.False(File.Exists(ep.Output));
      // The editor on the same pair, saving where the next run looks: full paths, the full exe (not on PATH).
      var pair = new SubsRetimerLauncher.Request(ep.S2s("番組 [01].en.ass"), Path.Combine(ep.Dir, "番組 [01].srt"),
        "utf-8", "shift_jis", false, ep.Output);
      Assert.Equal(SubsRetimerLauncher.EditorCommand(Exe, pair), outcome.EditorCommand);
    }

    [Theory]
    [InlineData(1, "subsretimer: Target file is not valid utf-8 text: /a/b.srt; pass --target-encoding with its encoding\n",
      "failed: Target file is not valid utf-8 text: /a/b.srt; pass --target-encoding with its encoding")]
    [InlineData(1, "subsretimer: Unknown option: --min-match\nUsage: subsretimer [options] ...\n  --auto  ...\n",
      "failed: Unknown option: --min-match")]
    [InlineData(134, "Unhandled exception. System.Exception: boom\n   at Main()\n",
      "failed: subsretimer exited with code 134: Unhandled exception. System.Exception: boom")]
    [InlineData(1, "", "failed: subsretimer exited with code 1")]
    public async Task Retime_ExitOne_OrAnotherCode_FailsWithTheToolsMessage_AndNoEditorCommand(int exitCode, string stderr, string column)
    {
      Ep ep = Episode();
      Write(ep.Output, "an earlier retime", T0.AddMinutes(-1));
      Script(exitCode, null, stderr);

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(Exe, "utf-8"));

      Assert.Equal(RetimeKind.Failed, outcome.Kind);
      Assert.Equal(column, outcome.Column);
      Assert.Null(outcome.EditorCommand); // the editor refuses the same file (design 9)
      Assert.False(File.Exists(ep.Output));
    }

    [Fact]
    public async Task Retime_SubsretimerNotFound_Fails_AfterDeletingTheStaleRetime()
    {
      Ep ep = Episode();
      Write(ep.Output, "an earlier retime", T0.AddMinutes(-1));
      List<Started> started = Script(0, "saved.retime.json");

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(null, "utf-8"));

      Assert.Equal(RetimeKind.Failed, outcome.Kind);
      Assert.Equal("failed: " + RetimeStage.NotFoundMessage, outcome.Column);
      Assert.Empty(started);
      Assert.False(File.Exists(ep.Output));
    }

    [Fact]
    public async Task Retime_RealLauncher_ExeThatDoesNotStart_IsAFailureWithItsMessage()
    {
      Ep ep = Episode();

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En,
        new RetimeOptions(Path.Combine(scope.TempDir, "no such subsretimer"), "utf-8"));

      Assert.Equal(RetimeKind.Failed, outcome.Kind);
      Assert.StartsWith("Could not run ", outcome.Reason);
    }

    [Theory]
    [InlineData("no JP", "no JP file named like the video")]
    [InlineData("second JP", "2 JP files (番組 [01].ja.cc.srt, 番組 [01].srt)")]
    [InlineData("no EN", "no .en file")]
    [InlineData("second EN", "2 .en files (s2s/番組 [01].en.ass, s2s/番組 [01].en.srt)")]
    public async Task Retime_NoJpOrNoEnFile_LeavesAnEditorFixAlone_NotReady_AndRunsNothing(string problem, string column)
    {
      Ep ep = Episode();
      string fix = Write(ep.Output, "fixed in the editor", T0.AddMinutes(1));
      if (problem == "no JP") File.Delete(ep.Jp.Path!);
      if (problem == "second JP") Write(Path.Combine(ep.Dir, "番組 [01].ja.cc.srt"), "1\n", T0);
      if (problem == "no EN") File.Delete(ep.En.Path!);
      if (problem == "second EN") Write(ep.S2s("番組 [01].en.srt"), "1\n", T0);
      string[] videos = { ep.Video };
      FoundFile jp = RetimeStage.FindJpFiles(ep.Dir, videos)[0];
      FoundFile en = RetimeStage.FindEnFiles(ep.Dir, videos)[0];
      List<Started> started = Script(0, "saved.retime.json");

      RetimeOutcome outcome = await RetimeStage.RetimeAsync(ep.Dir, ep.Video, jp, en, new RetimeOptions(Exe, "utf-8"));

      Assert.Equal(problem.EndsWith("JP") ? RetimeKind.NoJpFile : RetimeKind.NoEnFile, outcome.Kind);
      Assert.Equal(column.Replace('/', Path.DirectorySeparatorChar), outcome.Column);
      Assert.False(outcome.Ready); // so season leaves it out of go
      Assert.Null(outcome.OutputPath);
      Assert.Empty(started);
      // A lookup problem never destroys hand work.
      Assert.Equal("fixed in the editor", File.ReadAllText(fix));
      Assert.Equal(T0.AddMinutes(1), File.GetLastWriteTimeUtc(fix));
    }

    [Fact]
    public async Task Retime_Cancelled_Throws_AndLeavesNoRetime()
    {
      Ep ep = Episode();
      using var cts = new CancellationTokenSource();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) =>
      {
        File.WriteAllText(Arg(psi, "--output"), "1\n00:00:31,000 --> 00:00:3");
        cts.Cancel();
        throw new OperationCanceledException(ct);
      };

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        RetimeStage.RetimeAsync(ep.Dir, ep.Video, ep.Jp, ep.En, new RetimeOptions(Exe, "utf-8"), cts.Token));

      Assert.False(File.Exists(ep.Output));
    }

    // ── the table text, the exe's name, the options ──────────────────────

    [Fact]
    public void Column_PerOutcome()
    {
      static RetimeReport Report(int exit, int segments, double? share, string? reason = null) =>
        new(exit, exit == 0 ? "/s2s/x.ja.srt" : null, segments, share, reason);

      Assert.Equal("kept", new RetimeOutcome(RetimeKind.Kept, "/s2s/x.ja.srt").Column);
      Assert.Equal("2 segments, 97% of EN covered", new RetimeOutcome(RetimeKind.Retimed, "/s2s/x.ja.srt", Report(0, 2, 0.974)).Column);
      Assert.Equal("1 segment, 100% of EN covered", new RetimeOutcome(RetimeKind.Retimed, "/s2s/x.ja.srt", Report(0, 1, 1.0)).Column);
      Assert.Equal("3 segments", new RetimeOutcome(RetimeKind.Retimed, "/s2s/x.ja.srt", Report(0, 3, null)).Column);
      Assert.Equal("retimed", new RetimeOutcome(RetimeKind.Retimed, "/s2s/x.ja.srt").Column);
      // Cut down as subsretimer prints it: 79.9% is below a --min-match of 0.8, so not "80%".
      Assert.Equal("below --min-match (79%)", new RetimeOutcome(RetimeKind.BelowMinMatch, Report: Report(2, 2, 0.799, RetimeReport.BelowMinMatch)).Column);
      Assert.Equal("below --min-match (41%)", new RetimeOutcome(RetimeKind.BelowMinMatch, Report: Report(2, 2, 0.41, RetimeReport.BelowMinMatch)).Column);
      Assert.Equal("no timed lines", new RetimeOutcome(RetimeKind.NoTimedLines, Report: Report(2, 0, null, RetimeReport.NoTimedLines)).Column);
      Assert.Equal("not saved", new RetimeOutcome(RetimeKind.NotSaved).Column);
      Assert.Equal("failed: subsretimer not found", new RetimeOutcome(RetimeKind.Failed, Reason: "subsretimer not found").Column);
      Assert.Equal("2 JP files (a.srt, a.ja.srt)", new RetimeOutcome(RetimeKind.NoJpFile, Reason: "2 JP files (a.srt, a.ja.srt)").Column);
      Assert.Equal("no .en file", new RetimeOutcome(RetimeKind.NoEnFile, Reason: "no .en file").Column);
    }

    [Fact]
    public void CommandName_SubsretimerWhenPathFindsThatSameFile_ElseTheFullPath()
    {
      string tools = Path.Combine(scope.TempDir, "my tools");
      string exe = Path.Combine(tools, "subsretimer");

      Assert.Equal("subsretimer", RetimeStage.CommandName(exe, exe));
      Assert.Equal("subsretimer", RetimeStage.CommandName(exe, Path.Combine(tools, "..", "my tools", "subsretimer")));
      Assert.Equal(exe, RetimeStage.CommandName(exe, Path.Combine(scope.TempDir, "bin", "subsretimer")));
      Assert.Equal(exe, RetimeStage.CommandName(exe, null));
    }

    [Fact]
    public void Options_FromAProject_TheSubs1Encoding_AndSubsretimerFromTheToolsDirectory_NamedByPath()
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      string exe = Path.Combine(tools, "subsretimer");
      File.WriteAllText(exe, "");
      ConstantSettings.ToolsDir = tools;
      Settings.Instance.Subs[0].Encoding = "shift_jis";
      Settings.Instance.Subs[1].Encoding = "utf-16";

      RetimeOptions options = RetimeOptions.FromSettings(Settings.Instance, 0.8, force: true);

      Assert.Equal(new RetimeOptions(exe, "shift_jis", 0.8, true), options);
      string? path = Environment.GetEnvironmentVariable("PATH");
      try
      {
        Environment.SetEnvironmentVariable("PATH", Path.Combine(scope.TempDir, "elsewhere") + Path.PathSeparator + path);
        Assert.Equal(exe, RetimeStage.CommandName(exe));
        Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
        Assert.Equal("subsretimer", RetimeStage.CommandName(exe));
      }
      finally
      {
        Environment.SetEnvironmentVariable("PATH", path);
      }
    }

    // ── the real tool (only when SUBSRETIMER_EXE points at a build) ─────

    [RequiresEnvFact("SUBSRETIMER_EXE")]
    public async Task RealTool_TwoEpisodeSeason_OneRetimedAndKeptNextRun_OneBelowMinMatchWithItsEditorCommand()
    {
      string exe = Environment.GetEnvironmentVariable("SUBSRETIMER_EXE")!;
      Assert.True(File.Exists(exe), "SUBSRETIMER_EXE: no such file: " + exe);
      string dir = Path.Combine(scope.TempDir, "アニメ S1 [Grp]");
      string s2s = Path.Combine(dir, "s2s");
      Directory.CreateDirectory(s2s);
      string[] videos = { Path.Combine(dir, "第1話.mkv"), Path.Combine(dir, "第2話.mkv") };
      foreach (string v in videos) File.WriteAllText(v, "not really an mkv");
      // EN as extracted; JP 15 s late after its 40th line, and episode 2's JP only its first 60 lines.
      var inputs = new List<string>
      {
        SubsRetimerLauncherTests.WriteSrt(s2s, "第1話.en.srt", 0),
        SubsRetimerLauncherTests.WriteSrt(s2s, "第2話.en.srt", 0),
        SubsRetimerLauncherTests.WriteSrt(dir, "第1話.srt", 15),
        SubsRetimerLauncherTests.WriteSrt(dir, "第2話.ja.srt", 15, lines: 60),
      };
      foreach (string f in inputs) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
      var options = new RetimeOptions(exe, "utf-8", MinMatch: 0.8);

      async Task<RetimeOutcome[]> RunSeason()
      {
        FoundFile[] jp = RetimeStage.FindJpFiles(dir, videos), en = RetimeStage.FindEnFiles(dir, videos);
        var outcomes = new RetimeOutcome[videos.Length];
        for (int i = 0; i < videos.Length; i++)
          outcomes[i] = await RetimeStage.RetimeAsync(dir, videos[i], jp[i], en[i], options);
        return outcomes;
      }

      RetimeOutcome[] first = await RunSeason();

      Assert.Equal(RetimeKind.Retimed, first[0].Kind);
      Assert.Equal("2 segments, 100% of EN covered", first[0].Column);
      Assert.Equal(Path.Combine(s2s, "第1話.ja.srt"), first[0].OutputPath);
      Assert.Contains("00:", File.ReadAllText(first[0].OutputPath!));
      Assert.Equal(RetimeKind.BelowMinMatch, first[1].Kind);
      Assert.Equal("below --min-match (50%)", first[1].Column);
      Assert.False(File.Exists(Path.Combine(s2s, "第2話.ja.srt")));
      var pair = new SubsRetimerLauncher.Request(Path.Combine(s2s, "第2話.en.srt"), Path.Combine(dir, "第2話.ja.srt"),
        "utf-8", "utf-8", false, Path.Combine(s2s, "第2話.ja.srt"));
      Assert.Equal(SubsRetimerLauncher.EditorCommand(RetimeStage.CommandName(exe), pair), first[1].EditorCommand);

      // go --season takes episode 1 and skips episode 2 for its missing .ja file.
      string[] s2sNames = Directory.GetFiles(s2s).Select(f => Path.GetFileName(f)).ToArray()!;
      EpisodeList list = EpisodeList.ForSeason(dir, videos, s2sNames, 1, 0);
      Assert.Equal(Path.Combine(s2s, "第1話.ja.srt"), list.Episodes[0].Subs1);
      Assert.Equal("no .ja file", list.Episodes[1].SkipReason);

      // Written after both its files: the next run keeps it, bytes and all, and asks again for episode 2.
      byte[] retimed = File.ReadAllBytes(first[0].OutputPath!);
      RetimeOutcome[] second = await RunSeason();
      Assert.Equal(RetimeKind.Kept, second[0].Kind);
      Assert.Equal(retimed, File.ReadAllBytes(second[0].OutputPath!));
      Assert.Equal(RetimeKind.BelowMinMatch, second[1].Kind);
    }
  }
}
