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
  /// console. In this phase <c>go</c> resolves and prints the episode list (<c>--dry-run</c>)
  /// and refuses to run. <see cref="EpisodeListTests"/> covers the list itself.
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
      CliOptions o = CliOptions.Parse(new[] { "go", "--project", "p 日本.json", "--season", "D:\\Show [S1]", "--dry-run", "--yes", "--verbose", "--prefs", "x.json" });
      Assert.Equal("go", o.Command);
      Assert.Equal("p 日本.json", o.ProjectPath);
      Assert.Equal("D:\\Show [S1]", o.SeasonDir);
      Assert.True(o.DryRun && o.Yes && o.Verbose);
      Assert.Equal("x.json", o.PrefsPath);
      Assert.False(o.NoPrefs);
      Assert.True(CliOptions.Parse(new[] { "go", "--project", "p", "--no-prefs" }).NoPrefs);
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

    [Fact]
    public async Task Go_WithoutDryRun_IsNotBuiltYet()
    {
      using var scope = new TestScope();
      string project = SaveProject(scope);
      string season = MakeSeason(scope, SeasonMissingJp);

      Result r = await Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains("not built yet", r.Stderr);
      Assert.Equal("", r.Stdout);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
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

    // ── preferences ─────────────────────────────────────────────────────

    [Fact]
    public async Task Preferences_AreRead_FromAnyFileName_AndNeverWritten()
    {
      using var scope = new TestScope();
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
