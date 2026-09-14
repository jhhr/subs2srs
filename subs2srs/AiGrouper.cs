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
  /// Standard (non-batch, uncached) prices in USD per million tokens for the cost estimate,
  /// matched by model-name prefix (longest match wins). Unknown models get a token count only.
  /// </summary>
  /// <remarks>
  /// Read from the official pricing pages on 2026-09-14:
  /// <list type="bullet">
  /// <item>Anthropic: https://claude.com/pricing#api (display names; the API IDs come from
  /// https://platform.claude.com/docs/en/about-claude/models/overview). Only models still served on
  /// the Claude API are listed (Opus 4.1 is retired there). The full 1M context is billed at the
  /// standard rate, so there is no long-context tier.</item>
  /// <item>OpenAI: https://developers.openai.com/api/docs/pricing. Legacy models (gpt-3.5-turbo,
  /// davinci-002, babbage-002) are left out. Where the page has a short/long context split the
  /// short-context price is used (gpt-5.5 and gpt-5.4 say "&lt;272K"; the threshold for gpt-6-astra
  /// and the gpt-5.6 family is not stated); a chunk never comes near it.</item>
  /// <item>Google: https://ai.google.dev/gemini-api/docs/pricing, paid tier, text input. Text chat
  /// models only (no live, transcribe, translate, image or computer-use variants). Gemini 2.5 Pro
  /// and 3.1 Pro Preview use the "prompts &lt;= 200k tokens" tier. Gemini 3.6/3.7/3.8 Flash cost
  /// $0.75 / $3.75 through 2026-12-31 and $1.50 / $7.50 from 2027-01-01.</item>
  /// </list>
  /// Batch prices are half of these on all three providers (Gemini also has a Flex tier at the
  /// same discount); cached input is 10% (Anthropic, Gemini) or about 10% (OpenAI) of the input price.
  /// </remarks>
  public static class AiPricing
  {
    private static readonly (string prefix, double input, double output)[] Table =
    {
      // Anthropic
      ("claude-fable-5-1", 10.00, 50.00),
      ("claude-fable-5", 10.00, 50.00),
      ("claude-opus-5", 5.00, 25.00),
      ("claude-opus-4-8", 5.00, 25.00),
      ("claude-opus-4-7", 5.00, 25.00),
      ("claude-opus-4-6", 5.00, 25.00),
      ("claude-opus-4-5", 5.00, 25.00),
      ("claude-sonnet-5", 2.00, 10.00),
      ("claude-sonnet-4-6", 3.00, 15.00),
      ("claude-sonnet-4-5", 3.00, 15.00),
      ("claude-haiku-4-5", 1.00, 5.00),
      // OpenAI
      ("gpt-6-astra", 10.00, 50.00),
      ("gpt-5.6-sol", 4.00, 20.00),
      ("gpt-5.6-terra", 2.00, 12.00),
      ("gpt-5.6-luna", 0.20, 1.20),
      ("gpt-5.5-pro", 30.00, 180.00),
      ("gpt-5.5", 5.00, 30.00),
      ("gpt-5.4-pro", 30.00, 180.00),
      ("gpt-5.4-mini", 0.75, 4.50),
      ("gpt-5.4-nano", 0.20, 1.25),
      ("gpt-5.4", 2.50, 15.00),
      ("gpt-5.2-pro", 21.00, 168.00),
      ("gpt-5.2", 1.75, 14.00),
      ("gpt-5.1", 1.25, 10.00),
      ("gpt-5-pro", 15.00, 120.00),
      ("gpt-5-mini", 0.25, 2.00),
      ("gpt-5-nano", 0.05, 0.40),
      ("gpt-5", 1.25, 10.00),
      ("gpt-4.1-mini", 0.40, 1.60),
      ("gpt-4.1-nano", 0.10, 0.40),
      ("gpt-4.1", 2.00, 8.00),
      ("gpt-4o-2024-05-13", 5.00, 15.00),
      ("gpt-4o-mini", 0.15, 0.60),
      ("gpt-4o", 2.50, 10.00),
      ("gpt-4-turbo-2024-04-09", 10.00, 30.00),
      ("gpt-4-0613", 30.00, 60.00),
      ("o1-pro", 150.00, 600.00),
      ("o1", 15.00, 60.00),
      ("o3-pro", 20.00, 80.00),
      ("o3-mini", 1.10, 4.40),
      ("o3", 2.00, 8.00),
      ("o4-mini", 1.10, 4.40),
      // Google
      ("gemini-3.8-flash", 0.75, 3.75),
      ("gemini-3.7-flash", 0.75, 3.75),
      ("gemini-3.6-flash", 0.75, 3.75),
      ("gemini-3.5-flash-lite", 0.30, 2.50),
      ("gemini-3.5-flash", 1.50, 9.00),
      ("gemini-3.1-flash-lite", 0.25, 1.50),
      ("gemini-3.1-pro-preview", 2.00, 12.00),
      ("gemini-3-flash-preview", 0.50, 3.00),
      ("gemini-2.5-pro", 1.25, 10.00),
      ("gemini-2.5-flash-lite", 0.10, 0.40),
      ("gemini-2.5-flash", 0.30, 2.50),
    };

    public static double? Usd(string model, int inputTokens, int outputTokens)
    {
      if (string.IsNullOrWhiteSpace(model)) return null;
      string m = model.Trim().ToLowerInvariant();
      (string prefix, double input, double output)? best = null;
      foreach (var row in Table)
      {
        // The prefix must be the whole ID or be followed by "-" (a dated snapshot or variant), so
        // "gpt-5.6" is not priced as "gpt-5" and "gemini-3.1-pro" not as anything.
        bool matches = m.StartsWith(row.prefix, StringComparison.Ordinal)
          && (m.Length == row.prefix.Length || m[row.prefix.Length] == '-');
        if (matches && (best == null || row.prefix.Length > best.Value.prefix.Length))
          best = row;
      }
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

      string system = AiGroupingPrompt.BuildSystem(limits.MaxSnippetMs / 1000, options.ExtraInstructions);
      int systemTokens = AiGroupingPrompt.EstimateTokens(system);
      List<AiChunk> chunks = AiChunker.Split(lines, kept, limits.MaxSnippetMs, options.ChunkTargetLines);
      int input = 0, output = 0;
      foreach (AiChunk chunk in chunks)
      {
        input += systemTokens + AiGroupingPrompt.EstimateTokens(AiGroupingPrompt.BuildUser(lines, kept, chunk));
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

      string system = AiGroupingPrompt.BuildSystem(limits.MaxSnippetMs / 1000, options.ExtraInstructions);
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
        string user = AiGroupingPrompt.BuildUser(lines, kept, chunk);
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
