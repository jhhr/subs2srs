using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <see cref="MkvExtract"/>: the EN extract's name (season plan B3), and mkvextract through a
  /// scripted runner (arguments, exit codes, the partial file deleted on a failure and a cancel,
  /// an existing output kept untouched). Two tests run the real mkvextract on an mkv made here
  /// with mkvmerge from generated subtitles, in a folder with a space and Japanese characters.
  /// </summary>
  public class MkvExtractTests : IDisposable
  {
    private readonly TestScope scope = new TestScope();
    private readonly string? path = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
      MkvExtract.RunnerOverride = null;
      ConstantSettings.MkvToolNixDirsOverride = null;
      Environment.SetEnvironmentVariable("PATH", path);
      scope.Dispose();
    }

    private static MkvTrackInfo Track(int id, string codecId)
      => new MkvTrackInfo { Id = id, Type = "subtitles", CodecId = codecId, Language = "eng" };

    // ── The output name ──────────────────────────────────────────────────

    [Theory]
    [InlineData("S_TEXT/ASS", "ass")]
    [InlineData("S_TEXT/SSA", "ssa")]
    [InlineData("S_TEXT/UTF8", "srt")]
    [InlineData("S_TEXT/WEBVTT", null)]
    [InlineData("S_HDMV/PGS", null)]
    [InlineData("s_text/ass", null)]
    [InlineData("", null)]
    public void Extension_FromTheCodecId(string codecId, string? ext)
      => Assert.Equal(ext, MkvExtract.Extension(codecId));

    [Theory]
    [InlineData("[Group] Show - 01 [1080p].mkv", "S_TEXT/ASS", "[Group] Show - 01 [1080p].en.ass")]
    [InlineData("Show.S01E02.1080p.mkv", "S_TEXT/UTF8", "Show.S01E02.1080p.en.srt")]
    [InlineData("番組 第3話.mkv", "S_TEXT/SSA", "番組 第3話.en.ssa")]
    public void OutputPath_IsS2s_VideoName_En_Ext(string video, string codecId, string name)
    {
      string season = Path.Combine(scope.TempDir, "Show S1");

      string output = MkvExtract.OutputPath(season, Path.Combine(season, video), Track(3, codecId));

      Assert.Equal(Path.Combine(season, "s2s", name), output);
    }

    [Fact]
    public void OutputPath_IsTheSubs2File_GoSeasonLooksFor()
    {
      string season = Path.Combine(scope.TempDir, "Show S1");
      string video = Path.Combine(season, "[Group] Show - 01.mkv");
      string output = MkvExtract.OutputPath(season, video, Track(2, "S_TEXT/ASS"));

      EpisodeList list = EpisodeList.ForSeason(season, new[] { video },
        new[] { Path.GetFileName(output), "[Group] Show - 01.ja.srt" }, startNumber: 1, endNumber: 0);

      Episode episode = Assert.Single(list.Episodes);
      Assert.Null(episode.SkipReason);
      Assert.Equal(output, episode.Subs2);
    }

    [Theory]
    [InlineData("S_HDMV/PGS")]
    [InlineData("S_TEXT/WEBVTT")]
    public void OutputPath_NotAnAssSsaOrSrtTrack_Throws(string codecId)
    {
      ArgumentException ex = Assert.Throws<ArgumentException>(
        () => MkvExtract.OutputPath(scope.TempDir, "a.mkv", Track(4, codecId)));
      Assert.Contains("track 4 (" + codecId + ")", ex.Message);
    }

    // ── mkvextract (scripted) ────────────────────────────────────────────

    /// <summary>An empty <c>mkvextract</c> in the Tools Directory, so it resolves on any machine.</summary>
    private string FakeMkvExtract()
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      string exe = Path.Combine(tools, "mkvextract");
      File.WriteAllText(exe, "");
      ConstantSettings.ToolsDir = tools;
      return exe;
    }

    /// <summary>A season folder with one (fake) video in it; returns the video's path.</summary>
    private string Video(string name = "番組 [01].mkv")
    {
      string season = Path.Combine(scope.TempDir, "シーズン 1");
      Directory.CreateDirectory(season);
      string video = Path.Combine(season, name);
      File.WriteAllText(video, "not really an mkv");
      return video;
    }

    /// <summary>
    /// A runner that records the start info, writes <paramref name="written"/> (when not null) to
    /// the output named in the last argument, as mkvextract would, and returns the given result.
    /// </summary>
    private List<ProcessStartInfo> Script(int? exitCode, string? written, string stdout = "", string stderr = "")
    {
      var started = new List<ProcessStartInfo>();
      MkvExtract.RunnerOverride = (psi, ct) =>
      {
        started.Add(psi);
        if (written != null) File.WriteAllText(OutputOf(psi), written);
        return Task.FromResult(new CliProcessResult { ExitCode = exitCode, Stdout = stdout, Stderr = stderr });
      };
      return started;
    }

    private static string OutputOf(ProcessStartInfo psi)
    {
      string spec = psi.ArgumentList.Last();
      return spec.Substring(spec.IndexOf(':') + 1);
    }

    [Fact]
    public async Task Extract_RunsMkvextractTracks_WithFullPaths_AndCreatesTheS2sFolder()
    {
      string exe = FakeMkvExtract();
      string video = Video();
      string output = MkvExtract.OutputPath(Path.GetDirectoryName(video)!, video, Track(3, "S_TEXT/ASS"));
      List<ProcessStartInfo> started = Script(0, "[Script Info]\n", stdout: "Progress: 50%\rProgress: 100%\n");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 3, output);

      Assert.Equal(MkvExtractStatus.Extracted, result.Status);
      Assert.Null(result.Reason);
      Assert.Equal(output, result.OutputPath);
      Assert.Equal("[Script Info]\n", File.ReadAllText(output));
      ProcessStartInfo psi = Assert.Single(started);
      Assert.Equal(exe, psi.FileName);
      Assert.Equal(new[] { "--output-charset", "UTF-8", video, "tracks", "3:" + output }, psi.ArgumentList);
      Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError);
      Assert.Equal("utf-8", psi.StandardOutputEncoding!.WebName);
      if (!OperatingSystem.IsWindows())
        Assert.True(MkvTracks.IsUtf8Locale(psi.Environment));
    }

    [Fact]
    public async Task Extract_RelativeNames_ArePassedAsFullPaths()
    {
      FakeMkvExtract();
      string video = Video("@show.mkv");
      List<ProcessStartInfo> started = Script(0, "1\n");
      string cwd = Environment.CurrentDirectory;
      try
      {
        Environment.CurrentDirectory = Path.GetDirectoryName(video)!;
        MkvExtractResult result = await MkvExtract.ExtractAsync("@show.mkv", 0, Path.Combine("s2s", "@show.en.srt"));

        Assert.Equal(MkvExtractStatus.Extracted, result.Status);
        Assert.Equal(video, started.Single().ArgumentList[2]);
        Assert.Equal("0:" + Path.Combine(Path.GetDirectoryName(video)!, "s2s", "@show.en.srt"), started.Single().ArgumentList[4]);
      }
      finally
      {
        Environment.CurrentDirectory = cwd;
      }
    }

    [Fact]
    public async Task Extract_ExistingOutput_IsKept_Untouched_AndMkvextractNotRun()
    {
      FakeMkvExtract();
      string video = Video();
      string output = MkvExtract.OutputPath(Path.GetDirectoryName(video)!, video, Track(2, "S_TEXT/UTF8"));
      Directory.CreateDirectory(Path.GetDirectoryName(output)!);
      byte[] bytes = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nFixed by hand\n\n");
      File.WriteAllBytes(output, bytes);
      var mtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
      File.SetLastWriteTimeUtc(output, mtime);
      List<ProcessStartInfo> started = Script(0, "overwritten");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 2, output);

      Assert.Equal(MkvExtractStatus.Kept, result.Status);
      Assert.Null(result.Reason);
      Assert.Empty(started);
      Assert.Equal(bytes, File.ReadAllBytes(output));
      Assert.Equal(mtime, File.GetLastWriteTimeUtc(output));
    }

    [Fact]
    public async Task Extract_ExistingEmptyOutput_IsExtractedAgain()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(Path.GetDirectoryName(video)!, "s2s", "番組 [01].en.ass");
      Directory.CreateDirectory(Path.GetDirectoryName(output)!);
      File.WriteAllText(output, "");
      List<ProcessStartInfo> started = Script(0, "[Script Info]\n");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 0, output);

      Assert.Equal(MkvExtractStatus.Extracted, result.Status);
      Assert.Single(started);
    }

    [Fact]
    public async Task Extract_ExitCode2_FailsWithItsError_AndDeletesThePartialFile()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(Path.GetDirectoryName(video)!, "s2s", "番組 [01].en.ass");
      Script(2, "[Script Info]\npartial",
        stdout: "Extracting track 3 with the CodecID 'S_TEXT/ASS' to the file '" + output + "'.\n"
          + "Progress: 10%\rError: Failed to write to the file '" + output + "': disk full\n");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 3, output);

      Assert.Equal(MkvExtractStatus.Failed, result.Status);
      Assert.Equal("mkvextract exited with code 2: Failed to write to the file '" + output + "': disk full", result.Reason);
      Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Extract_ExitCode2_WithoutAnErrorLine_GivesTheLastLine()
    {
      FakeMkvExtract();
      string video = Video();
      Script(3, null, stdout: "Progress: 10%\n", stderr: "something broke\n\n");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 3, Path.Combine(scope.TempDir, "o.ass"));

      Assert.Equal("mkvextract exited with code 3: something broke", result.Reason);
    }

    [Fact]
    public async Task Extract_ExitCode1_IsWarnings_TheFileIsKeptAsExtracted()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(scope.TempDir, "s2s", "a.en.srt");
      Script(1, "1\n00:00:01,000 --> 00:00:02,000\nLine\n\n", stdout: "Warning: something odd in the file\n");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 1, output);

      Assert.Equal(MkvExtractStatus.Extracted, result.Status);
      Assert.True(File.Exists(output));
    }

    [Theory]
    [InlineData(0, null, "Progress: 100%\n", "mkvextract exited with code 0 but wrote nothing")]
    [InlineData(0, "", "Progress: 100%\n", "mkvextract exited with code 0 but wrote nothing")]
    [InlineData(1, "", "Warning: odd track\nProgress: 100%\n", "mkvextract exited with code 1 but wrote nothing: odd track")]
    public async Task Extract_SuccessWithoutOutput_Fails_AndLeavesNoFile(int code, string? written, string stdout, string reason)
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(scope.TempDir, "s2s", "a.en.srt");
      Script(code, written, stdout);

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 1, output);

      Assert.Equal(MkvExtractStatus.Failed, result.Status);
      Assert.Equal(reason, result.Reason);
      Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Extract_NoExitCode_OrNotStarting_FailsWithAReason_AndLeavesNoFile()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(scope.TempDir, "s2s", "a.en.ass");
      Script(null, "partial");

      MkvExtractResult unfinished = await MkvExtract.ExtractAsync(video, 0, output);

      Assert.Equal("mkvextract did not finish", unfinished.Reason);
      Assert.False(File.Exists(output));

      MkvExtract.RunnerOverride = (psi, ct) => throw new Win32Exception("No such file or directory");
      MkvExtractResult notStarted = await MkvExtract.ExtractAsync(video, 0, output);

      Assert.Equal(MkvExtractStatus.Failed, notStarted.Status);
      Assert.StartsWith("could not start mkvextract: ", notStarted.Reason);
    }

    [Fact]
    public async Task Extract_Cancelled_Throws_AndDeletesThePartialFile()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(Path.GetDirectoryName(video)!, "s2s", "番組 [01].en.ass");
      using var cts = new CancellationTokenSource();
      MkvExtract.RunnerOverride = (psi, ct) =>
      {
        File.WriteAllText(OutputOf(psi), "[Script Info]\npartial");
        cts.Cancel();
        throw new OperationCanceledException(ct);
      };

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MkvExtract.ExtractAsync(video, 0, output, cts.Token));

      Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Extract_CancelledBeforeItStarts_LeavesAnExistingOutputAlone()
    {
      FakeMkvExtract();
      string video = Video();
      string output = Path.Combine(scope.TempDir, "s2s", "a.en.ass");
      Directory.CreateDirectory(Path.GetDirectoryName(output)!);
      File.WriteAllText(output, "[Script Info]\n");
      List<ProcessStartInfo> started = Script(0, "x");

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => MkvExtract.ExtractAsync(video, 0, output, new CancellationToken(true)));

      Assert.Empty(started);
      Assert.Equal("[Script Info]\n", File.ReadAllText(output));
    }

    [Fact]
    public async Task Extract_MkvextractNotFound_OrNoVideo_IsAReason_AndNothingRuns()
    {
      string empty = Path.Combine(scope.TempDir, "empty");
      Directory.CreateDirectory(empty);
      ConstantSettings.ToolsDir = empty;
      Environment.SetEnvironmentVariable("PATH", empty);
      ConstantSettings.MkvToolNixDirsOverride = new[] { empty };
      string video = Video();
      string output = Path.Combine(scope.TempDir, "s2s", "a.en.ass");
      List<ProcessStartInfo> started = Script(0, "x");

      MkvExtractResult notFound = await MkvExtract.ExtractAsync(video, 0, output);

      Assert.Equal(MkvExtract.NotFoundMessage, notFound.Reason);

      FakeMkvExtract();
      string missing = Path.Combine(scope.TempDir, "nothere.mkv");
      MkvExtractResult noVideo = await MkvExtract.ExtractAsync(missing, 0, output);

      Assert.Equal(MkvExtractStatus.Failed, noVideo.Status);
      Assert.Equal("no such file: " + missing, noVideo.Reason);
      Assert.Empty(started);
      Assert.False(File.Exists(output));
    }

    // ── The real mkvextract ──────────────────────────────────────────────

    /// <summary>
    /// An mkv with an English ASS track (0), an English SRT track (1) and a Japanese SRT track (2),
    /// muxed from generated files in a season folder whose path has a space and Japanese characters.
    /// </summary>
    private string RealVideo()
    {
      string season = Path.Combine(scope.TempDir, "シーズン [1] 'x'");
      Directory.CreateDirectory(season);
      string ass = Path.Combine(season, "en.ass"), srt = Path.Combine(season, "en.srt"), ja = Path.Combine(season, "ja.srt");
      File.WriteAllText(ass, MkvTracksTests.Ass(3, "English line é"), new UTF8Encoding(false));
      File.WriteAllText(srt, MkvTracksTests.Srt(5, "English full line"), new UTF8Encoding(false));
      File.WriteAllText(ja, MkvTracksTests.Srt(4, "日本語の台詞"), new UTF8Encoding(false));
      string made = Path.Combine(season, "made.mkv");
      MkvTracksTests.Mux(made, "--language", "0:eng", ass, "--language", "0:eng", srt, "--language", "0:jpn", ja);
      string video = Path.Combine(season, "番組 第1話 [Group].mkv");
      File.Move(made, video);
      foreach (string f in new[] { ass, srt, ja }) File.Delete(f);
      return video;
    }

    [RequiresMkvToolnixFact]
    public async Task RealMkvextract_AssAndSrtTracks_ToS2s_AsUtf8WithBom()
    {
      string video = RealVideo();
      string season = Path.GetDirectoryName(video)!;
      MkvTrackList list = await MkvTracks.ListAsync(video);
      Assert.Null(list.Error);

      string assOut = MkvExtract.OutputPath(season, video, list.Tracks[0]);
      MkvExtractResult ass = await MkvExtract.ExtractAsync(video, list.Tracks[0].Id, assOut);
      string srtOut = MkvExtract.OutputPath(season, video, list.Tracks[1]);
      MkvExtractResult srt = await MkvExtract.ExtractAsync(video, list.Tracks[1].Id, srtOut);
      string jaOut = Path.Combine(season, "s2s", "ja.srt");
      MkvExtractResult ja = await MkvExtract.ExtractAsync(video, 2, jaOut);

      Assert.Equal((MkvExtractStatus.Extracted, (string?)null), (ass.Status, ass.Reason));
      Assert.Equal((MkvExtractStatus.Extracted, (string?)null), (srt.Status, srt.Reason));
      Assert.Equal(MkvExtractStatus.Extracted, ja.Status);
      Assert.Equal(Path.Combine(season, "s2s", "番組 第1話 [Group].en.ass"), ass.OutputPath);
      Assert.Equal(Path.Combine(season, "s2s", "番組 第1話 [Group].en.srt"), srt.OutputPath);
      // mkvextract 82 writes text subtitles as UTF-8 with a BOM (the inputs had none).
      byte[] bom = { 0xEF, 0xBB, 0xBF };
      foreach (string file in new[] { assOut, srtOut, jaOut })
        Assert.Equal(bom, File.ReadAllBytes(file).Take(3));
      string assText = File.ReadAllText(assOut);
      Assert.Contains("[Events]", assText);
      Assert.Equal(new[] { "English line é 1.", "English line é 2.", "English line é 3." },
        assText.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal))
          .Select(l => l.Split(',', 10)[9].Trim()));
      Assert.Equal(MkvTracksTests.Srt(5, "English full line").Trim(), File.ReadAllText(srtOut).Replace("\r\n", "\n").Trim());
      Assert.Contains("日本語の台詞 4.", File.ReadAllText(jaOut));

      // A second run keeps them.
      MkvExtractResult again = await MkvExtract.ExtractAsync(video, list.Tracks[0].Id, assOut);
      Assert.Equal(MkvExtractStatus.Kept, again.Status);
    }

    [RequiresMkvToolnixFact]
    public async Task RealMkvextract_TrackIdNotInTheFile_FailsWithItsMessage_AndLeavesNoFile()
    {
      string video = RealVideo();
      string output = Path.Combine(Path.GetDirectoryName(video)!, "s2s", "番組 第1話 [Group].en.ass");

      MkvExtractResult result = await MkvExtract.ExtractAsync(video, 9, output);

      Assert.Equal(MkvExtractStatus.Failed, result.Status);
      Assert.Equal("mkvextract exited with code 2: No track with the ID 9 was found in the source file.", result.Reason);
      Assert.False(File.Exists(output));
      Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!));
    }
  }
}
