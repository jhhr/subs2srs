using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <see cref="MkvTracks"/>: <c>mkvmerge -J</c> parsed into tracks, the EN track picked (season
  /// plan B1, B2), mkvmerge run through a scripted runner, and MKVToolNix found in its Windows
  /// install folder (<see cref="ConstantSettings.ResolveTool"/>) through an injected folder list.
  /// The fixtures in <c>Fixtures/mkv</c> were recorded from mkvmerge 82 (see its README), except
  /// the hand-written <c>image-tracks.handwritten.json</c>. One test runs the real mkvmerge.
  /// </summary>
  public class MkvTracksTests : IDisposable
  {
    private readonly TestScope scope = new TestScope();
    private readonly string? path = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
      MkvTracks.RunnerOverride = null;
      ConstantSettings.MkvToolNixDirsOverride = null;
      Environment.SetEnvironmentVariable("PATH", path);
      scope.Dispose();
    }

    private static string Fixture(string name)
      => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mkv", name));

    private static IReadOnlyList<MkvTrackInfo> Tracks(string fixture)
    {
      MkvTrackList list = MkvTracks.Parse(Fixture(fixture));
      Assert.Null(list.Error);
      return list.Tracks;
    }

    private static MkvTrackInfo Sub(int id, string codecId = "S_TEXT/UTF8", string lang = "eng",
      int? events = 10, bool forced = false, bool isDefault = true)
      => new MkvTrackInfo
      {
        Id = id, Type = "subtitles", CodecId = codecId, Language = lang, LanguageIetf = "",
        Events = events, Forced = forced, Default = isDefault,
      };

    // ── Parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_RecordedFile_ReadsEveryFieldOfEveryTrack()
    {
      IReadOnlyList<MkvTrackInfo> tracks = Tracks("multi-tracks.json");

      var expected = new (int Id, string Type, string Codec, string CodecId, string Lang, string Ietf,
        string Name, bool Forced, bool Default, int? Events)[]
      {
        (0, "video", "MPEG-4p2", "V_MPEG4/ISO/ASP", "und", "und", "", false, false, 1),
        (1, "audio", "AAC", "A_AAC", "und", "und", "", false, false, 0),
        (2, "subtitles", "SubStationAlpha", "S_TEXT/ASS", "eng", "en", "English", false, true, 6),
        (3, "subtitles", "SubRip/SRT", "S_TEXT/UTF8", "eng", "en", "English (full)", false, true, 9),
        (4, "subtitles", "SubStationAlpha", "S_TEXT/ASS", "eng", "en", "Signs", true, true, 12),
        (5, "subtitles", "SubRip/SRT", "S_TEXT/UTF8", "jpn", "ja", "Japanese", false, true, 15),
      };
      Assert.Equal(expected, tracks.Select(t =>
        (t.Id, t.Type, t.Codec, t.CodecId, t.Language, t.LanguageIetf, t.Name, t.Forced, t.Default, t.Events)));

      Assert.Equal(new[] { false, false, true, true, true, true }, tracks.Select(t => t.IsText));
      Assert.All(tracks, t => Assert.False(t.IsImage));
    }

    [Fact]
    public void Parse_IetfTagAndWebVtt_AsMkvmergeReportsThem()
    {
      IReadOnlyList<MkvTrackInfo> tracks = Tracks("en-us-webvtt.json");

      Assert.Equal(new[] { "en", "en-US", "en", "ja" }, tracks.Select(t => t.LanguageIetf));
      Assert.Equal("eng", tracks[1].Language); // mkvmerge 82 derives the old tag from en-US
      Assert.Equal("S_TEXT/WEBVTT", tracks[2].CodecId);
      Assert.False(tracks[2].IsText);
      Assert.False(tracks[2].IsImage);
      Assert.Equal("", tracks[3].Name);
    }

    [Fact]
    public void Parse_HandWrittenImageTracks_AreImage_NotText()
    {
      IReadOnlyList<MkvTrackInfo> tracks = Tracks("image-tracks.handwritten.json");

      Assert.Equal(new[] { 2, 3, 5 }, tracks.Where(t => t.IsImage).Select(t => t.Id));
      Assert.Equal(new[] { 4 }, tracks.Where(t => t.IsText).Select(t => t.Id));
      Assert.True(new MkvTrackInfo { Type = "subtitles", CodecId = "S_IMAGE/BMP" }.IsImage);
    }

    [Fact]
    public void Parse_MissingFields_GiveDefaults_AndATrackWithoutIdIsLeftOut()
    {
      MkvTrackList list = MkvTracks.Parse(
        "{\"tracks\":[{\"id\":3,\"type\":\"subtitles\",\"properties\":{\"codec_id\":\"S_TEXT/ASS\"}},"
        + "{\"type\":\"subtitles\"},{\"id\":4}]}");

      Assert.Null(list.Error);
      Assert.Equal(new[] { 3, 4 }, list.Tracks.Select(t => t.Id));
      MkvTrackInfo t = list.Tracks[0];
      Assert.Equal(("", "", "", ""), (t.Codec, t.Language, t.LanguageIetf, t.Name));
      Assert.False(t.Forced);
      Assert.False(t.Default);
      Assert.Null(t.Events);
      Assert.Equal("", list.Tracks[1].Type);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("{\"tracks\": [")]
    public void Parse_TextThatIsNotItsJson_IsAnError_NotAnException(string text)
    {
      MkvTrackList list = MkvTracks.Parse(text);

      Assert.Equal("mkvmerge did not print a track list (no JSON in its output)", list.Error);
      Assert.Empty(list.Tracks);
    }

    [Fact]
    public void Parse_ErrorsAndAnUnreadableContainer_AreErrors()
    {
      // Both recorded from mkvmerge 82: -J on a missing file (exit 2), and on a text file (exit 0).
      string missing = "{\n  \"errors\": [\n    \"The file 'nothere.mkv' could not be opened for reading: open file error.\\n\"\n  ],\n  \"warnings\": []\n}\n";
      string garbage = "{\n  \"container\": {\n    \"recognized\": false,\n    \"supported\": false\n  },\n  \"errors\": [],\n"
        + "  \"file_name\": \"bad.mkv\",\n  \"identification_format_version\": 19,\n  \"warnings\": []\n}\n";

      Assert.Equal("mkvmerge: The file 'nothere.mkv' could not be opened for reading: open file error.",
        MkvTracks.Parse(missing).Error);
      Assert.Equal("mkvmerge cannot read this file (format not recognised or not supported)",
        MkvTracks.Parse(garbage).Error);
    }

    [Theory]
    [InlineData("eng", "en", true)]
    [InlineData("ENG", "", true)]
    [InlineData("und", "en", true)]
    [InlineData("und", "en-US", true)]
    [InlineData("", "EN-gb", true)]
    [InlineData("jpn", "ja", false)]
    [InlineData("und", "und", false)]
    [InlineData("enm", "enm", false)]
    [InlineData("", "", false)]
    public void IsEnglish_ByEitherTag(string language, string ietf, bool english)
    {
      Assert.Equal(english, new MkvTrackInfo { Language = language, LanguageIetf = ietf }.IsEnglish);
    }

    [Fact]
    public void Label_IdNameAndEvents_ForTheTable()
    {
      Assert.Equal("3 \"English (full)\" 9 ev", Tracks("multi-tracks.json")[3].Label);
      Assert.Equal("7", new MkvTrackInfo { Id = 7 }.Label);
      Assert.Equal("7 0 ev", new MkvTrackInfo { Id = 7, Events = 0 }.Label);
    }

    // ── The pick ─────────────────────────────────────────────────────────

    [Fact]
    public void Pick_MostEventsWins_ForcedAndJapaneseIgnored()
    {
      // 2 eng ASS 6, 3 eng SRT 9, 4 eng ASS forced 12, 5 jpn SRT 15: every subtitle track default.
      MkvTrackPick pick = MkvTracks.Pick(Tracks("multi-tracks.json"));

      Assert.Null(pick.Reason);
      Assert.Equal(3, pick.Track!.Id);
    }

    [Fact]
    public void Pick_IetfEnUsCounts_WebVttIgnored()
    {
      // 0 eng ASS 6, 1 en-US SRT 11, 2 eng WebVTT 20, 3 jpn SRT 15.
      MkvTrackPick pick = MkvTracks.Pick(Tracks("en-us-webvtt.json"));

      Assert.Equal(1, pick.Track!.Id);
      Assert.Equal("English (US)", pick.Track.Name);

      // By the IETF tag alone too, as a file whose old tag says nothing would have it.
      MkvTrackInfo ietfOnly = new MkvTrackInfo
      {
        Id = 1, Type = "subtitles", CodecId = "S_TEXT/UTF8", Language = "und", LanguageIetf = "en-US", Events = 11,
      };
      Assert.Equal(1, MkvTracks.Pick(new[] { Sub(0, events: 6), ietfOnly }).Track!.Id);
    }

    [Fact]
    public void Pick_IgnoresTheDefaultFlag()
    {
      MkvTrackPick pick = MkvTracks.Pick(new[] { Sub(0, events: 5, isDefault: true), Sub(1, events: 8, isDefault: false) });

      Assert.Equal(1, pick.Track!.Id);
    }

    [Fact]
    public void Pick_MissingOrZeroCounts_CountAsZero_ATieGoesToTheLowerId()
    {
      Assert.Equal(2, MkvTracks.Pick(new[] { Sub(2, events: null), Sub(3, events: null) }).Track!.Id);
      Assert.Equal(2, MkvTracks.Pick(new[] { Sub(3, events: 0), Sub(2, events: 0) }).Track!.Id);
      Assert.Equal(3, MkvTracks.Pick(new[] { Sub(2, events: null), Sub(3, events: 4) }).Track!.Id);
    }

    [Fact]
    public void Pick_SsaCounts()
    {
      Assert.Equal(5, MkvTracks.Pick(new[] { Sub(4, events: 3), Sub(5, codecId: "S_TEXT/SSA", events: 4) }).Track!.Id);
    }

    [Fact]
    public void Pick_NoSubtitleTrack_SaysSo()
    {
      MkvTrackPick pick = MkvTracks.Pick(Tracks("multi-tracks.json").Where(t => !t.IsSubtitles).ToList());

      Assert.Null(pick.Track);
      Assert.Equal("no subtitle track", pick.Reason);
    }

    [Fact]
    public void Pick_OnlyImageTracks_NoPick_WithTheReason()
    {
      // The hand-written file without its one text track (4, forced SRT): PGS and VobSub only.
      MkvTrackPick pick = MkvTracks.Pick(Tracks("image-tracks.handwritten.json").Where(t => t.Id != 4).ToList());

      Assert.Null(pick.Track);
      Assert.Equal("only image subtitle tracks (PGS/VobSub), which need OCR", pick.Reason);
    }

    [Fact]
    public void Pick_EnglishOnlyAsImageOrForced_NamesTheTracksPassedOver()
    {
      MkvTrackPick pick = MkvTracks.Pick(Tracks("image-tracks.handwritten.json"));

      Assert.Null(pick.Track);
      Assert.Equal("no English text subtitle track (2: image S_HDMV/PGS, 3: image S_VOBSUB, 4: forced)", pick.Reason);
    }

    [Fact]
    public void Pick_NoEnglishTrack_SaysSo()
    {
      MkvTrackPick japanese = MkvTracks.Pick(Tracks("multi-tracks.json").Where(t => t.Id < 2 || t.Id == 5).ToList());
      Assert.Null(japanese.Track);
      Assert.Equal("no English text subtitle track", japanese.Reason);

      MkvTrackPick webVtt = MkvTracks.Pick(Tracks("en-us-webvtt.json").Where(t => t.Id >= 2).ToList());
      Assert.Null(webVtt.Track);
      Assert.Equal("no English text subtitle track (2: S_TEXT/WEBVTT)", webVtt.Reason);
    }

    [Theory]
    [InlineData(2)] // English ASS with fewer events than the automatic pick
    [InlineData(4)] // forced
    [InlineData(5)] // Japanese: an override is how a mistagged track is used
    public void Pick_Override_AcceptsAnyTextSubtitleTrack(int id)
    {
      MkvTrackPick pick = MkvTracks.Pick(Tracks("multi-tracks.json"), id);

      Assert.Null(pick.Reason);
      Assert.Equal(id, pick.Track!.Id);
    }

    [Fact]
    public void Pick_Override_RefusesImageVideoMissingAndWebVttTracks()
    {
      Assert.Equal("track 2 (S_HDMV/PGS) is not an ASS, SSA or SRT subtitle track",
        MkvTracks.Pick(Tracks("image-tracks.handwritten.json"), 2).Reason);
      Assert.Equal("track 3 (S_VOBSUB) is not an ASS, SSA or SRT subtitle track",
        MkvTracks.Pick(Tracks("image-tracks.handwritten.json"), 3).Reason);
      Assert.Equal("track 0 (video V_MPEG4/ISO/ASP) is not an ASS, SSA or SRT subtitle track",
        MkvTracks.Pick(Tracks("multi-tracks.json"), 0).Reason);
      Assert.Equal("track 2 (S_TEXT/WEBVTT) is not an ASS, SSA or SRT subtitle track",
        MkvTracks.Pick(Tracks("en-us-webvtt.json"), 2).Reason);
      Assert.Equal("no track 9 in the file", MkvTracks.Pick(Tracks("multi-tracks.json"), 9).Reason);
      Assert.Null(MkvTracks.Pick(Tracks("multi-tracks.json"), 0).Track);
    }

    // ── Running mkvmerge (scripted) ──────────────────────────────────────

    /// <summary>An empty <c>mkvmerge</c> in the Tools Directory, so it resolves on any machine.</summary>
    private string FakeMkvMerge()
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      string exe = Path.Combine(tools, "mkvmerge");
      File.WriteAllText(exe, "");
      ConstantSettings.ToolsDir = tools;
      return exe;
    }

    private List<ProcessStartInfo> Script(int? exitCode, string stdout, string stderr = "")
    {
      var started = new List<ProcessStartInfo>();
      MkvTracks.RunnerOverride = (psi, ct) =>
      {
        started.Add(psi);
        return Task.FromResult(new CliProcessResult { ExitCode = exitCode, Stdout = stdout, Stderr = stderr });
      };
      return started;
    }

    [Fact]
    public async Task ListAsync_RunsMkvmergeJ_WithUtf8Pipes_AndReadsItsJson()
    {
      string exe = FakeMkvMerge();
      List<ProcessStartInfo> started = Script(0, Fixture("multi-tracks.json"));
      string mkv = Path.Combine(scope.TempDir, "番組 [01].mkv");

      MkvTrackList list = await MkvTracks.ListAsync(mkv);

      Assert.Null(list.Error);
      Assert.Equal(6, list.Tracks.Count);
      ProcessStartInfo psi = Assert.Single(started);
      Assert.Equal(exe, psi.FileName);
      Assert.Equal(new[] { "--output-charset", "UTF-8", "-J", mkv }, psi.ArgumentList);
      Assert.Equal("", psi.Arguments);
      Assert.False(psi.UseShellExecute);
      Assert.True(psi.CreateNoWindow);
      Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError);
      Assert.Equal("utf-8", psi.StandardOutputEncoding!.WebName);
      Assert.Equal("utf-8", psi.StandardErrorEncoding!.WebName);
      Assert.Empty(psi.StandardOutputEncoding.GetPreamble());
      if (!OperatingSystem.IsWindows())
        Assert.True(MkvTracks.IsUtf8Locale(psi.Environment));
    }

    [Fact]
    public async Task ListAsync_RelativeName_IsPassedAsAFullPath()
    {
      FakeMkvMerge();
      List<ProcessStartInfo> started = Script(0, Fixture("multi-tracks.json"));

      await MkvTracks.ListAsync("@show.mkv");

      Assert.Equal(Path.GetFullPath("@show.mkv"), started.Single().ArgumentList[3]);
    }

    [Fact]
    public async Task ListAsync_ExitCode1_IsWarnings_TheTracksAreRead()
    {
      FakeMkvMerge();
      Script(1, Fixture("multi-tracks.json"));

      MkvTrackList list = await MkvTracks.ListAsync("a.mkv");

      Assert.Null(list.Error);
      Assert.Equal(6, list.Tracks.Count);
    }

    [Fact]
    public async Task ListAsync_FailingRunner_IsAReason_NotAnException()
    {
      FakeMkvMerge();

      Script(2, "{\n  \"errors\": [\n    \"The file 'a.mkv' could not be opened for reading: open file error.\\n\"\n  ],\n  \"warnings\": []\n}\n");
      Assert.Equal("mkvmerge exited with code 2: The file 'a.mkv' could not be opened for reading: open file error.",
        (await MkvTracks.ListAsync("a.mkv")).Error);

      Script(2, "", "Error: something\nwent wrong\n\n");
      Assert.Equal("mkvmerge exited with code 2: went wrong", (await MkvTracks.ListAsync("a.mkv")).Error);

      Script(3, "", "");
      Assert.Equal("mkvmerge exited with code 3: (no output)", (await MkvTracks.ListAsync("a.mkv")).Error);

      Script(null, "");
      Assert.Equal("mkvmerge did not finish", (await MkvTracks.ListAsync("a.mkv")).Error);

      MkvTracks.RunnerOverride = (psi, ct) => throw new Win32Exception("No such file or directory");
      Assert.Equal("could not start mkvmerge: No such file or directory", (await MkvTracks.ListAsync("a.mkv")).Error);
    }

    [Fact]
    public async Task ListAsync_UnparsableOutput_IsAReason()
    {
      FakeMkvMerge();
      Script(0, "mkvmerge v82.0 ('I'm The President') 64-bit\n");

      MkvTrackList list = await MkvTracks.ListAsync("a.mkv");

      Assert.Equal("mkvmerge did not print a track list (no JSON in its output)", list.Error);
      Assert.Empty(list.Tracks);
    }

    [Fact]
    public async Task ListAsync_Cancelled_Throws()
    {
      FakeMkvMerge();
      MkvTracks.RunnerOverride = (psi, ct) => throw new OperationCanceledException(ct);

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MkvTracks.ListAsync("a.mkv", new CancellationToken(true)));
    }

    [Fact]
    public async Task ListAsync_MkvmergeNotFound_IsAReason_AndNothingRuns()
    {
      string empty = Path.Combine(scope.TempDir, "empty");
      Directory.CreateDirectory(empty);
      ConstantSettings.ToolsDir = empty;
      Environment.SetEnvironmentVariable("PATH", empty);
      ConstantSettings.MkvToolNixDirsOverride = new[] { empty };
      List<ProcessStartInfo> started = Script(0, Fixture("multi-tracks.json"));

      MkvTrackList list = await MkvTracks.ListAsync("a.mkv");

      Assert.Equal(MkvTracks.NotFoundMessage, list.Error);
      Assert.Empty(started);
    }

    [Fact]
    public async Task PickAsync_ListsThenPicks_AListingErrorIsTheReason()
    {
      FakeMkvMerge();

      Script(0, Fixture("multi-tracks.json"));
      Assert.Equal(3, (await MkvTracks.PickAsync("a.mkv")).Track!.Id);
      Assert.Equal(5, (await MkvTracks.PickAsync("a.mkv", 5)).Track!.Id);

      Script(0, "garbage");
      MkvTrackPick pick = await MkvTracks.PickAsync("a.mkv");
      Assert.Null(pick.Track);
      Assert.Equal("mkvmerge did not print a track list (no JSON in its output)", pick.Reason);
    }

    [Fact]
    public void IsUtf8Locale_FollowsLcAllThenLcCtypeThenLang()
    {
      static bool Utf8(params (string Name, string? Value)[] vars)
        => MkvTracks.IsUtf8Locale(vars.ToDictionary(v => v.Name, v => v.Value));

      Assert.False(Utf8());
      Assert.True(Utf8(("LANG", "en_US.UTF-8")));
      Assert.True(Utf8(("LC_CTYPE", "C.utf8")));
      Assert.False(Utf8(("LC_ALL", "C"), ("LANG", "en_US.UTF-8")));
      Assert.True(Utf8(("LC_ALL", ""), ("LANG", "ja_JP.UTF-8")));
      Assert.False(Utf8(("LANG", "POSIX")));
    }

    // ── Finding MKVToolNix ───────────────────────────────────────────────

    [Fact]
    public void MkvToolNixDirs_ProgramFilesFolders_OnWindowsOnly()
    {
      var env = new Dictionary<string, string?>
      {
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
      };
      string? Get(string name) => env.TryGetValue(name, out string? v) ? v : null;

      Assert.Equal(new[] { Path.Combine(@"C:\Program Files", "MKVToolNix"), Path.Combine(@"C:\Program Files (x86)", "MKVToolNix") },
        ConstantSettings.MkvToolNixDirs(isWindows: true, Get));
      Assert.Empty(ConstantSettings.MkvToolNixDirs(isWindows: false, Get));

      env["ProgramFiles(x86)"] = " ";
      Assert.Equal(new[] { Path.Combine(@"C:\Program Files", "MKVToolNix") }, ConstantSettings.MkvToolNixDirs(true, Get));
      env["ProgramFiles(x86)"] = @"C:\PROGRAM FILES";
      Assert.Single(ConstantSettings.MkvToolNixDirs(true, Get));
      env.Clear();
      Assert.Empty(ConstantSettings.MkvToolNixDirs(true, Get));
    }

    [Fact]
    public void ResolveTool_FindsMkvToolNix_InItsInstallFolder_AfterToolsDirAndPath()
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      string onPath = Path.Combine(scope.TempDir, "path");
      string install = Path.Combine(scope.TempDir, "Program Files", "MKVToolNix");
      foreach (string dir in new[] { tools, onPath, install }) Directory.CreateDirectory(dir);
      foreach (string tool in new[] { "mkvmerge", "mkvextract", "mkvinfo", "ffmpeg", "subsretimer" })
        File.WriteAllText(Path.Combine(install, tool), "");
      ConstantSettings.ToolsDir = tools;
      Environment.SetEnvironmentVariable("PATH", onPath);
      ConstantSettings.MkvToolNixDirsOverride = new[] { Path.Combine(scope.TempDir, "missing"), install };

      Assert.Equal(Path.Combine(install, "mkvmerge"), ConstantSettings.ResolveTool("mkvmerge"));
      Assert.Equal(Path.Combine(install, "mkvextract"), ConstantSettings.ResolveTool("mkvextract"));
      Assert.Equal(Path.Combine(install, "mkvinfo"), ConstantSettings.ResolveTool("mkvinfo"));
      Assert.Equal(Path.Combine(install, "mkvmerge"), ConstantSettings.PathMkvMergeExeFull);
      Assert.Equal(Path.Combine(install, "mkvextract"), ConstantSettings.PathMkvExtractExeFull);
      Assert.Equal(Path.Combine(install, "mkvinfo"), ConstantSettings.PathMkvInfoExeFull);
      // No other tool is looked for there.
      Assert.Null(ConstantSettings.ResolveTool("ffmpeg"));
      Assert.Null(ConstantSettings.ResolveTool("subsretimer"));

      // PATH, then the Tools Directory, come first.
      File.WriteAllText(Path.Combine(onPath, "mkvmerge"), "");
      Assert.Equal(Path.Combine(onPath, "mkvmerge"), ConstantSettings.ResolveTool("mkvmerge"));
      File.WriteAllText(Path.Combine(tools, "mkvmerge"), "");
      Assert.Equal(Path.Combine(tools, "mkvmerge"), ConstantSettings.ResolveTool("mkvmerge"));
    }

    // ── The real mkvmerge ────────────────────────────────────────────────

    [RequiresMkvToolnixFact]
    public async Task RealMkvmerge_GeneratedFile_JapaneseNames_ListedAndPicked()
    {
      string dir = Path.Combine(scope.TempDir, "シーズン [1]");
      Directory.CreateDirectory(dir);
      string ass = Path.Combine(dir, "en.ass"), srt = Path.Combine(dir, "en.srt"), ja = Path.Combine(dir, "ja.srt");
      File.WriteAllText(ass, Ass(3, "English line"), new UTF8Encoding(false));
      File.WriteAllText(srt, Srt(5, "English full line"), new UTF8Encoding(false));
      File.WriteAllText(ja, Srt(7, "日本語の台詞"), new UTF8Encoding(false));
      string made = Path.Combine(dir, "made.mkv");
      Mux(made, "--language", "0:eng", "--track-name", "0:English", ass,
        "--language", "0:eng", "--track-name", "0:English (full)", srt,
        "--language", "0:jpn", "--track-name", "0:日本語", ja);
      string mkv = Path.Combine(dir, "番組 [01] 'x'.mkv");
      File.Move(made, mkv);

      MkvTrackList list = await MkvTracks.ListAsync(mkv);

      Assert.Null(list.Error);
      Assert.Equal(new[] { (0, "S_TEXT/ASS", "English", 3), (1, "S_TEXT/UTF8", "English (full)", 5), (2, "S_TEXT/UTF8", "日本語", 7) },
        list.Tracks.Select(t => (t.Id, t.CodecId, t.Name, t.Events ?? -1)));
      Assert.Equal(new[] { "eng", "eng", "jpn" }, list.Tracks.Select(t => t.Language));
      MkvTrackPick pick = await MkvTracks.PickAsync(mkv);
      Assert.Equal(1, pick.Track!.Id);

      MkvTrackPick missing = await MkvTracks.PickAsync(Path.Combine(dir, "nothere.mkv"));
      Assert.Null(missing.Track);
      Assert.StartsWith("mkvmerge exited with code 2: The file ", missing.Reason);
    }

    /// <summary>mkvmerge with UTF-8 arguments for the track names, independent of the code under test.</summary>
    private static void Mux(string output, params string[] inputs)
    {
      var psi = new ProcessStartInfo(ConstantSettings.ResolveTool(ConstantSettings.ExeMkvMerge)!)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      foreach (string arg in new[] { "--command-line-charset", "UTF-8", "-q", "-o", output }.Concat(inputs))
        psi.ArgumentList.Add(arg);
      if (!OperatingSystem.IsWindows()) psi.Environment["LC_ALL"] = "C.UTF-8";
      using Process p = Process.Start(psi)!;
      Task<string> stdout = p.StandardOutput.ReadToEndAsync();
      Task<string> stderr = p.StandardError.ReadToEndAsync();
      p.WaitForExit();
      Assert.True(p.ExitCode == 0, "mkvmerge failed: " + stdout.Result + stderr.Result);
    }

    private static string Srt(int n, string text)
    {
      var sb = new StringBuilder();
      for (int i = 0; i < n; i++)
        sb.Append($"{i + 1}\n00:00:{i + 1:00},000 --> 00:00:{i + 1:00},800\n{text} {i + 1}.\n\n");
      return sb.ToString();
    }

    private static string Ass(int n, string text)
    {
      var sb = new StringBuilder("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\n"
        + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, "
        + "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, "
        + "MarginR, MarginV, Encoding\n"
        + "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n"
        + "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
      for (int i = 0; i < n; i++)
        sb.Append($"Dialogue: 0,0:00:{i + 1:00}.00,0:00:{i + 1:00}.80,Default,,0,0,0,,{text} {i + 1}.\n");
      return sb.ToString();
    }
  }
}
