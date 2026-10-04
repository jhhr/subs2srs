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
  /// One episode of <c>season</c>: its video and what each stage made of it, filled in stage by
  /// stage; a part stays null when its stage did not run. The table is built from these rows.
  /// </summary>
  internal sealed class SeasonEpisode
  {
    public SeasonEpisode(string video) { Video = video; }

    /// <summary>The video, full path.</summary>
    public string Video { get; }
    public string Name => Path.GetFileNameWithoutExtension(Video);
    /// <summary>The EN track, or why there is none; null when not looked up (<c>--only retime</c>).</summary>
    public MkvTrackPick? Pick { get; set; }
    /// <summary>The picked track's extraction; null when none ran (no track, a dry run).</summary>
    public MkvExtractResult? Extraction { get; set; }
    /// <summary>The EN file the retime takes, or why there is none.</summary>
    public FoundFile? En { get; set; }
    /// <summary>The JP file, or why there is none; null when the retime stage does not run.</summary>
    public FoundFile? Jp { get; set; }
    public RetimeOutcome? Retime { get; set; }
    /// <summary>A dry run's answer for the EN file: kept, to extract, or why not.</summary>
    public string? EnPlan { get; set; }
    /// <summary>A dry run's answer for the retime: kept, to retime, or why not.</summary>
    public string? RetimePlan { get; set; }
    /// <summary>The run was cancelled before this episode was done.</summary>
    public bool Cancelled { get; set; }
  }

  /// <summary>A column of the season table: its header and each episode's cell.</summary>
  internal sealed record SeasonColumn(string Header, Func<SeasonEpisode, string> Cell);

  /// <summary>
  /// <c>season DIR</c> (season plan B, C, D): for each episode of the season folder, one after
  /// another, the EN track picked (<see cref="MkvTracks.PickAsync"/>) and extracted into
  /// <c>s2s</c> (<see cref="MkvExtract"/>), then the JP file found by name and retimed to it
  /// (<see cref="RetimeStage"/>); then one table, the editor commands for the pairs subsretimer
  /// did not save, and the exit code. The table is a list of columns over the episodes' rows.
  /// </summary>
  internal static class SeasonCommand
  {
    /// <summary>The retime's reason when the episode has no EN track; the EN track column says why.</summary>
    public const string NoEnTrack = "no EN track";

    public static async Task<int> RunAsync(CliOptions options, TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
      CliRunner.LoadProject(options.ProjectPath);
      Settings s = Settings.Instance;
      string[] videos = EpisodeList.SeasonVideos(options.SeasonDir!);
      string dir = Path.GetFullPath(options.SeasonDir!);
      bool extract = options.Only != SeasonStage.Retime;
      bool retime = options.Only != SeasonStage.Extract;
      if (extract) RequireMkvToolNix();
      token.ThrowIfCancellationRequested();

      // The episodes go --season makes cards of: videos after Episode End # are left out.
      int count = ProjectFiles.EpisodeLimit(s.EpisodeStartNumber, s.EpisodeEndNumber) is int limit
        ? Math.Min(limit, videos.Length) : videos.Length;
      List<SeasonEpisode> episodes = videos.Take(count).Select(v => new SeasonEpisode(v)).ToList();
      stderr.WriteLine(Summary(dir, count, videos.Length - count));

      // Every video, the ones left out too: a name that is a longer video's JP file is that video's.
      FoundFile[]? jpFiles = retime ? RetimeStage.FindJpFiles(dir, videos) : null;
      FoundFile[]? enFiles = extract ? null : RetimeStage.FindEnFiles(dir, videos);
      // Resolved once; a missing subsretimer fails only the episodes it would have to retime.
      RetimeOptions? retimeOptions = retime && !options.DryRun ? RetimeOptions.FromSettings(s, options.MinMatch, options.Force) : null;

      for (int i = 0; i < episodes.Count; i++)
      {
        SeasonEpisode e = episodes[i];
        string at = FormattableString.Invariant($"[{i + 1}/{episodes.Count}] {e.Name}: ");
        try
        {
          if (!extract)
          {
            e.En = enFiles![i];
            if (options.DryRun) e.EnPlan = e.En.Path != null ? "there" : e.En.Problem;
          }
          else if (options.DryRun)
            await PlanExtractAsync(dir, e, options, token);
          else
          {
            await ExtractAsync(dir, e, options, token);
            stderr.WriteLine(at + ExtractLine(e));
          }

          if (retime)
          {
            e.Jp = jpFiles![i];
            if (options.DryRun)
              e.RetimePlan = PlanRetime(dir, e, options.Force);
            else
            {
              e.Retime = await RetimeStage.RetimeAsync(dir, e.Video, e.Jp, e.En!, retimeOptions!, token);
              stderr.WriteLine(at + "retime: " + e.Retime.Column);
            }
          }
        }
        catch (OperationCanceledException)
        {
          foreach (SeasonEpisode left in episodes.Skip(i)) left.Cancelled = true;
          PrintReport(episodes, options, CliOptions.ExitCancelled, stdout);
          throw;
        }
      }

      int ready = episodes.Count(e => Ready(e, retime, options.DryRun));
      int code = ready == episodes.Count ? CliOptions.ExitOk : CliOptions.ExitSkipped;
      PrintReport(episodes, options, code, stdout);
      if (options.DryRun)
      {
        if (episodes.Any(e => e.RetimePlan == "to retime") && RetimeStage.ResolveExe() == null)
          stderr.WriteLine("warning: " + RetimeStage.NotFoundMessage + "; the episodes to retime would fail.");
        stderr.WriteLine("Dry run: nothing was extracted, retimed or deleted.");
      }
      return code;
    }

    /// <summary>mkvmerge picks the tracks and mkvextract extracts them: both, before any work.</summary>
    private static void RequireMkvToolNix()
    {
      if (ConstantSettings.ResolveTool(ConstantSettings.ExeMkvMerge) == null)
        throw new CliException(MkvTracks.NotFoundMessage);
      if (ConstantSettings.ResolveTool(ConstantSettings.ExeMkvExtract) == null)
        throw new CliException(MkvExtract.NotFoundMessage);
    }

    private static string Summary(string dir, int count, int leftOut)
    {
      string more = leftOut > 0
        ? FormattableString.Invariant($"; {leftOut} more after Episode End # {Settings.Instance.EpisodeEndNumber} left out")
        : "";
      return FormattableString.Invariant($"{count} episode(s) in {dir}{more}.");
    }

    // ── extraction ──────────────────────────────────────────────────────

    /// <summary>
    /// The EN track, then its extraction to <c>s2s/&lt;video name&gt;.en.&lt;ext&gt;</c>: an
    /// existing file is kept (<see cref="MkvExtract.ExtractAsync"/>). Every other EN file of the
    /// episode, an extraction of another track (go skips an episode with two), is deleted
    /// first; with <c>--force</c> the track's own too, so it is extracted again. A failure
    /// becomes the retime's missing EN file, with the reason.
    /// </summary>
    private static async Task ExtractAsync(string dir, SeasonEpisode e, CliOptions options, CancellationToken token)
    {
      e.Pick = await MkvTracks.PickAsync(e.Video, options.Track, token);
      if (e.Pick.Track is not MkvTrackInfo track)
      {
        // Without a track there is no telling which EN file is right: they all stay.
        e.En = FoundFile.Missing(NoEnTrack);
        return;
      }
      string output = MkvExtract.OutputPath(dir, e.Video, track);
      string? error = RetimeStage.Delete(EnFiles(dir, e.Video).Where(f => options.Force || !SamePath(f, output)));
      e.Extraction = error != null
        ? new MkvExtractResult { Status = MkvExtractStatus.Failed, OutputPath = output, Reason = error }
        : await MkvExtract.ExtractAsync(e.Video, track.Id, output, token);
      e.En = e.Extraction.Status == MkvExtractStatus.Failed
        ? FoundFile.Missing("extraction failed: " + e.Extraction.Reason)
        : FoundFile.Of(e.Extraction.OutputPath);
    }

    /// <summary>
    /// <see cref="ExtractAsync"/> without writing or deleting: the track (mkvmerge only reads),
    /// and whether its EN file is there and would be kept.
    /// </summary>
    private static async Task PlanExtractAsync(string dir, SeasonEpisode e, CliOptions options, CancellationToken token)
    {
      e.Pick = await MkvTracks.PickAsync(e.Video, options.Track, token);
      if (e.Pick.Track is not MkvTrackInfo track)
      {
        e.En = FoundFile.Missing(NoEnTrack);
        e.EnPlan = "-";
        return;
      }
      string output = MkvExtract.OutputPath(dir, e.Video, track);
      bool there = new FileInfo(output) is { Exists: true, Length: > 0 };
      List<string> others = EnFiles(dir, e.Video).Where(f => !SamePath(f, output)).ToList();
      e.En = FoundFile.Of(output);
      e.EnPlan = (!there ? "to extract" : options.Force ? "to extract again" : "kept")
        + (others.Count > 0 ? "; deletes " + string.Join(", ", others.Select(f => Path.GetFileName(f))) : "");
    }

    /// <summary>The episode's <c>.en</c> files in <c>s2s</c>, as go --season finds its Subs2 (any case), full paths.</summary>
    private static List<string> EnFiles(string dir, string video)
    {
      string s2s = Path.Combine(dir, EpisodeList.SubsFolder);
      if (!Directory.Exists(s2s)) return new List<string>();
      return EpisodeList.Named(Directory.GetFiles(s2s).Select(f => Path.GetFileName(f)), Path.GetFileNameWithoutExtension(video),
        EpisodeList.Subs2Tag).Select(n => Path.Combine(s2s, n)).ToList();
    }

    /// <summary>One file to the file system: names that differ only in case are one file on Windows.</summary>
    private static bool SamePath(string a, string b) => string.Equals(a, b, RetimeStage.PathComparison);

    /// <summary>The episode's line on stderr after its extraction.</summary>
    private static string ExtractLine(SeasonEpisode e)
    {
      if (e.Pick?.Track is not MkvTrackInfo track) return "EN track: " + e.Pick?.Reason;
      return "EN track " + track.Label + ": " + ExtractionCell(e);
    }

    // ── the retime, planned ─────────────────────────────────────────────

    /// <summary>What <see cref="RetimeStage.RetimeAsync"/> would do, in its order: the JP file's problem, the EN file's, kept or to retime.</summary>
    private static string PlanRetime(string dir, SeasonEpisode e, bool force)
    {
      if (e.Jp!.Path == null) return e.Jp.Problem ?? RetimeStage.NoJpFileReason;
      if (e.En!.Path == null) return e.En.Problem ?? RetimeStage.NoEnFileReason;
      // An EN file still to extract is not there yet, so its retime is not kept either.
      return RetimeStage.WouldKeep(dir, e.Video, e.Jp.Path, e.En.Path, force) ? "kept" : "to retime";
    }

    /// <summary>
    /// The episode got what the run's last stage makes: a retime <c>go</c> can use, or with
    /// <c>--only extract</c> its EN file. In a dry run: no problem known yet.
    /// </summary>
    private static bool Ready(SeasonEpisode e, bool retime, bool dryRun)
      => dryRun ? e.En?.Path != null && (!retime || e.Jp?.Path != null)
        : retime ? e.Retime?.Ready == true : e.En?.Path != null;

    // ── the table ───────────────────────────────────────────────────────

    /// <summary>
    /// The table's columns for these options: Episode, EN track (unless <c>--only retime</c>),
    /// then the last stage's result: Retime, or with <c>--only extract</c> the EN file. A dry
    /// run shows the plan: EN file, JP file and Retime.
    /// </summary>
    internal static List<SeasonColumn> Columns(CliOptions options)
    {
      bool extract = options.Only != SeasonStage.Retime;
      bool retime = options.Only != SeasonStage.Extract;
      var columns = new List<SeasonColumn> { new("Episode", e => e.Name) };
      if (extract)
        columns.Add(new("EN track", e => e.Pick == null ? Pending(e) : e.Pick.Track?.Label ?? e.Pick.Reason ?? ""));
      if (options.DryRun)
      {
        columns.Add(new("EN file", e => e.EnPlan ?? Pending(e)));
        if (retime)
        {
          columns.Add(new("JP file", e => e.Jp == null ? Pending(e) : e.Jp.Path != null ? Path.GetFileName(e.Jp.Path) : e.Jp.Problem ?? ""));
          columns.Add(new("Retime", e => e.RetimePlan ?? Pending(e)));
        }
      }
      else if (retime)
        columns.Add(new("Retime", e => e.Retime?.Column ?? Pending(e)));
      else
        columns.Add(new("EN file", ExtractionCell));
      return columns;
    }

    /// <summary>A stage that did not run: cancelled before it, or nothing to do ("-").</summary>
    private static string Pending(SeasonEpisode e) => e.Cancelled ? "cancelled" : "-";

    private static string ExtractionCell(SeasonEpisode e) => e.Extraction?.Status switch
    {
      MkvExtractStatus.Extracted => "extracted",
      MkvExtractStatus.Kept => "kept",
      MkvExtractStatus.Failed => "failed: " + e.Extraction.Reason,
      _ => e.Pick != null && e.Pick.Track == null ? "-" : Pending(e),
    };

    /// <summary>
    /// On stdout: the table; a note when the picked EN track differs between episodes; after a
    /// run, how many episodes are ready and the exit code, then the editor command of each pair
    /// subsretimer did not save (design 9).
    /// </summary>
    internal static void PrintReport(IReadOnlyList<SeasonEpisode> episodes, CliOptions options, int exitCode, TextWriter stdout)
    {
      PrintTable(episodes, Columns(options), stdout);
      if (TrackNote(episodes, options.Track != null) is string note) stdout.WriteLine(note);
      if (options.DryRun) return;

      bool retime = options.Only != SeasonStage.Extract;
      int ready = episodes.Count(e => Ready(e, retime, dryRun: false));
      stdout.WriteLine(FormattableString.Invariant(
        $"{(retime ? "retimed JP files" : "EN files")}: {ready} of {episodes.Count} episodes; exit {exitCode}"));
      List<string> commands = episodes.Select(e => e.Retime?.EditorCommand).OfType<string>().ToList();
      if (commands.Count == 0) return;
      stdout.WriteLine("align by hand, then run again:");
      foreach (string command in commands)
        stdout.WriteLine("  " + command);
    }

    internal static void PrintTable(IReadOnlyList<SeasonEpisode> episodes, IReadOnlyList<SeasonColumn> columns, TextWriter stdout)
    {
      var table = new TextTable(columns.Select(c => c.Header).ToArray());
      foreach (SeasonEpisode e in episodes)
        table.Add(columns.Select(c => c.Cell(e)).ToArray());
      foreach (string line in table.Lines())
        stdout.WriteLine(line);
    }

    /// <summary>
    /// B2's warning: the picked tracks, by id and name, when they are not all one. A group of up
    /// to three episodes is named, so an odd one out can be found. Without <c>--track</c>, how to
    /// take one track from every episode.
    /// </summary>
    internal static string? TrackNote(IEnumerable<SeasonEpisode> episodes, bool trackGiven)
    {
      var groups = episodes.Where(e => e.Pick?.Track != null)
        .GroupBy(e => TrackName(e.Pick!.Track!)).ToList();
      if (groups.Count < 2) return null;
      return "note: the EN track differs between episodes: " + string.Join(", ", groups.Select(g =>
      {
        int n = g.Count();
        string count = n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " episode" : " episodes");
        return g.Key + " in " + count + (n <= 3 ? " (" + string.Join(", ", g.Select(e => e.Name)) + ")" : "");
      })) + (trackGiven ? "." : "; --track <id> extracts the same one from all.");
    }

    /// <summary>A track's id and name, without its event count (which differs between episodes anyway).</summary>
    private static string TrackName(MkvTrackInfo track) =>
      track.Id.ToString(CultureInfo.InvariantCulture) + (track.Name.Length > 0 ? " \"" + track.Name + "\"" : "");
  }
}
