//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace subs2srs.Tests
{
  public class SubsRetimerLauncherTests
  {
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

    private static string? ToolPath()
    {
      string? p = Environment.GetEnvironmentVariable("SUBSRETIMER_EXE");
      return p != null && File.Exists(p) ? p : null;
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

    [Fact]
    public async Task RealTool_AutoAlign_SavesAndReportsPath()
    {
      string? exe = ToolPath();
      if (exe == null) return; // tool not available in this environment

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

    [Fact]
    public async Task RealTool_MissingFile_IsFailureWithMessage()
    {
      string? exe = ToolPath();
      if (exe == null) return;
      var result = await SubsRetimerLauncher.RunAsync(exe,
        new SubsRetimerLauncher.Request("/nonexistent/EN.srt", "/nonexistent/JP.ass", "utf-8", "utf-8", true));
      Assert.True(result.Failed);
      Assert.Contains("not found", result.StdErr);
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
