using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Cli
{
  /// <summary>
  /// subs2srs-cli behind <see cref="Program.Main"/>: parse, bootstrap the app's singletons the
  /// way the GUI's start does (without GTK), run the command and turn failures into exit codes.
  /// Tests call <see cref="RunAsync"/> in-process with their own writers inside a TestScope.
  /// </summary>
  internal static class CliRunner
  {
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken token = default)
    {
      CliOptions options;
      try
      {
        options = CliOptions.Parse(args);
      }
      catch (CliException ex)
      {
        stderr.WriteLine("subs2srs-cli: " + ex.Message);
        stderr.WriteLine("Run with --help for usage.");
        return ex.ExitCode;
      }
      if (options.Help)
      {
        stdout.WriteLine(CliOptions.Usage);
        return CliOptions.ExitOk;
      }
      if (options.Version)
      {
        stdout.WriteLine("subs2srs-cli " + UtilsAssembly.Version);
        return CliOptions.ExitOk;
      }

      // Process-wide hooks, put back on the way out for in-process callers (the tests).
      Action<string>? echo = Logger.Instance.Echo;
      Func<string, string, bool> confirm = UtilsMsg.OnShowConfirm;
      try
      {
        // Shift-JIS and the other legacy code pages the Subs1/Subs2 encodings may name.
        UtilsCommon.RegisterEncodings();
        ReadPreferences(options, stderr);
        // No log file from the console; the app log is echoed to stderr on --verbose instead.
        ConstantSettings.EnableLogging = false;
        if (options.Verbose) Logger.Instance.Echo = line => stderr.WriteLine(line);
        // UtilsMsg already writes every error, message and question to stderr; only the
        // answer to a question is decided here. The error and info hooks stay unset.
        UtilsMsg.OnShowConfirm = (_, _) => options.Yes;

        return await GoAsync(options, stdout, stderr, token);
      }
      catch (CliException ex)
      {
        stderr.WriteLine("subs2srs-cli: " + ex.Message);
        return ex.ExitCode;
      }
      catch (OperationCanceledException)
      {
        stderr.WriteLine("subs2srs-cli: cancelled.");
        return CliOptions.ExitCancelled;
      }
      catch (Exception ex)
      {
        stderr.WriteLine("subs2srs-cli: unexpected error: " + ex);
        return CliOptions.ExitError;
      }
      finally
      {
        Logger.Instance.Echo = echo;
        UtilsMsg.OnShowConfirm = confirm;
      }
    }

    /// <summary>
    /// The GUI's preferences (tools folder, AI settings and cache), read and never written:
    /// <see cref="PrefIO.read"/> would create a missing file and migrate an old one. With
    /// <c>--no-prefs</c> the preferences in memory stay as they are (the defaults in a new process).
    /// </summary>
    private static void ReadPreferences(CliOptions options, TextWriter stderr)
    {
      if (options.NoPrefs) return;
      string path = options.PrefsPath ?? PrefIO.JsonPath;
      if (!File.Exists(path))
      {
        if (options.PrefsPath != null) throw new CliException("preferences file not found: " + path);
        stderr.WriteLine($"subs2srs-cli: no preferences at {path}; using the defaults.");
        return;
      }
      try
      {
        PrefIO.ReadFile(path);
      }
      catch (Exception ex)
      {
        throw new CliException($"cannot read the preferences {path}: {ex.Message}");
      }
    }

    private static Task<int> GoAsync(CliOptions options, TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
      if (!options.DryRun)
        throw new CliException("go without --dry-run is not built yet; --dry-run prints the episode list.");

      LoadProject(options.ProjectPath);
      token.ThrowIfCancellationRequested();
      EpisodeList list = options.SeasonDir != null
        ? EpisodeList.FromSeason(options.SeasonDir, Settings.Instance)
        : EpisodeList.FromPatterns(Settings.Instance);

      PrintEpisodes(list, stdout);
      List<GoProblem> problems = RunChecks(list);
      PrintChecks(problems, options.Yes, stderr);
      int skipped = list.SkippedCount;
      string from = list.SeasonDir != null ? "in " + list.SeasonDir : "from the project's patterns";
      string leftOut = list.LeftOut > 0
        ? $"; {list.LeftOut} more after Episode End # {Settings.Instance.EpisodeEndNumber} left out"
        : "";
      stderr.WriteLine(FormattableString.Invariant(
        $"{list.Episodes.Count} episode(s) {from}: {list.Episodes.Count - skipped} ready, {skipped} skipped{leftOut}. Dry run: nothing was made."));
      // A warning leaves the exit code alone: whether go would stop at it is up to --yes.
      if (problems.Exists(p => p.IsError)) return Task.FromResult(CliOptions.ExitError);
      return Task.FromResult(skipped > 0 ? CliOptions.ExitSkipped : CliOptions.ExitOk);
    }

    /// <summary>
    /// The checks the GUI's Go makes too (<see cref="GoChecks"/>), over the episodes that would
    /// run. The command line asks the model whenever the project groups snippets by AI,
    /// whatever the AI Grouping On Go preference says.
    /// </summary>
    private static List<GoProblem> RunChecks(EpisodeList list)
    {
      Settings s = Settings.Instance;
      // Pattern mode resolved the project's own files; in season mode the run's videos are the ready episodes'.
      if (list.SeasonDir != null)
        s.VideoClips.Files = list.Episodes.Where(e => !e.Skipped && e.Video != null).Select(e => e.Video!).ToArray();
      return GoChecks.Run(s, GoChecks.AudioStreamIndex(s), s.Snippets.Mode == SnippetMode.AI);
    }

    /// <summary>
    /// The checks' findings on stderr, under the table: "error: " or "warning: " and the message
    /// (its further lines indented), then what a run would do at a warning, which <c>--yes</c> decides.
    /// </summary>
    internal static void PrintChecks(List<GoProblem> problems, bool yes, TextWriter stderr)
    {
      if (problems.Count == 0)
      {
        stderr.WriteLine("Checks before starting: all passed.");
        return;
      }
      foreach (GoProblem p in problems)
      {
        string[] lines = p.Message.Replace("\r\n", "\n").TrimEnd().Split('\n');
        stderr.WriteLine((p.IsError ? "error: " : "warning: ") + lines[0]);
        foreach (string line in lines.Skip(1))
          stderr.WriteLine(line.Length > 0 ? "  " + line : "");
      }
      if (problems.Exists(p => !p.IsError))
        stderr.WriteLine(yes
          ? "With --yes, go answers yes to the warning and goes on."
          : "Without --yes, go answers no to the warning and stops; with --yes it goes on.");
    }

    private static void LoadProject(string path)
    {
      if (!File.Exists(path)) throw new CliException("project file not found: " + path);
      try
      {
        // Restores every setting but the Files arrays, which are never saved.
        ProjectIO.Load(path);
      }
      catch (Exception ex)
      {
        throw new CliException($"cannot read the project {path}: {ex.Message}");
      }
    }

    /// <summary>
    /// One row per episode: number, video, Subs1, Subs2 (and audio file), and "ready" or why
    /// it is skipped. Season paths are shown relative to the season folder, pattern-mode
    /// paths by file name.
    /// </summary>
    internal static void PrintEpisodes(EpisodeList list, TextWriter stdout)
    {
      bool audio = list.Episodes.Exists(e => e.Audio != null);
      var table = audio
        ? new TextTable("#", "Video", "Subs1", "Subs2", "Audio", "Status")
        : new TextTable("#", "Video", "Subs1", "Subs2", "Status");
      foreach (Episode e in list.Episodes)
      {
        string status = e.Skipped ? "skipped: " + e.SkipReason : "ready";
        string number = e.Number.ToString(CultureInfo.InvariantCulture);
        if (audio)
          table.Add(number, Show(list, e.Video), Show(list, e.Subs1), Show(list, e.Subs2), Show(list, e.Audio), status);
        else
          table.Add(number, Show(list, e.Video), Show(list, e.Subs1), Show(list, e.Subs2), status);
      }
      foreach (string line in table.Lines())
        stdout.WriteLine(line);
    }

    private static string Show(EpisodeList list, string? path)
    {
      if (path == null) return "-";
      return list.SeasonDir != null ? Path.GetRelativePath(list.SeasonDir, path) : Path.GetFileName(path);
    }
  }
}
