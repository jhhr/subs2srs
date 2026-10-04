using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// subs2srs-cli through <see cref="CliRunner.RunAsync"/> in-process, and once as the built
  /// console: <c>go --dry-run</c> (the episode list and the checks before starting) and
  /// <c>go</c>'s runs of the pipeline, their table and exit codes. <see cref="EpisodeListTests"/>
  /// covers the list itself, <see cref="GoChecksTests"/> the checks.
  /// </summary>
  public class CliTests
  {
    private sealed record Result(int Code, string Stdout, string Stderr)
    {
      public string[] Lines => Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
      public override string ToString() => $"exit {Code}\n--- stdout\n{Stdout}--- stderr\n{Stderr}";
    }

    private static async Task<Result> Run(params string[] args)
    {
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      int code = await CliRunner.RunAsync(args, stdout, stderr);
      return new Result(code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Save the scope's settings, after <paramref name="edit"/>, as a project; then reset them so only the file carries them.</summary>
    private static string SaveProject(TestScope scope, Action<Settings>? edit = null)
    {
      var s = Settings.Instance;
      s.DeckName = "Show";
      s.OutputDir = scope.OutputDir;
      edit?.Invoke(s);
      string path = Path.Combine(scope.TempDir, "show 日本.s2s.json");
      ProjectIO.Save(path, s);
      Settings.Instance.Reset();
      return path;
    }

    /// <summary>
    /// The checks before starting need ffmpeg; these tests are about the list, so an empty file
    /// named ffmpeg in the Tools Directory stands in for it on a machine without one. A dry run
    /// starts it only in the audio-stream check, which then finds no stream. Returns the folder.
    /// </summary>
    private static string FakeFfmpeg(TestScope scope)
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      File.WriteAllText(Path.Combine(tools, "ffmpeg"), "");
      ConstantSettings.ToolsDir = tools;
      return tools;
    }

    /// <summary>A season folder (a space, brackets and Japanese in its name) holding these empty files.</summary>
    private static string MakeSeason(TestScope scope, params string[] files)
    {
      string dir = Path.Combine(scope.TempDir, "Show S1 [Grp] 日本");
      Directory.CreateDirectory(Path.Combine(dir, "s2s"));
      foreach (string f in files)
        File.WriteAllText(Path.Combine(dir, f), "");
      return dir;
    }

    private static readonly string[] SeasonMissingJp =
    {
      "[Grp] 進撃 - 01.mkv", "[Grp] 進撃 - 02.mkv", "[Grp] 進撃 - 03.mkv",
      "s2s/[Grp] 進撃 - 01.ja.srt", "s2s/[Grp] 進撃 - 01.en.ass",
      "s2s/[Grp] 進撃 - 02.en.ass",
      "s2s/[Grp] 進撃 - 03.ja.srt", "s2s/[Grp] 進撃 - 03.en.ass",
    };

    // ── the command line ────────────────────────────────────────────────

    [Theory]
    [InlineData("no command")]
    [InlineData("no command", "--project", "p.json")]
    [InlineData("go needs --project", "go")]
    [InlineData("unknown command 'run'", "run", "--project", "p.json")]
    [InlineData("--project needs a value", "go", "--project")]
    [InlineData("--season needs a value", "go", "--project", "p.json", "--season", "--dry-run")]
    [InlineData("unknown argument '--bogus'", "go", "--project", "p.json", "--bogus")]
    [InlineData("cannot be combined", "go", "--project", "p.json", "--prefs", "x.json", "--no-prefs")]
    [InlineData("--grouping takes rules or off, not 'ai'", "go", "--project", "p.json", "--grouping", "ai")]
    public async Task UsageErrors_Exit1_OnStderr(string expected, params string[] args)
    {
      using var scope = new TestScope();
      Result r = await Run(args);
      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains(expected, r.Stderr);
      Assert.Contains("--help", r.Stderr);
      Assert.Equal("", r.Stdout);
    }

    [Fact]
    public void Parse_ReadsEveryOption()
    {
      CliOptions o = CliOptions.Parse(new[] { "go", "--project", "p 日本.json", "--season", "D:\\Show [S1]", "--dry-run", "--yes", "--verbose", "--prefs", "x.json", "--grouping", "Rules" });
      Assert.Equal("go", o.Command);
      Assert.Equal("p 日本.json", o.ProjectPath);
      Assert.Equal("D:\\Show [S1]", o.SeasonDir);
      Assert.True(o.DryRun && o.Yes && o.Verbose);
      Assert.Equal("x.json", o.PrefsPath);
      Assert.False(o.NoPrefs);
      Assert.Equal(SnippetMode.Rules, o.Grouping);
      CliOptions plain = CliOptions.Parse(new[] { "go", "--project", "p", "--no-prefs", "--grouping", "off" });
      Assert.True(plain.NoPrefs);
      Assert.Equal(SnippetMode.Off, plain.Grouping);
      Assert.Null(CliOptions.Parse(new[] { "go", "--project", "p" }).Grouping);
    }

    [Fact]
    public async Task HelpAndVersion_Exit0_OnStdout()
    {
      using var scope = new TestScope();
      Result help = await Run("--help");
      Assert.Equal(0, help.Code);
      Assert.Contains("usage: subs2srs-cli go --project <file>", help.Stdout);
      Assert.Contains("--season <dir>", help.Stdout);
      Assert.Equal(0, (await Run("go", "--help")).Code);

      Result version = await Run("--version");
      Assert.Equal(0, version.Code);
      Assert.Equal("subs2srs-cli " + UtilsAssembly.Version, version.Stdout.Trim());
    }

    // ── go --dry-run ────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_Season_WithAMissingJapaneseFile_Exits3_AndTheOthersKeepTheirNumbers()
    {
      using var scope = new TestScope(" ä 日本");
      FakeFfmpeg(scope);
      string project = SaveProject(scope, s => s.EpisodeStartNumber = 1);
      string season = MakeSeason(scope, SeasonMissingJp);

      Result r = await Run("go", "--project", project, "--season", season, "--dry-run", "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      string[][] rows = r.Lines.Select(EpisodeListTests.Cells).ToArray();
      Assert.Equal(new[] { "#", "Video", "Subs1", "Subs2", "Status" }, rows[0]);
      string s2s(string name) => Path.Combine("s2s", name);
      Assert.Equal(new[] { "1", "[Grp] 進撃 - 01.mkv", s2s("[Grp] 進撃 - 01.ja.srt"), s2s("[Grp] 進撃 - 01.en.ass"), "ready" }, rows[1]);
      Assert.Equal(new[] { "2", "[Grp] 進撃 - 02.mkv", "-", s2s("[Grp] 進撃 - 02.en.ass"), "skipped: no .ja file" }, rows[2]);
      Assert.Equal(new[] { "3", "[Grp] 進撃 - 03.mkv", s2s("[Grp] 進撃 - 03.ja.srt"), s2s("[Grp] 進撃 - 03.en.ass"), "ready" }, rows[3]);
      Assert.Equal(4, rows.Length);
      Assert.Contains($"3 episode(s) in {season}: 2 ready, 1 skipped.", r.Stderr);
      // The process-wide hooks are the scope's again (its recorder answers yes).
      Assert.True(UtilsMsg.OnShowConfirm("q", ""));
      Assert.Empty(scope.Msgs.Errors);
    }

    [Fact]
    public async Task DryRun_Season_EveryEpisodeReady_Exits0_NumberedFromTheProjectsStart_AndCutAtItsEnd()
    {
      using var scope = new TestScope();
      FakeFfmpeg(scope);
      string project = SaveProject(scope, s => { s.EpisodeStartNumber = 7; s.EpisodeEndNumber = 8; });
      string season = MakeSeason(scope,
        "a.mkv", "b.mkv", "c.mkv",
        "s2s/a.ja.srt", "s2s/a.en.srt", "s2s/b.ja.srt", "s2s/b.en.srt"); // c has none, but comes after the end number

      Result r = await Run("go", "--project", project, "--season", season, "--dry-run", "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitOk, r.ToString());
      Assert.Equal(new[] { "7", "8" }, r.Lines.Skip(1).Select(l => EpisodeListTests.Cells(l)[0]));
      Assert.Contains("1 more after Episode End # 8 left out", r.Stderr);
    }

    [Fact]
    public async Task DryRun_Patterns_UseTheProjectsPatterns_AsGoDoes()
    {
      using var scope = new TestScope(" ä 日本");
      FakeFfmpeg(scope);
      var set = PatternSet.Create(scope.TempDir);
      string project = SaveProject(scope, s =>
      {
        s.Subs[0].FilePattern = set.Subs1Pattern;
        s.Subs[1].FilePattern = set.Subs2Pattern;
        s.VideoClips.FilePattern = set.VideoPattern;
        s.EpisodeStartNumber = PatternSet.Start;
        s.EpisodeEndNumber = PatternSet.End;
        s.Snapshots.Enabled = true;
      });

      Result r = await Run("go", "--project", project, "--dry-run", "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitOk, r.ToString());
      string[][] rows = r.Lines.Select(EpisodeListTests.Cells).ToArray();
      Assert.Equal(4, rows.Length);
      Assert.Equal(new[] { "2", Path.GetFileName(set.Video[0]), Path.GetFileName(set.Subs1[0]), Path.GetFileName(set.Subs2[0]), "ready" }, rows[1]);
      Assert.Equal(new[] { "4", Path.GetFileName(set.Video[2]), Path.GetFileName(set.Subs1[2]), Path.GetFileName(set.Subs2[2]), "ready" }, rows[3]);
    }

    [Fact]
    public async Task DryRun_Patterns_WithUnequalCounts_Exits1_WithTheListsSideBySide()
    {
      using var scope = new TestScope();
      var set = PatternSet.Create(scope.TempDir);
      File.Delete(set.AllSubs2[3]);
      string project = SaveProject(scope, s =>
      {
        s.Subs[0].FilePattern = set.Subs1Pattern;
        s.Subs[1].FilePattern = set.Subs2Pattern;
        s.VideoClips.FilePattern = set.VideoPattern;
        s.Snapshots.Enabled = true;
      });

      Result r = await Run("go", "--project", project, "--dry-run", "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Equal("", r.Stdout);
      Assert.Contains("(4 Subs1, 3 Subs2, 4 Video)", r.Stderr);
      Assert.Contains(r.Stderr.Split(Environment.NewLine), l => l.StartsWith("4  [Grp] Show - 04.ja.srt", StringComparison.Ordinal) && l.Contains(" -  "));
    }

    /// <summary>
    /// The checks run after the list: each error on stderr under the table, exit 1. Snippets
    /// grouped by AI through <c>claude</c> need it whatever the AI Grouping On Go preference says
    /// (off here): the command line's pre-pass always asks the model.
    /// </summary>
    [Fact]
    public async Task DryRun_WithFailedChecks_ListsThemUnderTheTable_AndExits1()
    {
      using var scope = new TestScope();
      FakeFfmpeg(scope);
      string file = Path.Combine(scope.TempDir, "a file");
      File.WriteAllText(file, "");
      string outputDir = Path.Combine(file, "out");
      string project = SaveProject(scope, s =>
      {
        s.OutputDir = outputDir;
        s.DeckName = "";
        s.Snippets.Mode = SnippetMode.AI;
        s.Snippets.AiModel = "terminal-claude-sonnet-5";
      });
      string season = MakeSeason(scope, SeasonMissingJp);

      Result r;
      try
      {
        ClaudeCliProvider.ExecutableOverride = "";
        r = await Run("go", "--project", project, "--season", season, "--dry-run", "--no-prefs");
      }
      finally
      {
        ClaudeCliProvider.ExecutableOverride = null;
      }

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Equal(4, r.Lines.Length); // the table all the same
      string[] errors = r.Stderr.Split(Environment.NewLine).Where(l => l.StartsWith("error: ", StringComparison.Ordinal)).ToArray();
      Assert.Equal(3, errors.Length);
      Assert.StartsWith($"error: Cannot write to output directory \"{outputDir}\": ", errors[0]);
      Assert.Equal("error: Please provide Deck Name.", errors[1]);
      Assert.Equal("error: Snippets are grouped by AI with terminal-claude-sonnet-5. " + ClaudeCliProvider.NoCliMessage, errors[2]);
      Assert.Contains("2 ready, 1 skipped. Dry run: nothing was made.", r.Stderr);
    }

    /// <summary>
    /// A warning is listed with what go would do at it, which <c>--yes</c> decides; the exit code
    /// stays the list's. Stream #1 is missing from both videos (the test video has one).
    /// </summary>
    [RequiresFfmpegFact]
    public async Task DryRun_WithAnAudioStreamWarning_SaysWhatGoWouldDo_AndKeepsTheListsExitCode()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      string project = SaveProject(scope, s => s.VideoClips.AudioStream = new InfoStream("0:2", "1", "", ""));
      string season = MakeSeason(scope, "s2s/a.ja.srt", "s2s/a.en.srt", "s2s/b.ja.srt", "s2s/b.en.srt");
      File.Copy(TestMedia.VideoPath, Path.Combine(season, "a.mkv"));
      File.Copy(TestMedia.VideoPath, Path.Combine(season, "b.mkv"));

      Result no = await Run("go", "--project", project, "--season", season, "--dry-run", "--no-prefs");
      Assert.True(no.Code == CliOptions.ExitOk, no.ToString());
      string[] lines = no.Stderr.Split(Environment.NewLine);
      Assert.Contains("warning: Audio stream #1 issues:", lines);
      Assert.Contains("  Stream not found in episodes: 1, 2", lines);
      Assert.Contains("  Continue anyway?", lines);
      Assert.Contains("Without --yes, go answers no to the warning and stops; with --yes it goes on.", lines);
      Assert.DoesNotContain(lines, l => l.StartsWith("error: ", StringComparison.Ordinal));

      Result yes = await Run("go", "--project", project, "--season", season, "--dry-run", "--no-prefs", "--yes");
      Assert.True(yes.Code == CliOptions.ExitOk, yes.ToString());
      Assert.Contains("With --yes, go answers yes to the warning and goes on.", yes.Stderr.Split(Environment.NewLine));
    }

    [Fact]
    public async Task MissingProject_BadProject_MissingSeason_OrAudioFromFiles_Exit1()
    {
      using var scope = new TestScope();
      string season = MakeSeason(scope, SeasonMissingJp);

      Result missing = await Run("go", "--project", Path.Combine(scope.TempDir, "nope.s2s.json"), "--season", season, "--dry-run", "--no-prefs");
      Assert.True(missing.Code == CliOptions.ExitError, missing.ToString());
      Assert.Contains("project file not found", missing.Stderr);

      string bad = Path.Combine(scope.TempDir, "bad.s2s.json");
      File.WriteAllText(bad, "{ not json");
      Result unreadable = await Run("go", "--project", bad, "--season", season, "--dry-run", "--no-prefs");
      Assert.True(unreadable.Code == CliOptions.ExitError, unreadable.ToString());
      Assert.Contains("cannot read the project", unreadable.Stderr);

      string project = SaveProject(scope);
      Result noSeason = await Run("go", "--project", project, "--season", Path.Combine(scope.TempDir, "nope"), "--dry-run", "--no-prefs");
      Assert.True(noSeason.Code == CliOptions.ExitError, noSeason.ToString());
      Assert.Contains("season folder not found", noSeason.Stderr);

      string fromFiles = SaveProject(scope, s => { s.AudioClips.Enabled = true; s.AudioClips.UseAudioFromVideo = false; s.AudioClips.UseExistingAudio = true; });
      Result audio = await Run("go", "--project", fromFiles, "--season", season, "--dry-run", "--no-prefs");
      Assert.True(audio.Code == CliOptions.ExitError, audio.ToString());
      Assert.Contains("audio files", audio.Stderr);
      Assert.Equal("", audio.Stdout);
    }

    // ── go: the run ─────────────────────────────────────────────────────

    /// <summary>
    /// A project for a short real run: audio clips from the video and snapshots (no video
    /// clips or animated snapshots), snippets grouped by the rules.
    /// </summary>
    private static string SaveRunProject(TestScope scope, Action<Settings>? edit = null) => SaveProject(scope, s =>
    {
      s.Subs[0].Encoding = "utf-8";
      s.Subs[1].Encoding = "utf-8";
      s.AudioClips.Enabled = true;
      s.AudioClips.UseAudioFromVideo = true;
      s.AudioClips.UseExistingAudio = false;
      s.AudioClips.AudioFormat = "MP3";
      s.AudioClips.Bitrate = 64;
      s.AudioClips.Normalize = false;
      s.AudioClips.PadEnabled = false;
      s.VideoClips.AudioStream = new InfoStream("0:a:0", "0", "", "Default"); // as the GUI saves it
      s.Snapshots.Enabled = true;
      s.VideoClips.Enabled = false;
      s.AnimatedSnapshots.Enabled = false;
      s.Snippets.Mode = SnippetMode.Rules;
      s.EpisodeStartNumber = 1;
      edit?.Invoke(s);
    });

    /// <summary>
    /// The test video as <paramref name="dir"/>/<paramref name="name"/>.mkv (ffmpeg reads it by
    /// its content), and the dialogue as its .ja and its .en file in <paramref name="subsDir"/>:
    /// the rules group its four lines into three cards.
    /// </summary>
    private static void RealEpisode(string dir, string subsDir, string name, bool ja = true, bool en = true)
    {
      File.Copy(TestMedia.VideoPath, Path.Combine(dir, name + ".mkv"), overwrite: true);
      if (ja) File.Move(TestMedia.WriteDialogueSrt(dir), Path.Combine(subsDir, name + ".ja.srt"));
      if (en) File.Move(TestMedia.WriteDialogueTranslationSrt(dir), Path.Combine(subsDir, name + ".en.srt"));
    }

    /// <summary>The tag (first column) of every card in the TSV.</summary>
    private static string[] Tags(string tsv)
      => File.ReadAllLines(tsv, Encoding.UTF8).Where(l => l.Length > 0).Select(l => l.Split('\t')[0]).ToArray();

    /// <summary>
    /// Ten videos, so the names are padded to two digits; only episodes 1 and 3 have both
    /// subtitles (2 lacks its .ja file, 4 to 10 have none and are empty files, never read). The
    /// two run in one pipeline under their own numbers, padded for the season's ten episodes as
    /// a run of all ten would pad them, not for the two that ran.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Go_Season_WithEpisode2sJapaneseFileMissing_Exits3_AndMakesTheOthersCardsUnderTheirNumbers()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope(" ä 日本");
      string project = SaveRunProject(scope);
      string season = MakeSeason(scope, Enumerable.Range(4, 7).Select(n => $"[Grp] 進撃 - {n:00}.mkv").ToArray());
      string subs = Path.Combine(season, "s2s");
      RealEpisode(season, subs, "[Grp] 進撃 - 01");
      RealEpisode(season, subs, "[Grp] 進撃 - 02", ja: false);
      RealEpisode(season, subs, "[Grp] 進撃 - 03");

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      string[][] rows = r.Lines.Select(EpisodeListTests.Cells).ToArray();
      Assert.Equal(new[] { "#", "Episode", "Status", "Cards" }, rows[0]);
      Assert.Equal(new[] { "1", "[Grp] 進撃 - 01", "done", "3" }, rows[1]);
      Assert.Equal(new[] { "2", "[Grp] 進撃 - 02", "skipped: no .ja file", "-" }, rows[2]);
      Assert.Equal(new[] { "3", "[Grp] 進撃 - 03", "done", "3" }, rows[3]);
      Assert.Equal(new[] { "10", "[Grp] 進撃 - 10", "skipped: no .ja file; no .en file", "-" }, rows[10]);
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");
      Assert.Equal($"season TSV: {tsv} (2 of 10 episodes); exit 3", r.Lines[11]);
      Assert.Equal(12, r.Lines.Length);

      Assert.Equal(new[] { "Show_01", "Show_01", "Show_01", "Show_03", "Show_03", "Show_03" }, Tags(tsv));
      string[] media = Directory.GetFiles(Path.Combine(scope.OutputDir, "Show.media")).Select(f => Path.GetFileName(f)).ToArray();
      Assert.Equal(12, media.Length); // an mp3 and a jpg per card
      Assert.All(media, m => Assert.True(m.StartsWith("Show_01_", StringComparison.Ordinal) || m.StartsWith("Show_03_", StringComparison.Ordinal), m));
      Assert.Contains(media, m => m.StartsWith("Show_03_", StringComparison.Ordinal) && m.EndsWith(".mp3", StringComparison.Ordinal));
      // Progress on stderr, a line per step.
      Assert.Contains("Step 1 of 7: Combine subs", r.Stderr.Split(Environment.NewLine));
      Assert.Contains($"10 episode(s) in {season}: 2 ready, 8 skipped.", r.Stderr);
    }

    /// <summary>
    /// The project's patterns, equal counts: exit 0. The project groups by AI with the AI Grouping
    /// On Go preference on; <c>--grouping rules</c> keeps the pipeline's own AI step from running.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Go_Patterns_WithEqualCounts_Exits0_AndGroupingRules_KeepsTheModelOut()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope(" ä 日本");
      string dir = Path.Combine(scope.TempDir, "Show 日本");
      Directory.CreateDirectory(dir);
      RealEpisode(dir, dir, "ep 01");
      RealEpisode(dir, dir, "ep 02");
      string project = SaveRunProject(scope, s =>
      {
        s.Subs[0].FilePattern = Path.Combine(dir, "*.ja.srt");
        s.Subs[1].FilePattern = Path.Combine(dir, "*.en.srt");
        s.VideoClips.FilePattern = Path.Combine(dir, "*.mkv");
        s.Snippets.Mode = SnippetMode.AI;
        s.Snippets.AiModel = "fake-model";
      });
      ConstantSettings.AiGroupingOnGo = true; // kept by --no-prefs
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll());
      using var _ = fake.Install();

      Result r = await Run("go", "--project", project, "--grouping", "rules", "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitOk, r.ToString());
      Assert.Empty(fake.Requests);
      string[][] rows = r.Lines.Select(EpisodeListTests.Cells).ToArray();
      Assert.Equal(new[] { "1", "ep 01", "done", "3" }, rows[1]); // the model would join all four
      Assert.Equal(new[] { "2", "ep 02", "done", "3" }, rows[2]);
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");
      Assert.Equal($"TSV: {tsv} (2 of 2 episodes); exit 0", r.Lines[3]);
      Assert.Equal(new[] { "Show_1", "Show_1", "Show_1", "Show_2", "Show_2", "Show_2" }, Tags(tsv));
    }

    /// <summary>
    /// A text file as the video: the audio step fails after the TSV is written. The result's
    /// message, exit 1, and the TSV, whose cards lack their media, deleted.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Go_WhenAStepFails_Exits1_WithItsMessage_AndDeletesTheUnfinishedTsv()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      string project = SaveRunProject(scope);
      string season = MakeSeason(scope);
      RealEpisode(season, Path.Combine(season, "s2s"), "a");
      File.WriteAllText(Path.Combine(season, "a.mkv"), "This is not a video.");

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");
      string[] errors = r.Stderr.Split(Environment.NewLine);
      Assert.Contains(errors, l => l.StartsWith("subs2srs-cli: Generate audio clips failed: ffmpeg exited with code ", StringComparison.Ordinal));
      Assert.Contains($"subs2srs-cli: deleted {tsv}: the run stopped after writing it, before the media of its cards were made.", errors);
      Assert.False(File.Exists(tsv));
      Assert.Equal(new[] { "1", "a", "failed", "-" }, EpisodeListTests.Cells(r.Lines[1]));
      Assert.Equal("season TSV: not written (0 of 1 episodes); exit 1", r.Lines[2]);
    }

    /// <summary>Nothing is ready: the table, exit 3, and the pipeline never starts (no ffmpeg needed).</summary>
    [Fact]
    public async Task Go_WithNoEpisodeReady_PrintsTheTable_Exits3_AndMakesNothing()
    {
      using var scope = new TestScope();
      string project = SaveRunProject(scope);
      string season = MakeSeason(scope, "a.mkv", "b.mkv", "s2s/b.ja.srt");

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      Assert.Equal(new[] { "1", "a", "skipped: no .ja file; no .en file", "-" }, EpisodeListTests.Cells(r.Lines[1]));
      Assert.Equal(new[] { "2", "b", "skipped: no .en file", "-" }, EpisodeListTests.Cells(r.Lines[2]));
      Assert.Equal("season TSV: not written (0 of 2 episodes); exit 3", r.Lines[3]);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    /// <summary>A failed check stops go before the run: the errors on stderr, exit 1, nothing made.</summary>
    [Fact]
    public async Task Go_WithAFailedCheck_Exits1_AndMakesNothing()
    {
      using var scope = new TestScope();
      FakeFfmpeg(scope);
      string project = SaveRunProject(scope, s => s.DeckName = "");
      string season = MakeSeason(scope, SeasonMissingJp);

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains("error: Please provide Deck Name.", r.Stderr.Split(Environment.NewLine));
      Assert.Contains("subs2srs-cli: nothing was made: the checks before starting found the errors above.", r.Stderr);
      Assert.Equal("", r.Stdout);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    /// <summary>
    /// A warning of the checks (the audio stream is missing from both videos) is a question
    /// that only <c>--yes</c> answers yes: without it go stops before the run.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Go_AtAWarning_WithoutYes_Exits1_AndMakesNothing()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      string project = SaveRunProject(scope, s => s.VideoClips.AudioStream = new InfoStream("0:2", "1", "", ""));
      string season = MakeSeason(scope);
      RealEpisode(season, Path.Combine(season, "s2s"), "a");
      RealEpisode(season, Path.Combine(season, "s2s"), "b");

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains("subs2srs-cli: nothing was made: the answer to the warning above was no; with --yes go answers yes and goes on.", r.Stderr);
      Assert.Equal("", r.Stdout);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    /// <summary>Snippet mode AI needs the AI pre-pass, which go does not have yet: refused before anything is read or written.</summary>
    [Fact]
    public async Task Go_InAiMode_WithoutGrouping_IsRefused()
    {
      using var scope = new TestScope();
      string project = SaveRunProject(scope, s => s.Snippets.Mode = SnippetMode.AI);
      string season = MakeSeason(scope, SeasonMissingJp);

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains("go cannot ask the model yet", r.Stderr);
      Assert.Contains("--grouping rules", r.Stderr);
      Assert.Equal("", r.Stdout);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    // ── preferences ─────────────────────────────────────────────────────

    [Fact]
    public async Task Preferences_AreRead_FromAnyFileName_AndNeverWritten()
    {
      using var scope = new TestScope();
      FakeFfmpeg(scope);
      string project = SaveProject(scope);
      string season = MakeSeason(scope, SeasonMissingJp);

      // The user's preferences.json does not exist: the defaults, and no file is created
      // (PrefIO.read would write one).
      Result none = await Run("go", "--project", project, "--season", season, "--dry-run");
      Assert.False(File.Exists(scope.PreferencesJsonPath), "preferences.json was written");
      Assert.True(none.Code == CliOptions.ExitSkipped, none.ToString());
      Assert.Contains("no preferences at", none.Stderr);

      // --prefs reads the given file, whatever its name, and leaves it as it was.
      string cache = Path.Combine(scope.TempDir, "cache 日本");
      ConstantSettings.Prefs.AiCacheDir = cache;
      PrefIO.Write();
      string mine = Path.Combine(scope.TempDir, "mine", "my prefs.json");
      Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
      File.Move(scope.PreferencesJsonPath, mine);
      byte[] before = File.ReadAllBytes(mine);
      ConstantSettings.Prefs.AiCacheDir = Path.Combine(scope.TempDir, "ai-cache");

      Result given = await Run("go", "--project", project, "--season", season, "--dry-run", "--prefs", mine);
      Assert.True(given.Code == CliOptions.ExitSkipped, given.ToString());
      Assert.Equal(cache, ConstantSettings.AiCacheDir);
      Assert.Equal(before, File.ReadAllBytes(mine));
      Assert.False(File.Exists(scope.PreferencesJsonPath));

      Result missing = await Run("go", "--project", project, "--dry-run", "--prefs", Path.Combine(scope.TempDir, "nope.json"));
      Assert.True(missing.Code == CliOptions.ExitError, missing.ToString());
      Assert.Contains("preferences file not found", missing.Stderr);
    }

    // ── the built console ───────────────────────────────────────────────

    /// <summary>
    /// A redirected stdout and stderr carry Japanese names as UTF-8 without a byte order mark,
    /// whatever the locale says: LC_ALL names Latin-1 here, which .NET would write in on Linux
    /// (Windows ignores it; there the console's code page would be used).
    /// </summary>
    [Fact]
    public async Task RealProcess_JapaneseNames_ReachARedirectedCallerAsUtf8()
    {
      using var scope = new TestScope(" ä 日本");
      string project = SaveProject(scope);
      string season = MakeSeason(scope, SeasonMissingJp);

      string dll = Path.Combine(AppContext.BaseDirectory, "subs2srs-cli.dll");
      Assert.True(File.Exists(dll), "subs2srs-cli.dll is not next to the tests");
      var psi = new ProcessStartInfo("dotnet")
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardErrorEncoding = new UTF8Encoding(false),
        UseShellExecute = false,
        WorkingDirectory = scope.TempDir,
      };
      psi.Environment["LC_ALL"] = "en_US.ISO-8859-1";
      psi.Environment["PATH"] = FakeFfmpeg(scope) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
      foreach (string a in new[] { dll, "go", "--project", project, "--season", season, "--dry-run", "--no-prefs" })
        psi.ArgumentList.Add(a);
      using Process process = Process.Start(psi)!;
      Task<string> stderrTask = process.StandardError.ReadToEndAsync();
      using var raw = new MemoryStream(); // the bytes: a reader would drop a byte order mark
      await process.StandardOutput.BaseStream.CopyToAsync(raw);
      string stderr = await stderrTask;
      await process.WaitForExitAsync();
      byte[] bytes = raw.ToArray();
      string stdout = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);

      Assert.True(process.ExitCode == CliOptions.ExitSkipped, $"exit {process.ExitCode}\n{stdout}\n{stderr}");
      Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "a byte order mark on stdout");
      Assert.StartsWith("#", stdout);
      Assert.Contains("[Grp] 進撃 - 01.mkv", stdout);
      Assert.Contains(Path.Combine("s2s", "[Grp] 進撃 - 03.ja.srt"), stdout);
      Assert.Contains($"in {season}: 2 ready, 1 skipped", stderr);
    }
  }
}
