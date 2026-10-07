using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace subs2srs.Cli
{
  internal enum AiOutcomeKind { Cached, Grouped, PartlyByRules, UsageLimit, Failed }

  /// <summary>What the AI pre-pass made of one episode: the table's AI column, and why it is skipped.</summary>
  internal sealed record AiOutcome(AiOutcomeKind Kind, int FailedChunks = 0, int Chunks = 0, string? SkipReason = null)
  {
    /// <summary>The episode has no AI grouping, so it gets no cards in this run.</summary>
    public bool Skipped => SkipReason != null;

    public string Column => Kind switch
    {
      AiOutcomeKind.Cached => "cached",
      AiOutcomeKind.Grouped => "grouped",
      AiOutcomeKind.PartlyByRules => FormattableString.Invariant($"{FailedChunks}/{Chunks} by rules"),
      AiOutcomeKind.UsageLimit => "usage limit",
      _ => "failed",
    };

    public static readonly AiOutcome UsageLimitReached =
      new(AiOutcomeKind.UsageLimit, SkipReason: "the Claude usage limit was reached");
  }


  /// <summary>
  /// go's AI pre-pass, for a project that groups snippets by AI (season plan, decision 6): the
  /// pipeline's first two steps over the run's episodes, then the model's grouping of each
  /// episode, before the run. An episode without an AI grouping is skipped rather than grouped by
  /// the rules: <see cref="DropSkipped"/> takes it out of the run, and the others go to
  /// <see cref="SubsProcessor.StartAsync"/> with their lines and join vectors, so the run skips
  /// its own first steps and AI step (<see cref="WorkerSubs.aiGroupingOnGoApplies"/>), as after
  /// the GUI's Preview.
  /// </summary>
  internal sealed class AiPrePass
  {
    private AiPrePass(List<List<InfoCombined>> combinedAll, List<bool[]?> joins, AiOutcome[] outcomes, int[] numbers)
    {
      CombinedAll = combinedAll;
      Joins = joins;
      Outcomes = outcomes;
      Numbers = numbers;
    }

    /// <summary>Each episode's lines; after <see cref="DropSkipped"/>, those of the episodes that run.</summary>
    public List<List<InfoCombined>> CombinedAll { get; private set; }
    /// <summary>Each episode's join vector (stored form, a slot per line), null for a skipped one until <see cref="DropSkipped"/>.</summary>
    public List<bool[]?> Joins { get; private set; }
    /// <summary>One per episode the pre-pass was given, in order; <see cref="DropSkipped"/> keeps them all.</summary>
    public AiOutcome[] Outcomes { get; }
    /// <summary>The episode numbers of <see cref="Outcomes"/>.</summary>
    public int[] Numbers { get; }
    public int SkippedCount => Outcomes.Count(o => o.Skipped);

    /// <summary>
    /// "Combine subs" and "Inactivate lines" over the episodes of the Files arrays, made as the
    /// pipeline's own first steps make them: the same workers, a <see cref="WorkerVars"/> like
    /// <see cref="SubsProcessor.StartAsync"/>'s, the episode numbers already set (per-episode
    /// time-shift rules read them). One table of duplicate lines spans every episode given,
    /// including any the AI step drops afterwards: the lines it leaves active are the ones the
    /// model groups and the cache key hashes, so they must not change with the outcome.
    /// A failure is a <see cref="CliException"/> "&lt;step&gt; failed: &lt;why&gt;".
    /// </summary>
    public static List<List<InfoCombined>> FirstSteps(ConsoleProgress progress)
    {
      Settings s = Settings.Instance;
      var workerVars = new WorkerVars(null!, SubsProcessor.getMediaDir(s.OutputDir, s.DeckName),
        WorkerVars.SubsProcessingType.Normal);
      var worker = new WorkerSubs();
      workerVars.CombinedAll = Step(progress, "Combine subs", () => worker.combineAllSubs(workerVars, progress));
      if (workerVars.CombinedAll.Sum(lines => lines.Count) == 0)
        throw new CliException("Combine subs failed: no lines of dialog could be parsed from the subtitle files; check that they are valid.");
      workerVars.CombinedAll = Step(progress, "Inactivate lines", () => worker.inactivateLines(workerVars, progress));
      return workerVars.CombinedAll;
    }

    private static List<List<InfoCombined>> Step(ConsoleProgress progress, string label, Func<List<List<InfoCombined>>?> step)
    {
      progress.Line(label);
      List<List<InfoCombined>>? done;
      try
      {
        done = step();
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        throw new CliException($"{label} failed: {OneLine(ex.Message)}");
      }
      // The workers return null when cancelled (or when a parser gives up).
      progress.Token.ThrowIfCancellationRequested();
      return done ?? throw new CliException($"{label} failed: it stopped without an error message");
    }

    /// <summary>
    /// The pre-pass over the episodes of the Files arrays: <see cref="FirstSteps"/>, then
    /// <see cref="AiGrouper.Group"/> episode by episode with the project's AI settings, a line
    /// per episode on <paramref name="progress"/> and its chunks' progress under it. The outcome:
    /// cached, grouped, some chunks grouped by the rules (kept, with a warning: a retry would
    /// likely fail the same way), or skipped when every chunk failed or nothing could be asked.
    /// The Claude usage limit (<see cref="ClaudeCliProvider.UsageLimit"/>) stays reached for the
    /// rest of the process: an episode during which it was reached is skipped, though the rules
    /// could group its failed chunks (such a result is not cached, so a later run redoes the
    /// whole episode), and after it every episode the cache cannot answer is skipped without
    /// asking. A cached one still gets its grouping: no request is made for it.
    /// </summary>
    public static AiPrePass Run(ConsoleProgress progress, CancellationToken token)
    {
      List<List<InfoCombined>> combinedAll = FirstSteps(progress);
      SnippetLimits limits = SnippetLimits.FromSettings();
      int count = combinedAll.Count;
      var joins = new List<bool[]?>(new bool[]?[count]);
      var outcomes = new AiOutcome[count];
      int[] numbers = Enumerable.Range(0, count).Select(Settings.Instance.EpisodeNumber).ToArray();
      for (int i = 0; i < count; i++)
      {
        token.ThrowIfCancellationRequested();
        List<InfoCombined> lines = combinedAll[i];
        AiGroupingOptions options = AiGroupingOptions.FromSettings();
        progress.Line(FormattableString.Invariant($"AI grouping: episode {numbers[i]} ({i + 1} of {count})"));
        if (ClaudeCliProvider.UsageLimit != null && !AiGrouper.Estimate(lines, limits, options).Cached)
        {
          outcomes[i] = AiOutcome.UsageLimitReached;
          continue;
        }
        try
        {
          AiGroupingResult result = AiGrouper.Group(lines, limits, options, progress, token);
          if (result.FailedChunks > 0 && ClaudeCliProvider.UsageLimit != null)
          {
            outcomes[i] = AiOutcome.UsageLimitReached;
            continue;
          }
          joins[i] = AiGrouper.ApplyToLines(result, lines);
          outcomes[i] = result.FromCache ? new AiOutcome(AiOutcomeKind.Cached)
            : result.FailedChunks > 0 ? new AiOutcome(AiOutcomeKind.PartlyByRules, result.FailedChunks, result.Chunks)
            : new AiOutcome(AiOutcomeKind.Grouped);
        }
        catch (ProviderException ex)
        {
          // Every chunk failed, or nothing could be asked (no key, unknown model, no claude).
          outcomes[i] = ClaudeCliProvider.UsageLimit != null
            ? AiOutcome.UsageLimitReached
            : new AiOutcome(AiOutcomeKind.Failed, SkipReason: "AI grouping failed: " + OneLine(ex.Message));
        }
      }
      return new AiPrePass(combinedAll, joins, outcomes, numbers);
    }

    /// <summary>
    /// Whether each episode's AI grouping is in the cache, from the same first steps and without
    /// asking the model (<see cref="AiGrouper.Estimate"/>), for <c>--dry-run</c>.
    /// </summary>
    public static bool[] Cached(List<List<InfoCombined>> combinedAll)
    {
      SnippetLimits limits = SnippetLimits.FromSettings();
      AiGroupingOptions options = AiGroupingOptions.FromSettings();
      return combinedAll.Select(lines => AiGrouper.Estimate(lines, limits, options).Cached).ToArray();
    }

    /// <summary>The warnings and the usage limit on stderr, after the pre-pass.</summary>
    public void Report(TextWriter stderr)
    {
      for (int i = 0; i < Outcomes.Length; i++)
      {
        AiOutcome o = Outcomes[i];
        if (o.Kind == AiOutcomeKind.PartlyByRules)
          stderr.WriteLine(FormattableString.Invariant(
            $"warning: episode {Numbers[i]}: {o.FailedChunks} of {o.Chunks} chunks grouped by the rules, their requests failed (--verbose logs why).")
            + " Its cards are made; this grouping is not cached, so a later run asks the model again.");
      }
      int limited = Outcomes.Count(o => o.Kind == AiOutcomeKind.UsageLimit);
      if (limited > 0)
        stderr.WriteLine(FormattableString.Invariant(
          $"subs2srs-cli: the Claude usage limit was reached ({ClaudeCliProvider.UsageLimit}); {limited} episode(s) skipped.")
          + " Run go again after it resets: the groupings made so far come from the cache.");
    }

    /// <summary>
    /// Take the skipped episodes out of the run: out of <see cref="CombinedAll"/> and
    /// <see cref="Joins"/>, and out of the lists the pipeline reads by episode (<see cref="KeepEpisodes"/>).
    /// </summary>
    public void DropSkipped(Settings s)
    {
      bool[] keep = Outcomes.Select(o => !o.Skipped).ToArray();
      CombinedAll = CombinedAll.Where((_, i) => keep[i]).ToList();
      Joins = Joins.Where((_, i) => keep[i]).ToList();
      if (keep.Contains(false)) KeepEpisodes(s, keep);
    }

    /// <summary>
    /// Leave only the episodes <paramref name="keep"/> marks in every list the pipeline reads by
    /// an episode's position: the Subs1, Subs2, video and audio files and the episode numbers.
    /// The numbers are made explicit first and the names keep the padding of every episode given,
    /// so the episodes that run keep their numbers and names. A list of another length is not
    /// one the run reads by episode (a pattern it does not use) and is left as it is.
    /// </summary>
    internal static void KeepEpisodes(Settings s, bool[] keep)
    {
      int count = keep.Length;
      s.EpisodeNumbers ??= Enumerable.Range(0, count).Select(s.EpisodeNumber).ToArray();
      s.EpisodeCountForNames ??= count;
      T[] Kept<T>(T[] list) => list.Length == count ? list.Where((_, i) => keep[i]).ToArray() : list;
      s.Subs[0].Files = Kept(s.Subs[0].Files);
      s.Subs[1].Files = Kept(s.Subs[1].Files);
      s.VideoClips.Files = Kept(s.VideoClips.Files);
      s.AudioClips.Files = Kept(s.AudioClips.Files);
      s.EpisodeNumbers = Kept(s.EpisodeNumbers);
    }

    private static string OneLine(string message)
      => string.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
  }
}
