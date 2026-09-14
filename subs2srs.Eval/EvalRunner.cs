using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Eval
{
  /// <summary>
  /// Runs one producer (the rules or a model) over an evaluation set through the same code path
  /// as the app (<see cref="AiGrouper"/>: chunker, prompt, provider layer, answer repair, cache),
  /// scores every file, prints the table and writes the reports. Reusable from tests with a
  /// fake provider installed through <see cref="ChatProviders.Override"/>.
  /// </summary>
  public sealed class EvalRunner
  {
    private readonly EvalOptions options;
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;

    public EvalRunner(EvalOptions options, TextWriter? stdout = null, TextWriter? stderr = null)
    {
      this.options = options;
      this.stdout = stdout ?? Console.Out;
      this.stderr = stderr ?? Console.Error;
    }

    /// <summary>The run after scoring (reports written), or null when <c>--estimate</c> stopped before any call.</summary>
    public async Task<EvalRun?> RunAsync(CancellationToken ct)
    {
      GroupingEvalSet set = GroupingEvalSet.Load(options.SetDir, options.HoldoutDir);
      foreach ((string path, string error) in set.Errors)
        stderr.WriteLine($"skipping {path}: {error}");
      if (set.Files.Count == 0)
        throw new EvalException($"no {GroupingEvalSet.FilePattern} files under {set.Dir}" + (set.HoldoutDir != null ? $" or {set.HoldoutDir}" : ""), EvalOptions.ExitNoFiles);

      var run = new EvalRun
      {
        Name = options.EffectiveName,
        Producer = options.Rules ? "rules" : "ai",
        Model = options.Model,
        PromptVersion = options.Rules ? null : AiGroupingPrompt.PromptVersion,
        ChunkTargetLines = options.Rules ? null : options.ChunkTargetLines,
        ExtraInstructions = options.Rules ? null : options.ExtraInstructions,
        RuleOptions = options.Rules ? options.RuleOptions : null,
        SetDir = set.Dir,
        HoldoutDir = set.HoldoutDir,
        CacheDir = options.Rules ? null : new AiGroupingCache(options.CacheDir).Dir,
      };

      stdout.WriteLine(FormattableString.Invariant($"{run.Name}: {run.Describe()}"));
      stdout.WriteLine(FormattableString.Invariant($"{set.Files.Count} file(s): {set.Tuning.Count()} tuning, {set.Holdout.Count()} hold-out; {set.Files.Sum(f => f.File.Lines.Count):N0} lines"));

      if (!options.Rules && !await CheckEstimateAsync(set)) return null;

      var stopwatch = Stopwatch.StartNew();
      int index = 0;
      foreach (GroupingEvalFile file in set.Files)
      {
        ct.ThrowIfCancellationRequested();
        index++;
        EvalFileRecord record = await ScoreFileAsync(file, ct).ConfigureAwait(false);
        run.Files.Add(record);
        stdout.WriteLine(FormattableString.Invariant($"[{index}/{set.Files.Count}] {DescribeRecord(record)}"));
      }
      run.Aggregate();

      stdout.WriteLine();
      stdout.Write(run.ToTable());
      if (run.Producer == "ai")
      {
        string spent = FormattableString.Invariant($"Spent this run: {run.SpentInputTokens:N0} input + {run.SpentOutputTokens:N0} output tokens");
        if (run.SpentUsd.HasValue) spent += FormattableString.Invariant($", about ${run.SpentUsd.Value:0.000}");
        stdout.WriteLine(spent + FormattableString.Invariant($" ({run.Files.Count(f => f.Cached)} of {run.Files.Count} file(s) from the cache, {stopwatch.Elapsed.TotalSeconds:0.0} s)"));
      }

      string? comparison = null;
      if (options.Compare != null)
      {
        EvalRun other = LoadComparison(options.Compare, options.EffectiveOutDir);
        comparison = Compare(run, other, set);
        stdout.WriteLine();
        stdout.Write(comparison);
      }

      string outDir = options.EffectiveOutDir;
      run.WriteReports(outDir, comparison);
      stdout.WriteLine();
      stdout.WriteLine($"Reports: {run.JsonPath(outDir)}, {Path.GetFileName(run.CsvPath(outDir))}, {Path.GetFileName(run.MarkdownPath(outDir))}");

      if (run.Producer == "ai" && run.Files.All(f => f.Error != null))
        throw new EvalException("every model call failed: " + run.Files[0].Error, EvalOptions.ExitAllFailed);
      return run;
    }

    // ── estimate / cost cap ─────────────────────────────────────────────

    private Task<bool> CheckEstimateAsync(GroupingEvalSet set)
    {
      int requests = 0, input = 0, output = 0, cached = 0;
      double usd = 0;
      bool priced = true;
      foreach (GroupingEvalFile file in set.Files)
      {
        List<InfoCombined> lines = file.File.ToLines();
        AiCostEstimate e = AiGrouper.Estimate(lines, file.File.Limits.ToSnippetLimits(), AiOptionsFor(file));
        if (e.Cached) { cached++; continue; }
        requests += e.Chunks;
        input += e.InputTokens;
        output += e.OutputTokens;
        if (e.Usd.HasValue) usd += e.Usd.Value; else priced = false;
      }
      string cost = priced ? FormattableString.Invariant($"about ${usd:0.000}") : "price unknown for this model";
      stdout.WriteLine(FormattableString.Invariant($"Estimate: {requests} request(s), ~{input:N0} input + ~{output:N0} output tokens, {cost}; {cached} of {set.Files.Count} file(s) already cached"));

      if (options.MaxCostUsd.HasValue && requests > 0)
      {
        if (!priced)
          throw new EvalException($"--max-cost given but {options.Model} has no price in the table; drop --max-cost or add the price to AiPricing", EvalOptions.ExitCostCap);
        if (usd > options.MaxCostUsd.Value)
          throw new EvalException(FormattableString.Invariant($"estimated cost ${usd:0.000} exceeds --max-cost {options.MaxCostUsd.Value:0.000}; nothing was requested"), EvalOptions.ExitCostCap);
      }
      if (options.EstimateOnly)
      {
        stdout.WriteLine("--estimate: stopping before any request.");
        return Task.FromResult(false);
      }
      return Task.FromResult(true);
    }

    private AiGroupingOptions AiOptionsFor(GroupingEvalFile file) => new AiGroupingOptions
    {
      Model = options.Model ?? "",
      ChunkTargetLines = options.ChunkTargetLines,
      ExtraInstructions = options.ExtraInstructions,
      ForceRefresh = options.Refresh,
      CacheDir = options.CacheDir,
      Concurrency = options.Concurrency,
      Fallback = options.RuleOptions,
      ProgressLabel = file.Name,
    };

    // ── one file ────────────────────────────────────────────────────────

    private async Task<EvalFileRecord> ScoreFileAsync(GroupingEvalFile file, CancellationToken ct)
    {
      GroupingValidationFile v = file.File;
      List<InfoCombined> lines = v.ToLines();
      int[] kept = SnippetGrouping.KeptIndices(lines);
      SnippetLimits limits = v.Limits.ToSnippetLimits();
      var record = new EvalFileRecord
      {
        RelativePath = file.RelativePath,
        IsHoldout = file.IsHoldout,
        Lines = lines.Count,
        Truth = v.Joins,
      };
      if (v.Proposal != null)
      {
        record.ProposalProducer = v.Proposal.Producer;
        record.ProposalModel = v.Proposal.Model;
        record.ProposalPromptVersion = v.Proposal.PromptVersion;
        record.ProposalFlips = GroupingScorer.Flips(v.Proposal.Joins, v.Joins, lines.Count);
      }

      try
      {
        if (options.Rules)
        {
          record.Predicted = RuleBasedGrouper.Group(lines, kept, limits, options.RuleOptions);
        }
        else
        {
          var progress = new ConsoleProgress(stderr, ct);
          AiGroupingResult result = await AiGrouper.GroupAsync(lines, limits, AiOptionsFor(file), progress, ct).ConfigureAwait(false);
          progress.Done();
          record.Predicted = result.KeptJoins;
          record.Cached = result.FromCache;
          record.Chunks = result.Chunks;
          record.FailedChunks = result.FailedChunks;
          record.InputTokens = result.InputTokens;
          record.OutputTokens = result.OutputTokens;
          record.Usd = AiPricing.Usd(options.Model!, result.InputTokens, result.OutputTokens);
          record.Repairs = result.Repairs;
        }
        record.Score = GroupingScorer.Score(record.Truth, record.Predicted, lines.Count);
        record.Score.InputTokens = record.InputTokens;
        record.Score.OutputTokens = record.OutputTokens;
        record.Score.Usd = record.Usd;
      }
      catch (OperationCanceledException) { throw; }
      catch (ProviderException ex)
      {
        record.Error = ex.Message;
      }
      return record;
    }

    private static string DescribeRecord(EvalFileRecord r)
    {
      if (r.Score == null) return $"{r.RelativePath}: {r.Error}";
      GroupingScore s = r.Score;
      string text = FormattableString.Invariant($"{r.RelativePath}: F1 {s.F1 * 100:0.0} (P {s.Precision * 100:0.0}, R {s.Recall * 100:0.0}), exact {s.ExactSnippets}/{s.TruthSnippets}, over {s.OverMergedSnippets}, under {s.UnderMergedSnippets}");
      if (r.Cached) text += ", cached";
      else if (r.InputTokens + r.OutputTokens > 0) text += FormattableString.Invariant($", {r.InputTokens + r.OutputTokens:N0} tokens");
      if (r.FailedChunks > 0) text += FormattableString.Invariant($", {r.FailedChunks} chunk(s) fell back to the rules");
      return text;
    }

    // ── compare ─────────────────────────────────────────────────────────

    public static EvalRun LoadComparison(string spec, string outDir)
    {
      string path = File.Exists(spec) ? spec : Path.Combine(outDir, spec.EndsWith(EvalRun.Extension, StringComparison.OrdinalIgnoreCase) ? spec : spec + EvalRun.Extension);
      if (!File.Exists(path)) throw new EvalException($"--compare: no run file at {spec} or {path}", EvalOptions.ExitUsage);
      try { return EvalRun.Read(path); }
      catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidDataException)
      {
        throw new EvalException($"--compare: cannot read {path}: {ex.Message}", EvalOptions.ExitUsage);
      }
    }

    /// <summary>Markdown text: per-file F1 deltas and every boundary where the two runs disagree, with the human truth and the line texts.</summary>
    public static string Compare(EvalRun current, EvalRun other, GroupingEvalSet set)
    {
      var sb = new StringBuilder();
      sb.Append("## Compared with ").Append(other.Name).Append(" (").Append(other.Describe()).Append(")\n\n");
      var byPath = other.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
      int fixedTotal = 0, brokenTotal = 0, matched = 0;
      var details = new StringBuilder();
      foreach (EvalFileRecord mine in current.Files)
      {
        if (!byPath.TryGetValue(mine.RelativePath, out EvalFileRecord? theirs)) { sb.Append("- ").Append(mine.RelativePath).Append(": not in the other run\n"); continue; }
        if (mine.Score == null || theirs.Score == null) { sb.Append("- ").Append(mine.RelativePath).Append(": failed in one of the runs\n"); continue; }
        matched++;
        List<int> diff = GroupingScorer.Diff(mine.Predicted, theirs.Predicted, mine.Lines);
        int fixedHere = diff.Count(k => GroupingScorer.JoinAt(mine.Predicted, k) == GroupingScorer.JoinAt(mine.Truth, k));
        int brokenHere = diff.Count - fixedHere;
        fixedTotal += fixedHere;
        brokenTotal += brokenHere;
        sb.Append(FormattableString.Invariant($"- {mine.RelativePath}: F1 {theirs.Score.F1 * 100:0.0} -> {mine.Score.F1 * 100:0.0} ({Signed(mine.Score.F1 - theirs.Score.F1)}), {diff.Count} boundary(ies) differ: {fixedHere} fixed, {brokenHere} broken\n"));
        if (diff.Count == 0) continue;

        GroupingEvalFile? file = set.Files.FirstOrDefault(f => f.RelativePath == mine.RelativePath);
        details.Append("\n### ").Append(mine.RelativePath).Append("\n\n");
        details.Append("| k | truth | ").Append(current.Name).Append(" | ").Append(other.Name).Append(" | line k | line k+1 |\n| ---: | :---: | :---: | :---: | --- | --- |\n");
        foreach (int k in diff)
        {
          string t1 = file != null && k < file.File.Lines.Count ? file.File.Lines[k].T : "";
          string tNext = file != null && k + 1 < file.File.Lines.Count ? file.File.Lines[k + 1].T : "";
          details.Append(FormattableString.Invariant($"| {k} | {Mark(GroupingScorer.JoinAt(mine.Truth, k))} | {Mark(GroupingScorer.JoinAt(mine.Predicted, k))} | {Mark(GroupingScorer.JoinAt(theirs.Predicted, k))} | {Cell(t1)} | {Cell(tNext)} |\n"));
        }
      }
      sb.Append(FormattableString.Invariant($"\n{matched} file(s) compared; F1 (all) {other.All.F1 * 100:0.0} -> {current.All.F1 * 100:0.0} ({Signed(current.All.F1 - other.All.F1)}); {fixedTotal} boundary(ies) fixed, {brokenTotal} broken.\n"));
      sb.Append(details);
      return sb.ToString();
    }

    private static string Signed(double delta) => (delta >= 0 ? "+" : "") + (delta * 100).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Mark(bool join) => join ? "join" : "-";
    private static string Cell(string text) => (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    // ── progress ────────────────────────────────────────────────────────

    /// <summary>Per-chunk progress from the bulk runner on one stderr line (overwritten in a terminal).</summary>
    private sealed class ConsoleProgress : IProgressReporter
    {
      private readonly TextWriter writer;
      private readonly CancellationToken token;
      private readonly bool terminal;
      private string last = "";

      public ConsoleProgress(TextWriter writer, CancellationToken token)
      {
        this.writer = writer;
        this.token = token;
        terminal = ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected;
      }

      public bool Cancel => token.IsCancellationRequested;
      public CancellationToken Token => token;
      public int StepsTotal { get; set; }
      public void NextStep(int step, string description) { }
      public void UpdateProgress(string text) => UpdateProgress(-1, text);
      public void EnableDetail(bool enable) { }
      public void SetDuration(TimeSpan duration) { }
      public void OnFFmpegOutput(object sender, DataReceivedEventArgs e) { }

      public void UpdateProgress(int percent, string text)
      {
        if (text == last) return;
        last = text;
        if (terminal) writer.Write("\r" + text.PadRight(Math.Max(text.Length, 60)));
        else writer.WriteLine(text);
      }

      public void Done()
      {
        if (terminal && last != "") writer.Write("\r" + new string(' ', Math.Max(last.Length, 60)) + "\r");
      }
    }
  }
}
