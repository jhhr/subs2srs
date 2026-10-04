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

    /// <summary>
    /// <c>go</c>: load the project, resolve the episodes and set up the run; then with
    /// <c>--dry-run</c> print the list and the checks, otherwise check, run the pipeline once
    /// over every ready episode and print the table.
    /// </summary>
    private static async Task<int> GoAsync(CliOptions options, TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
      LoadProject(options.ProjectPath);
      Settings s = Settings.Instance;
      if (options.Grouping is SnippetMode grouping)
        s.Snippets.Mode = grouping;
      else if (s.Snippets.Mode == SnippetMode.AI && !options.DryRun)
        // So the mode is never AI when the pipeline starts, and its own AI step
        // (WorkerSubs.aiGroupingOnGoApplies) never runs from here.
        throw new CliException("the project groups snippets by AI, and go cannot ask the model yet (its AI pre-pass "
          + "is not built yet). Run with --grouping rules or --grouping off to group them without the model.");
      token.ThrowIfCancellationRequested();
      EpisodeList list = options.SeasonDir != null
        ? EpisodeList.FromSeason(options.SeasonDir, s)
        : EpisodeList.FromPatterns(s);
      List<Episode> ready = list.Episodes.Where(e => !e.Skipped).ToList();
      // Pattern mode: ProjectFiles.Resolve has set up the files, numbered from the start number.
      if (list.SeasonDir != null) SetUpSeasonRun(s, list, ready);

      if (options.DryRun)
      {
        PrintEpisodes(list, stdout);
        List<GoProblem> found = RunChecks(s);
        PrintChecks(found, options.Yes, stderr);
        stderr.WriteLine(Summary(list) + " Dry run: nothing was made.");
        // A warning leaves the exit code alone: whether go would stop at it is up to --yes.
        if (found.Exists(p => p.IsError)) return CliOptions.ExitError;
        return list.SkippedCount > 0 ? CliOptions.ExitSkipped : CliOptions.ExitOk;
      }

      stderr.WriteLine(Summary(list));
      if (ready.Count == 0)
      {
        PrintTable(list, null, CliOptions.ExitSkipped, stdout);
        return CliOptions.ExitSkipped;
      }
      List<GoProblem> problems = RunChecks(s);
      if (problems.Exists(p => p.IsError))
      {
        PrintChecks(problems, options.Yes, stderr);
        throw new CliException("nothing was made: the checks before starting found the errors above.");
      }
      // UtilsMsg puts each warning on stderr as a question, which --yes answers.
      foreach (GoProblem warning in problems)
        if (!UtilsMsg.showConfirm(warning.Message))
          throw new CliException("nothing was made: the answer to the warning above was no; with --yes go answers yes and goes on.");
      token.ThrowIfCancellationRequested();

      var progress = new ConsoleProgress(stderr, token);
      // The pipeline reports its end through UtilsMsg too (on stderr): the time taken, or the
      // error with its stack trace, or "Action cancelled.".
      PipelineResult result = await new SubsProcessor().StartAsync(progress);
      progress.Done();

      if (result.Status == PipelineStatus.Failed)
        stderr.WriteLine("subs2srs-cli: " + result.Message);
      if (result.Status != PipelineStatus.Completed && result.ImportFile != null && File.Exists(result.ImportFile))
        DeleteUnfinishedImportFile(result.ImportFile, stderr);
      int code = result.Status switch
      {
        PipelineStatus.Completed => list.SkippedCount > 0 ? CliOptions.ExitSkipped : CliOptions.ExitOk,
        PipelineStatus.Cancelled => CliOptions.ExitCancelled,
        _ => CliOptions.ExitError,
      };
      PrintTable(list, result, code, stdout);
      return code;
    }

    /// <summary>
    /// The settings of a season run, set after <see cref="ProjectIO.Load"/>, which clears them:
    /// the ready episodes' files in order, their numbers, and the name padding of the whole season.
    /// </summary>
    private static void SetUpSeasonRun(Settings s, EpisodeList list, List<Episode> ready)
    {
      s.Subs[0].Files = ready.Select(e => e.Subs1!).ToArray();
      s.Subs[1].Files = ready.Select(e => e.Subs2!).ToArray();
      // The pipeline reads the patterns only for whether there is a Subs2 and whether the
      // subtitles are VobSub (an .idx among the files they match). The first episode's own
      // file answers both: a name, not a wildcard (a season's .ja and .en files are text).
      s.Subs[0].FilePattern = ready.Count > 0 ? ready[0].Subs1! : "";
      s.Subs[1].FilePattern = ready.Count > 0 ? ready[0].Subs2! : "";
      s.VideoClips.FilePattern = Path.Combine(list.SeasonDir!, "*.mkv"); // only logged
      s.VideoClips.Files = ready.Select(e => e.Video!).ToArray();
      // Audio comes from the videos (FromSeason refuses audio files), but the audio worker
      // reads this list whenever it is not empty, and a project may keep an old pattern.
      s.AudioClips.FilePattern = "";
      s.AudioClips.Files = Array.Empty<string>();
      s.EpisodeNumbers = ready.Select(e => e.Number).ToArray();
      // Names padded as a run over every episode pads them, so that a skipped episode does not
      // rename the cards and media of the others from one run to the next.
      s.EpisodeCountForNames = list.Episodes.Count;
      ConstantSettings.UpdateAudioFilenameFormats();
    }

    /// <summary>
    /// The checks the GUI's Go makes too (<see cref="GoChecks"/>), over the episodes that would
    /// run. A project that groups snippets by AI needs the model (and <c>claude</c>) whatever
    /// the AI Grouping On Go preference says; only a dry run gets here with that mode, as yet.
    /// </summary>
    private static List<GoProblem> RunChecks(Settings s)
      => GoChecks.Run(s, GoChecks.AudioStreamIndex(s), s.Snippets.Mode == SnippetMode.AI);

    private static string Summary(EpisodeList list)
    {
      int skipped = list.SkippedCount;
      string from = list.SeasonDir != null ? "in " + list.SeasonDir : "from the project's patterns";
      string leftOut = list.LeftOut > 0
        ? $"; {list.LeftOut} more after Episode End # {Settings.Instance.EpisodeEndNumber} left out"
        : "";
      return FormattableString.Invariant(
        $"{list.Episodes.Count} episode(s) {from}: {list.Episodes.Count - skipped} ready, {skipped} skipped{leftOut}.");
    }

    /// <summary>
    /// A run that failed or was cancelled after "Generate import file" leaves a TSV whose cards
    /// lack some of their media: it is deleted, so that it cannot be imported by mistake. The
    /// media made so far stay. A TSV of an earlier run is not touched when the run stopped
    /// before that step.
    /// </summary>
    private static void DeleteUnfinishedImportFile(string path, TextWriter stderr)
    {
      try
      {
        File.Delete(path);
        stderr.WriteLine($"subs2srs-cli: deleted {path}: the run stopped after writing it, before the media of its cards were made.");
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
      {
        stderr.WriteLine($"subs2srs-cli: {path} was written before the run stopped and its cards lack media; do not import it. "
          + $"It could not be deleted: {ex.Message}");
      }
    }

    /// <summary>
    /// The table after a run, on stdout: one row per episode of the list with its number, video
    /// name, "done", "skipped: " and why, "failed" or "cancelled", and its cards; then the
    /// TSV's path, how many episodes it holds, and the exit code. <paramref name="result"/> is
    /// null when no episode was ready and nothing ran.
    /// </summary>
    internal static void PrintTable(EpisodeList list, PipelineResult? result, int exitCode, TextWriter stdout)
    {
      var table = new TextTable("#", "Episode", "Status", "Cards");
      int index = 0; // into the run's Files arrays, and so into CardsPerEpisode: the ready episodes in order
      foreach (Episode e in list.Episodes)
      {
        string number = e.Number.ToString(CultureInfo.InvariantCulture);
        string name = Path.GetFileNameWithoutExtension(e.Video ?? e.Subs1 ?? "");
        if (e.Skipped)
        {
          table.Add(number, name, "skipped: " + e.SkipReason, "-");
          continue;
        }
        int i = index++;
        PipelineStatus ran = result!.Status; // an episode was ready, so the run started
        string status = ran switch
        {
          PipelineStatus.Completed => "done",
          PipelineStatus.Cancelled => "cancelled",
          _ => "failed",
        };
        string cards = ran == PipelineStatus.Completed && i < result.CardsPerEpisode.Count
          ? result.CardsPerEpisode[i].ToString(CultureInfo.InvariantCulture)
          : "-";
        table.Add(number, name, status, cards);
      }
      foreach (string line in table.Lines())
        stdout.WriteLine(line);

      bool written = result?.Status == PipelineStatus.Completed && result.ImportFile != null;
      int done = written ? list.Episodes.Count(e => !e.Skipped) : 0;
      stdout.WriteLine(FormattableString.Invariant(
        $"{(list.SeasonDir != null ? "season TSV" : "TSV")}: {(written ? result!.ImportFile : "not written")} ({done} of {list.Episodes.Count} episodes); exit {exitCode}"));
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
