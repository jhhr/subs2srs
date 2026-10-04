//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <see cref="SubsRetimerLauncher"/> and <see cref="RetimeReport"/>: the arguments, the start
  /// info, runs on a scripted runner, a cancel that kills a real child, the editor command for
  /// both shells, and reports written by the real tool (<c>Fixtures/retime</c>). Three tests run
  /// the real tool when <c>SUBSRETIMER_EXE</c> names a build of it.
  /// </summary>
  public class SubsRetimerLauncherTests : IDisposable
  {
    private readonly TestScope scope = new TestScope();

    public void Dispose()
    {
      SubsRetimerLauncher.RunnerOverride = null;
      scope.Dispose();
    }

    private static SubsRetimerLauncher.Request Req(bool auto = true, string? output = null) =>
      new("/tmp/EN.srt", "/tmp/JP.ass", "utf-8", "shift_jis", auto, output);

    // ── BuildArguments ───────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_AutoWithEncodings()
    {
      var args = SubsRetimerLauncher.BuildArguments(Req());
      Assert.Equal(new[]
      {
        "--print-output", "--auto",
        "--ref-encoding", "utf-8", "--target-encoding", "shift_jis",
        "--", "/tmp/EN.srt", "/tmp/JP.ass"
      }, args);
    }

    [Fact]
    public void BuildArguments_EditorModeOmitsAuto_AndPassesOutput()
    {
      var args = SubsRetimerLauncher.BuildArguments(Req(auto: false, output: "/tmp/out.ass"));
      Assert.DoesNotContain("--auto", args);
      int i = args.IndexOf("--output");
      Assert.True(i >= 0);
      Assert.Equal("/tmp/out.ass", args[i + 1]);
      Assert.Equal("--print-output", args[0]);
      Assert.Equal("/tmp/JP.ass", args[^1]);
    }

    [Fact]
    public void BuildArguments_EmptyEncodingFallsBackToUtf8()
    {
      var args = SubsRetimerLauncher.BuildArguments(new SubsRetimerLauncher.Request("a", "b", "", "", true));
      int r = args.IndexOf("--ref-encoding");
      int t = args.IndexOf("--target-encoding");
      Assert.Equal("utf-8", args[r + 1]);
      Assert.Equal("utf-8", args[t + 1]);
    }

    [Fact]
    public void BuildArguments_MinMatchAndReport_BeforeTheEncodings_AndDashDashBeforeThePaths()
    {
      var args = SubsRetimerLauncher.BuildArguments(new SubsRetimerLauncher.Request(
        "/s1/s2s/第1話.en.ass", "/s1/第1話.srt", "utf-8", "shift_jis", true,
        "/s1/s2s/第1話.ja.srt", MinMatch: 0.85, ReportPath: "/s1/s2s/第1話.retime.json"));

      Assert.Equal(new[]
      {
        "--print-output", "--auto", "--output", "/s1/s2s/第1話.ja.srt",
        "--min-match", "0.85", "--report", "/s1/s2s/第1話.retime.json",
        "--ref-encoding", "utf-8", "--target-encoding", "shift_jis",
        "--", "/s1/s2s/第1話.en.ass", "/s1/第1話.srt"
      }, args);
    }

    [Theory]
    [InlineData(0.85, "0.85")]
    [InlineData(0.5, "0.5")]
    [InlineData(1.0, "1")]
    [InlineData(0.0, "0")]
    public void BuildArguments_MinMatch_InTheInvariantCulture_AndPassedWhenZero(double minMatch, string expected)
    {
      CultureInfo culture = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma; subsretimer refuses "0,85"
        var args = SubsRetimerLauncher.BuildArguments(Req() with { MinMatch = minMatch });
        Assert.Equal(expected, args[args.IndexOf("--min-match") + 1]);
      }
      finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void BuildArguments_NoMinMatchOrReport_WhenNotSet()
    {
      var args = SubsRetimerLauncher.BuildArguments(Req() with { ReportPath = "" });
      Assert.DoesNotContain("--min-match", args);
      Assert.DoesNotContain("--report", args);
    }

    // ── The process ──────────────────────────────────────────────────────

    [Fact]
    public void StartInfo_Utf8Pipes_NoWindow_ArgumentsOneByOne()
    {
      var request = Req(output: "/アニメ S1/s2s/第1話 [x].ja.srt");

      ProcessStartInfo psi = SubsRetimerLauncher.StartInfo("/opt/sub tools/subsretimer", request);

      Assert.Equal("/opt/sub tools/subsretimer", psi.FileName);
      Assert.Equal("", psi.Arguments);
      Assert.Equal(SubsRetimerLauncher.BuildArguments(request), psi.ArgumentList);
      Assert.False(psi.UseShellExecute);
      Assert.True(psi.CreateNoWindow);
      Assert.True(psi.RedirectStandardOutput);
      Assert.True(psi.RedirectStandardError);
      foreach (Encoding? encoding in new[] { psi.StandardOutputEncoding, psi.StandardErrorEncoding })
      {
        Assert.IsType<UTF8Encoding>(encoding);
        Assert.Empty(encoding!.GetPreamble());
      }
    }

    /// <summary>Scripts the tool: every run gets this result; the start infos are recorded.</summary>
    private static List<ProcessStartInfo> Script(int? exitCode, string stdout = "", string stderr = "")
    {
      var started = new List<ProcessStartInfo>();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) =>
      {
        started.Add(psi);
        return Task.FromResult(new CliProcessResult { ExitCode = exitCode, Stdout = stdout, Stderr = stderr });
      };
      return started;
    }

    [Fact]
    public async Task RunAsync_Scripted_StartsTheGivenExe_AndGetsAJapanesePathBack()
    {
      string saved = "/home/u/アニメ S1/s2s/第1話.ja.srt";
      var request = new SubsRetimerLauncher.Request("/home/u/アニメ S1/s2s/第1話.en.srt", "/home/u/アニメ S1/第1話.srt",
        "utf-8", "shift_jis", true, saved, 0.8, "/home/u/アニメ S1/s2s/第1話.retime.json");
      List<ProcessStartInfo> started = Script(0, saved + "\n", "reference covered: 95% (114 of 120 lines)\n");

      var result = await SubsRetimerLauncher.RunAsync("/opt/subsretimer", request);

      Assert.True(result.Saved);
      Assert.Equal(saved, result.SavedPath);
      ProcessStartInfo psi = Assert.Single(started);
      Assert.Equal("/opt/subsretimer", psi.FileName);
      Assert.Equal(SubsRetimerLauncher.BuildArguments(request), psi.ArgumentList);
      Assert.IsType<UTF8Encoding>(psi.StandardOutputEncoding);
    }

    [Fact]
    public async Task RunAsync_Cancelled_Throws_InsteadOfLookingLikeNothingSaved()
    {
      using var cts = new CancellationTokenSource();
      SubsRetimerLauncher.RunnerOverride = (psi, ct) =>
      {
        cts.Cancel();
        throw new OperationCanceledException(ct);
      };

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => SubsRetimerLauncher.RunAsync("/opt/subsretimer", Req(), cts.Token));
    }

    [Fact]
    public async Task RunAsync_CancelledBeforeItStarts_RunsNothing()
    {
      List<ProcessStartInfo> started = Script(0, "/tmp/x.ass\n");

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => SubsRetimerLauncher.RunAsync("/opt/subsretimer", Req(), new CancellationToken(true)));

      Assert.Empty(started);
    }

    [Fact]
    public async Task RunAsync_NoExitCode_IsAFailure()
    {
      Script(null);

      var result = await SubsRetimerLauncher.RunAsync("/opt/subsretimer", Req());

      Assert.True(result.Failed);
      Assert.Equal("subsretimer did not finish", result.StdErr);
    }

    /// <summary>A real child that writes its process id to <paramref name="pidFile"/>, then sleeps a minute.</summary>
    private static ProcessStartInfo Sleeper(string pidFile)
    {
      bool windows = OperatingSystem.IsWindows();
      ProcessStartInfo psi = UtilsCommon.makeToolStartInfo(windows ? "powershell.exe" : "/bin/sh", "",
        redirectStdout: true, redirectStderr: true);
      string[] args = windows
        ? new[] { "-NoProfile", "-NonInteractive", "-Command",
            "Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "' -Value $PID; Start-Sleep -Seconds 60" }
        : new[] { "-c", "echo $$ > \"$1\"; exec sleep 60", "sh", pidFile };
      foreach (string a in args) psi.ArgumentList.Add(a);
      return psi;
    }

    private static async Task<int> ReadPid(string pidFile, Task run)
    {
      for (DateTime end = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow < end; await Task.Delay(50))
      {
        Assert.False(run.IsCompleted, "the child ended before the cancel: " + run.Exception?.Message);
        try
        {
          if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
            return pid;
        }
        catch (IOException) { } // still being written
      }
      throw new TimeoutException("the child did not write its process id");
    }

    private static bool IsRunning(int pid)
    {
      try
      {
        using Process p = Process.GetProcessById(pid);
        return !p.HasExited;
      }
      catch (ArgumentException) { return false; }
      catch (InvalidOperationException) { return false; }
    }

    [Fact]
    public async Task RunAsync_Cancel_KillsTheRealChild_AndWaitsForIt()
    {
      string pidFile = Path.Combine(scope.TempDir, "pid.txt");
      // The launcher's own start info, run as a sleeping child through the real process loop.
      SubsRetimerLauncher.RunnerOverride = (psi, ct) => UtilsCommon.RunToolAsync(Sleeper(pidFile), ct);
      using var cts = new CancellationTokenSource();

      Task<SubsRetimerLauncher.Result> run = SubsRetimerLauncher.RunAsync("/opt/subsretimer", Req(), cts.Token);
      int pid = await ReadPid(pidFile, run);
      try
      {
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(IsRunning(pid), "the child still runs after the cancel");
      }
      finally
      {
        if (IsRunning(pid)) Process.GetProcessById(pid).Kill();
      }
    }

    // ── The editor command (Design 9) ────────────────────────────────────

    private static SubsRetimerLauncher.Request BatchRequest(string season, char sep, string targetEncoding = "utf-8") =>
      new($"{season}{sep}s2s{sep}Show - 02.en.ass", $"{season}{sep}Show - 02.srt", "utf-8", targetEncoding, Auto: true,
        OutputPath: $"{season}{sep}s2s{sep}Show - 02.ja.srt", MinMatch: 0.8,
        ReportPath: $"{season}{sep}s2s{sep}Show - 02.retime.json");

    [Fact]
    public void EditorCommand_Posix_SingleQuotes_AndLeavesOutTheBatchOptions()
    {
      var request = BatchRequest("/home/u/アニメ S1", '/', "shift_jis");

      Assert.Equal(
        "subsretimer --target-encoding shift_jis --output '/home/u/アニメ S1/s2s/Show - 02.ja.srt' "
        + "'/home/u/アニメ S1/s2s/Show - 02.en.ass' '/home/u/アニメ S1/Show - 02.srt'",
        SubsRetimerLauncher.EditorCommand("subsretimer", request, windows: false));
    }

    [Fact]
    public void EditorCommand_Posix_QuoteInAName_ExeWithASpace_PlainPathsBare()
    {
      var request = new SubsRetimerLauncher.Request("/v/ep1.en.srt", "/v/Don't $HOME `x`.srt", "", "", false, "/v/ep1.ja.srt");

      Assert.Equal(
        "'/opt/sub tools/subsretimer' --target-encoding utf-8 --output /v/ep1.ja.srt /v/ep1.en.srt "
        + "'/v/Don'\\''t $HOME `x`.srt'",
        SubsRetimerLauncher.EditorCommand("/opt/sub tools/subsretimer", request, windows: false));
    }

    [Fact]
    public void EditorCommand_Windows_DoubleQuotes_AndTheCallOperatorForAQuotedExe()
    {
      var request = BatchRequest(@"D:\Anime\Show S1", '\\');
      const string rest = @" --target-encoding utf-8 --output ""D:\Anime\Show S1\s2s\Show - 02.ja.srt"" "
        + @"""D:\Anime\Show S1\s2s\Show - 02.en.ass"" ""D:\Anime\Show S1\Show - 02.srt""";

      Assert.Equal("subsretimer" + rest, SubsRetimerLauncher.EditorCommand("subsretimer", request, windows: true));
      Assert.Equal(@"C:\Tools\subsretimer.exe" + rest,
        SubsRetimerLauncher.EditorCommand(@"C:\Tools\subsretimer.exe", request, windows: true));
      Assert.Equal(@"& ""C:\Program Files\subsretimer\subsretimer.exe""" + rest,
        SubsRetimerLauncher.EditorCommand(@"C:\Program Files\subsretimer\subsretimer.exe", request, windows: true));
    }

    [Fact]
    public void EditorCommand_Windows_JapaneseNames_EscapesWhatPowerShellWouldExpandOrEndAString()
    {
      var request = new SubsRetimerLauncher.Request(@"D:\アニメ\第1話.en.srt", "D:\\アニメ\\Don't $x `y` \u201Cz\u201D \u201Eq.srt",
        "shift_jis", "utf-8", true, @"D:\アニメ\第1話.ja.srt");

      Assert.Equal(
        @"subsretimer --ref-encoding shift_jis --target-encoding utf-8 --output ""D:\アニメ\第1話.ja.srt"" "
        + @"""D:\アニメ\第1話.en.srt"" ""D:\アニメ\Don't `$x ``y`` `" + "\u201Cz`\u201D `\u201Eq.srt\"",
        SubsRetimerLauncher.EditorCommand("subsretimer", request, windows: true));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("utf-8", false)]
    [InlineData("UTF-8", false)]
    [InlineData("shift_jis", true)]
    public void EditorCommand_RefEncoding_OnlyWhenNotUtf8(string refEncoding, bool passed)
    {
      var request = new SubsRetimerLauncher.Request("/a/en.srt", "/a/jp.srt", refEncoding, "euc-jp", true, "/a/out.srt");

      string command = SubsRetimerLauncher.EditorCommand("subsretimer", request, windows: false);

      Assert.Equal(passed, command.Contains("--ref-encoding " + refEncoding));
      Assert.Contains("--target-encoding euc-jp", command);
      Assert.Equal(SubsRetimerLauncher.EditorCommand("subsretimer", request, OperatingSystem.IsWindows()),
        SubsRetimerLauncher.EditorCommand("subsretimer", request));
    }

    [RequiresPosixShellFact]
    public async Task EditorCommand_Posix_RunsInSh_WithEveryArgumentIntact()
    {
      // A stand-in for the tool that prints its arguments, one per line, in a folder that needs quoting.
      string tools = Path.Combine(scope.TempDir, "ツール's dir");
      Directory.CreateDirectory(tools);
      string exe = Path.Combine(tools, "subsretimer");
      File.WriteAllText(exe, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done\n");
      if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      string season = Path.Combine(scope.TempDir, "アニメ S1");
      var request = new SubsRetimerLauncher.Request(
        Path.Combine(season, "s2s", "第2話 [Group].en.ass"), Path.Combine(season, "Don't $HOME `x` \\n \u201Cq\u201D.srt"),
        "shift_jis", "utf-8", true, Path.Combine(season, "s2s", "第2話 [Group].ja.srt"), 0.8, "/r.json");

      ProcessStartInfo psi = UtilsCommon.makeToolStartInfo(RequiresPosixShellFactAttribute.Shell, "",
        redirectStdout: true, redirectStderr: true);
      psi.ArgumentList.Add("-c");
      psi.ArgumentList.Add(SubsRetimerLauncher.EditorCommand(exe, request, windows: false));
      CliProcessResult run = await UtilsCommon.RunToolAsync(psi, CancellationToken.None);

      Assert.True(run.ExitCode == 0, run.Stderr);
      Assert.Equal(new[]
      {
        "--ref-encoding", "shift_jis", "--target-encoding", "utf-8",
        "--output", request.OutputPath!, request.ReferencePath, request.TargetPath
      }, run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // ── The report ───────────────────────────────────────────────────────

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "retime", name);

    [Fact]
    public void Report_Saved_FromTheRealTool()
    {
      RetimeReport? report = RetimeReport.Read(Fixture("saved.retime.json"));

      Assert.Equal(new RetimeReport(0, "/home/user/Anime/アニメ S1/s2s/第1話.ja.srt", 2, 0.95, null), report);
    }

    [Fact]
    public void Report_BelowMinMatch_FromTheRealTool()
    {
      RetimeReport? report = RetimeReport.Read(Fixture("below-min-match.retime.json"));

      Assert.Equal(new RetimeReport(2, null, 2, 0.5, RetimeReport.BelowMinMatch), report);
      Assert.Equal("below min-match", RetimeReport.BelowMinMatch);
    }

    [Fact]
    public void Report_NoTimedLines_FromTheRealTool()
    {
      RetimeReport? report = RetimeReport.Read(Fixture("no-timed-lines.retime.json"));

      Assert.Equal(new RetimeReport(2, null, 0, null, RetimeReport.NoTimedLines), report);
      Assert.Equal("no timed lines", RetimeReport.NoTimedLines);
    }

    [Fact]
    public void Report_OnlyVersionAndExitCode_TheRestDefaults_AWholeShareAndABomRead()
    {
      string path = Path.Combine(scope.TempDir, "r.json");
      File.WriteAllText(path, "{ \"version\": 1, \"exitCode\": 2 }");
      Assert.Equal(new RetimeReport(2, null, 0, null, null), RetimeReport.Read(path));

      File.WriteAllText(path, "{ \"version\": 1, \"exitCode\": 0, \"referenceCoverage\": { \"share\": 1 }, \"extra\": [1] }",
        new UTF8Encoding(true));
      Assert.Equal(1.0, RetimeReport.Read(path)!.ReferenceCoverage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"exitCode\": 0 }")]
    [InlineData("{ \"version\": 2, \"exitCode\": 0 }")]
    [InlineData("{ \"version\": 1.5, \"exitCode\": 0 }")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("{ \"version\": 1, \"exitCode\": \"0\" }")]
    [InlineData("{ \"version\": 1, \"exitCode\": 0, \"saved\": 3 }")]
    [InlineData("{ \"version\": 1, \"exitCode\": 0, \"referenceCoverage\": {} }")]
    [InlineData("{ \"version\": 1, \"exitCode\": 0, \"referenceCoverage\": { \"share\": \"95%\" } }")]
    [InlineData("{ \"version\": 1, \"exitCode\": 0, \"reason\": false }")]
    [InlineData("{ \"version\": 1, \"exitCode\": 0")]
    public void Report_NotTheToolsVersion1Json_IsNull(string json)
    {
      string path = Path.Combine(scope.TempDir, "r.json");
      File.WriteAllText(path, json);

      Assert.Null(RetimeReport.Read(path));
    }

    [Fact]
    public void Report_MissingOrNotAFile_IsNull()
    {
      Assert.Null(RetimeReport.Read(Path.Combine(scope.TempDir, "missing.json")));
      Assert.Null(RetimeReport.Read(Path.Combine(scope.TempDir, "no folder", "r.json")));
      Assert.Null(RetimeReport.Read(scope.TempDir));
      Assert.Null(RetimeReport.Read(""));
    }

    // ── ParseResult ──────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_ExitZeroWithPath_IsSaved()
    {
      var r = SubsRetimerLauncher.ParseResult(0, "/tmp/JP_retimed.ass\n", "saved /tmp/JP_retimed.ass\n");
      Assert.True(r.Saved);
      Assert.False(r.NothingSaved);
      Assert.False(r.Failed);
      Assert.Equal("/tmp/JP_retimed.ass", r.SavedPath);
      Assert.Equal("saved /tmp/JP_retimed.ass", r.StdErr);
    }

    [Fact]
    public void ParseResult_UsesLastLine_AndStripsCrLf()
    {
      var r = SubsRetimerLauncher.ParseResult(0, "/tmp/first.ass\r\n/tmp/second.ass\r\n", "");
      Assert.Equal("/tmp/second.ass", r.SavedPath);
    }

    [Fact]
    public void ParseResult_ExitZeroWithoutPath_IsNothingSaved()
    {
      var r = SubsRetimerLauncher.ParseResult(0, "", "");
      Assert.False(r.Saved);
      Assert.True(r.NothingSaved);
      Assert.False(r.Failed);
    }

    [Fact]
    public void ParseResult_ExitTwo_IsNothingSaved_EvenWithStdout()
    {
      var r = SubsRetimerLauncher.ParseResult(2, "/tmp/x.ass\n", "");
      Assert.True(r.NothingSaved);
      Assert.Null(r.SavedPath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(-1)]
    public void ParseResult_OtherExitCodes_AreFailures(int code)
    {
      var r = SubsRetimerLauncher.ParseResult(code, "/tmp/x.ass\n", "boom");
      Assert.True(r.Failed);
      Assert.Null(r.SavedPath);
      Assert.Equal("boom", r.StdErr);
    }

    // ── Real tool (only when SUBSRETIMER_EXE points at a build) ──────────

    /// <summary>The build <c>SUBSRETIMER_EXE</c> names; a name that is not a file fails the test rather than passing it.</summary>
    private static string RealTool()
    {
      string p = Environment.GetEnvironmentVariable("SUBSRETIMER_EXE")!;
      Assert.True(File.Exists(p), "SUBSRETIMER_EXE: no such file: " + p);
      return p;
    }

    private static string WriteSrt(string dir, string name, double offsetSecondsAfterLine40)
    {
      var sb = new System.Text.StringBuilder();
      var rnd = new Random(3);
      double t = 1.0;
      for (int i = 1; i <= 120; i++)
      {
        double dur = 0.6 + rnd.Next(0, 2400) / 1000.0;
        double off = i > 40 ? offsetSecondsAfterLine40 : 0;
        sb.Append(i).Append('\n')
          .Append(Fmt(t + off)).Append(" --> ").Append(Fmt(t + dur + off)).Append('\n')
          .Append("line ").Append(i).Append("\n\n");
        t += dur + 0.2 + rnd.Next(0, 3800) / 1000.0;
      }
      string path = Path.Combine(dir, name);
      File.WriteAllText(path, sb.ToString());
      return path;
    }

    private static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\,fff");

    [RequiresEnvFact("SUBSRETIMER_EXE")]
    public async Task RealTool_AutoAlign_SavesAndReportsPath()
    {
      string exe = RealTool();

      string dir = Path.Combine(Path.GetTempPath(), "subs2srs-retimer-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      string reference = WriteSrt(dir, "EN.srt", 0);
      string target = WriteSrt(dir, "JP.srt", 15);

      var result = await SubsRetimerLauncher.RunAsync(exe,
        new SubsRetimerLauncher.Request(reference, target, "utf-8", "utf-8", true));

      Assert.True(result.Saved, result.StdErr);
      Assert.Equal(Path.Combine(dir, "JP_retimed.srt"), result.SavedPath);
      Assert.True(File.Exists(result.SavedPath));
      Assert.Contains("lines 41-120: -15.000s", result.StdErr);

      // Running again without --output must refuse to overwrite, which surfaces as a failure.
      var again = await SubsRetimerLauncher.RunAsync(exe,
        new SubsRetimerLauncher.Request(reference, target, "utf-8", "utf-8", true));
      Assert.True(again.Failed);
      Assert.Contains("already exists", again.StdErr);
    }

    [RequiresEnvFact("SUBSRETIMER_EXE")]
    public async Task RealTool_MissingFile_IsFailureWithMessage()
    {
      string exe = RealTool();
      var result = await SubsRetimerLauncher.RunAsync(exe,
        new SubsRetimerLauncher.Request("/nonexistent/EN.srt", "/nonexistent/JP.ass", "utf-8", "utf-8", true));
      Assert.True(result.Failed);
      Assert.Contains("not found", result.StdErr);
    }

    [RequiresEnvFact("SUBSRETIMER_EXE")]
    public async Task RealTool_JapaneseFolder_SavedPathAndReportComeBackIntact()
    {
      string exe = RealTool();
      string season = Path.Combine(scope.TempDir, "アニメ S1");
      string s2s = Path.Combine(season, "s2s");
      Directory.CreateDirectory(s2s);
      string reference = WriteSrt(s2s, "第1話 [字幕].en.srt", 0);
      string target = WriteSrt(season, "第1話 [字幕].srt", 15);
      string output = Path.Combine(s2s, "第1話 [字幕].ja.srt");
      string report = Path.Combine(s2s, "第1話 [字幕].retime.json");

      SubsRetimerLauncher.Result result;
      CultureInfo culture = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // the tool refuses --min-match "0,8"
        result = await SubsRetimerLauncher.RunAsync(exe, new SubsRetimerLauncher.Request(
          reference, target, "utf-8", "utf-8", true, output, MinMatch: 0.8, ReportPath: report));
      }
      finally { CultureInfo.CurrentCulture = culture; }

      Assert.True(result.Saved, result.StdErr);
      Assert.Equal(output, result.SavedPath);
      Assert.True(File.Exists(output));
      Assert.Equal(new RetimeReport(0, output, 2, 1.0, null), RetimeReport.Read(report));
    }

    [Fact]
    public async Task MissingExecutable_IsFailureNotException()
    {
      var result = await SubsRetimerLauncher.RunAsync("/nonexistent/subsretimer",
        new SubsRetimerLauncher.Request("a.srt", "b.ass", "utf-8", "utf-8", true));
      Assert.True(result.Failed);
      Assert.Contains("Could not run", result.StdErr);
    }
  }
}
