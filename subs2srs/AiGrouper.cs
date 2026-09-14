using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>Knobs of one AI grouping run, taken from the project settings and preferences.</summary>
  public sealed class AiGroupingOptions
  {
    public string Model { get; set; } = "";
    public int ChunkTargetLines { get; set; } = AiChunker.DefaultTargetLines;
    public string? ExtraInstructions { get; set; }
    /// <summary>Ignore a cached answer and call the model again (the preview's Regroup (AI)).</summary>
    public bool ForceRefresh { get; set; }
    /// <summary>Null = the preference / default directory.</summary>
    public string? CacheDir { get; set; }
    /// <summary>Null = from the preferences for the model's provider.</summary>
    public int? Concurrency { get; set; }
    public int? Rpm { get; set; }
    /// <summary>Rule grouper used for chunks whose request failed.</summary>
    public RuleGrouperOptions Fallback { get; set; } = new RuleGrouperOptions();
    /// <summary>Prefix of the progress text ("AI grouping: 3 of 8 chunks").</summary>
    public string ProgressLabel { get; set; } = "AI grouping";

    public static AiGroupingOptions FromSettings(bool forceRefresh = false)
    {
      SnippetSettings s = Settings.Instance.Snippets;
      return new AiGroupingOptions
      {
        Model = s.AiModel ?? "",
        ChunkTargetLines = s.ChunkTargetLines,
        ExtraInstructions = s.AiExtraInstructions,
        ForceRefresh = forceRefresh,
        Fallback = RuleGrouperOptions.FromSettings(),
      };
    }
  }


  /// <summary>Token and cost estimate shown before a call (plan §A.5).</summary>
  public sealed class AiCostEstimate
  {
    public int Chunks { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    /// <summary>USD, or null when the model's price is not in the table.</summary>
    public double? Usd { get; init; }
    public bool Cached { get; init; }

    public string Describe()
    {
      if (Cached) return "cached answer, no request needed";
      string cost = Usd.HasValue ? FormattableString.Invariant($", about ${Usd.Value:0.000}") : ", price unknown for this model";
      return FormattableString.Invariant($"{Chunks} request(s), ~{InputTokens:N0} input + ~{OutputTokens:N0} output tokens{cost}");
    }
  }


  /// <summary>
  /// Prices in USD per million tokens for the cost estimate, matched by model-name prefix
  /// (longest match wins). Unknown models get a token count only. Checked 2026-09-14.
  /// </summary>
  public static class AiPricing
  {
    private static readonly (string prefix, double input, double output)[] Table =
    {
      ("claude-opus-5", 5, 25),
      ("claude-sonnet-5", 2, 10),
      ("claude-haiku-4-5", 1, 5),
      ("gpt-6-astra", 10.00, 50.00),
      ("gpt-5.6-sol", 4.00, 20.00),
      ("gpt-5.6-terra", 2.00, 12.00),
      ("gpt-5.6-luna", 0.20, 1.20),
      ("gpt-5.6", 4.00, 20.00),
      ("gpt-5.5", 5.00, 30.00),
      ("gpt-5.4-nano", 0.20, 1.25),
      ("gpt-5.4-mini", 0.75, 4.50),
      ("gpt-5.4", 2.50, 15.00),
      ("gpt-5.2", 1.75, 14.00),
      ("gpt-5.1", 1.25, 10.00),
      ("gpt-5-nano", 0.05, 0.40),
      ("gpt-5-mini", 0.25, 2.00),
      ("gpt-5", 1.25, 10.00),
      ("gpt-4.1-nano", 0.10, 0.40),
      ("gpt-4.1-mini", 0.40, 1.60),
      ("gpt-4.1", 2.00, 8.00),
      ("gemini-3.8-flash", 0.75, 3.75),
      ("gemini-3.7-flash", 0.75, 3.75),
      ("gemini-3.6-flash", 0.75, 3.75),
      ("gemini-3.5-flash-lite", 0.30, 2.50),
      ("gemini-3.5-flash", 1.50, 9.00),
      ("gemini-3.1-flash-lite", 0.25, 1.50),
      ("gemini-3.1-pro", 2.00, 12.00),
      ("gemini-2.5-flash-lite", 0.10, 0.40),
      ("gemini-2.5-flash", 0.30, 2.50),
      ("gemini-2.5-pro", 1.25, 10.00),
    };

    public static double? Usd(string model, int inputTokens, int outputTokens)
    {
      if (string.IsNullOrWhiteSpace(model)) return null;
      string m = model.Trim().ToLowerInvariant();
      (string prefix, double input, double output)? best = null;
      foreach (var row in Table)
        if (m.StartsWith(row.prefix, StringComparison.Ordinal) && (best == null || row.prefix.Length > best.Value.prefix.Length))
          best = row;
      if (best == null) return null;
      return inputTokens / 1e6 * best.Value.input + outputTokens / 1e6 * best.Value.output;
    }
  }


  /// <summary>
  /// The AI grouper (plan §3): chunk the kept lines at long gaps, ask the model per chunk through
  /// the bulk runner, parse and repair the answers, fall back to the rules for failed chunks,
  /// cache the result. Never throws for a bad answer; throws <see cref="ProviderException"/> only
  /// when nothing could be asked at all (no key, unknown model) and
  /// <see cref="OperationCanceledException"/> on cancel.
  /// </summary>
  public static class AiGrouper
  {
    public static AiCostEstimate Estimate(IReadOnlyList<InfoCombined> lines, SnippetLimits limits, AiGroupingOptions options)
    {
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var cache = new AiGroupingCache(options.CacheDir);
      if (!options.ForceRefresh && cache.TryGet(AiGroupingCache.KeyFor(lines, kept, options.Model, limits, options.ChunkTargetLines, options.ExtraInstructions)) != null)
        return new AiCostEstimate { Cached = true };

      bool hasSubs2 = AiGroupingPrompt.HasSubs2(lines);
      string system = AiGroupingPrompt.BuildSystem(limits.MaxSnippetMs / 1000, hasSubs2, options.ExtraInstructions);
      int systemTokens = AiGroupingPrompt.EstimateTokens(system);
      List<AiChunk> chunks = AiChunker.Split(lines, kept, limits.MaxSnippetMs, options.ChunkTargetLines);
      int input = 0, output = 0;
      foreach (AiChunk chunk in chunks)
      {
        input += systemTokens + AiGroupingPrompt.EstimateTokens(AiGroupingPrompt.BuildUser(lines, kept, chunk, hasSubs2));
        output += AiGroupingPrompt.EstimateOutputTokens(chunk.Count);
      }
      return new AiCostEstimate
      {
        Chunks = chunks.Count,
        InputTokens = input,
        OutputTokens = output,
        Usd = AiPricing.Usd(options.Model, input, output),
      };
    }

    public static async Task<AiGroupingResult> GroupAsync(IReadOnlyList<InfoCombined> lines, SnippetLimits limits,
      AiGroupingOptions options, IProgressReporter? progress, CancellationToken ct)
    {
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var cache = new AiGroupingCache(options.CacheDir);
      string key = AiGroupingCache.KeyFor(lines, kept, options.Model, limits, options.ChunkTargetLines, options.ExtraInstructions);
      if (!options.ForceRefresh)
      {
        AiGroupingResult? cached = cache.TryGet(key);
        if (cached != null && cached.KeptJoins.Length == kept.Length)
        {
          Logger.Instance.info($"AI grouping: using the cached answer ({options.Model}, prompt v{cached.PromptVersion}).");
          return cached;
        }
      }

      IChatProvider provider = ChatProviders.Create(options.Model); // throws ProviderException for an unknown prefix
      if (provider is HttpChatProvider http && !http.HasApiKey)
        throw new ProviderException(provider.Name, null,
          $"No API key configured for {provider.Name}. Set it in Preferences (AI) or the {ChatProviders.EnvVarFor(provider.Name)} environment variable.");
      string? providerName = ChatProviders.ProviderFor(options.Model) ?? provider.Name;
      (int rpm, int concurrency) limitsFor = ChatProviders.LimitsFor(providerName);
      var runner = new AiBulkRunner(options.Concurrency ?? limitsFor.concurrency, options.Rpm ?? limitsFor.rpm);

      bool hasSubs2 = AiGroupingPrompt.HasSubs2(lines);
      string system = AiGroupingPrompt.BuildSystem(limits.MaxSnippetMs / 1000, hasSubs2, options.ExtraInstructions);
      List<AiChunk> chunks = AiChunker.Split(lines, kept, limits.MaxSnippetMs, options.ChunkTargetLines);
      var result = new AiGroupingResult
      {
        Model = options.Model,
        KeptJoins = new bool[kept.Length],
        Chunks = chunks.Count,
      };
      if (kept.Length == 0) return result;

      Logger.Instance.info(FormattableString.Invariant(
        $"AI grouping: {kept.Length} lines in {chunks.Count} chunk(s) with {options.Model} (concurrency {options.Concurrency ?? limitsFor.concurrency}, {options.Rpm ?? limitsFor.rpm} rpm)."));

      BulkResult<(ChatCompletion completion, AiChunkAnswer answer)>[] outcomes = await runner.RunAsync(chunks, async (chunk, token) =>
      {
        string user = AiGroupingPrompt.BuildUser(lines, kept, chunk, hasSubs2);
        ChatCompletion completion = await provider.CompleteJsonAsync(system, user, AiGroupingPrompt.Schema, token).ConfigureAwait(false);
        return (completion, AiAnswerParser.Parse(completion.Text, chunk));
      }, progress, options.ProgressLabel, ct).ConfigureAwait(false);

      // Every request failed: the caller wants the real error (bad key, wrong model), not a silent rules grouping.
      if (Array.TrueForAll(outcomes, o => !o.Ok))
      {
        Exception? first = outcomes[0].Error;
        throw first as ProviderException
          ?? new ProviderException(provider.Name, null, first?.Message ?? "every request failed", first);
      }

      bool[]? rules = null;
      for (int c = 0; c < chunks.Count; c++)
      {
        AiChunk chunk = chunks[c];
        BulkResult<(ChatCompletion completion, AiChunkAnswer answer)> outcome = outcomes[c];
        if (outcome.Ok)
        {
          result.InputTokens += outcome.Value.completion.InputTokens;
          result.OutputTokens += outcome.Value.completion.OutputTokens;
          AiAnswerParser.ApplyToKeptJoins(outcome.Value.answer, result.KeptJoins, result.Notes);
          foreach (string repair in outcome.Value.answer.Repairs)
          {
            string line = FormattableString.Invariant($"chunk {c + 1} (lines {chunk.KeptStart}..{chunk.KeptEnd}): {repair}");
            result.Repairs.Add(line);
            Logger.Instance.info("AI grouping: repaired answer, " + line);
          }
        }
        else
        {
          result.FailedChunks++;
          string reason = outcome.Error is ProviderException pe ? pe.Message : outcome.Error?.Message ?? "unknown error";
          Logger.Instance.info(FormattableString.Invariant($"AI grouping: chunk {c + 1} (lines {chunk.KeptStart}..{chunk.KeptEnd}) failed, using the rules for it: {reason}"));
          rules ??= RuleBasedGrouper.Group(lines, kept, limits, options.Fallback);
          for (int k = chunk.KeptStart; k < chunk.KeptEnd; k++) result.KeptJoins[k] = rules[k];
        }
      }

      // Limits are enforced regardless of the producer; a cached answer is stored repaired.
      var log = new List<string>();
      result.KeptJoins = SnippetGrouping.Repair(lines, kept, result.KeptJoins, limits, log);
      foreach (string message in log)
        Logger.Instance.info("AI grouping: " + message);

      string summary = FormattableString.Invariant($"AI grouping: done, {result.InputTokens:N0} input + {result.OutputTokens:N0} output tokens");
      if (AiPricing.Usd(options.Model, result.InputTokens, result.OutputTokens) is double usd)
        summary += " (about $" + usd.ToString("0.000", CultureInfo.InvariantCulture) + ")";
      if (result.FailedChunks > 0)
        summary += FormattableString.Invariant($", {result.FailedChunks} of {result.Chunks} chunk(s) fell back to the rules");
      Logger.Instance.info(summary);

      if (result.FailedChunks == 0) cache.Put(key, result);
      return result;
    }

    /// <summary>
    /// Turn a result into a full-index join vector for <paramref name="lines"/> and put the
    /// model's notes on the first line of each snippet (<see cref="InfoCombined.GroupNote"/>),
    /// clearing stale notes first.
    /// </summary>
    public static bool[] ApplyToLines(AiGroupingResult result, IReadOnlyList<InfoCombined> lines)
    {
      int[] kept = SnippetGrouping.KeptIndices(lines);
      bool[] joins = SnippetGrouping.NoJoins(lines.Count);
      foreach (InfoCombined line in lines) line.GroupNote = null;
      if (result.KeptJoins.Length != kept.Length) return joins;
      SnippetGrouping.ApplyKeptJoins(joins, kept, result.KeptJoins);
      foreach (KeyValuePair<int, string> note in result.Notes)
      {
        int k = note.Key;
        if (k >= 0 && k < kept.Length && k < result.KeptJoins.Length && result.KeptJoins[k])
          lines[kept[k]].GroupNote = note.Value;
      }
      return joins;
    }

    /// <summary>Synchronous wrapper for the pipeline thread.</summary>
    public static AiGroupingResult Group(IReadOnlyList<InfoCombined> lines, SnippetLimits limits, AiGroupingOptions options,
      IProgressReporter? progress, CancellationToken ct)
      => GroupAsync(lines, limits, options, progress, ct).GetAwaiter().GetResult();
  }
}
