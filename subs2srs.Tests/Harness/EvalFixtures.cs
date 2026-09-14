using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace subs2srs.Tests.Harness
{
  /// <summary>regression.json in the eval fixtures: which cached run the hold-out gate scores.</summary>
  public sealed class EvalRegressionConfig
  {
    public string Model { get; set; } = "";
    public int ChunkTargetLines { get; set; } = AiChunker.DefaultTargetLines;
    public string? ExtraInstructions { get; set; }
    /// <summary>Boundary F1 the cached outputs must reach on the hold-out set (0..1).</summary>
    public double MinBoundaryF1 { get; set; } = 0.9;
  }


  /// <summary>
  /// The checked-in evaluation fixtures (<c>subs2srs.Tests/Fixtures/eval</c>, see its README):
  /// <c>regression.json</c>, <c>holdout/*.grouping.json</c> and <c>cache/</c> with the model's
  /// answers as written by <c>subs2srs.Eval --cache</c>. Nothing here touches the network.
  /// </summary>
  public static class EvalFixtures
  {
    /// <summary>Test hook: point the fixtures at another directory (a synthetic set in a temp dir).</summary>
    public static string? Override { get; set; }

    public static string Dir => Override ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "eval");
    public static string ConfigPath => Path.Combine(Dir, "regression.json");
    public static string HoldoutDir => Path.Combine(Dir, GroupingEvalSet.HoldoutDirName);
    public static string CacheDir => Path.Combine(Dir, "cache");

    public static EvalRegressionConfig? ReadConfig()
    {
      if (!File.Exists(ConfigPath)) return null;
      var config = JsonSerializer.Deserialize<EvalRegressionConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
      return config == null || string.IsNullOrWhiteSpace(config.Model) ? null : config;
    }

    /// <summary>The hold-out files with the cache key of the configured run and the cached answer, if any.</summary>
    public static List<(GroupingEvalFile file, string key, AiGroupingResult? cached)> Lookup(EvalRegressionConfig config)
    {
      var cache = new AiGroupingCache(CacheDir);
      var rows = new List<(GroupingEvalFile, string, AiGroupingResult?)>();
      foreach (GroupingEvalFile file in GroupingEvalSet.Load(Dir).Holdout)
      {
        List<InfoCombined> lines = file.File.ToLines();
        int[] kept = SnippetGrouping.KeptIndices(lines);
        string key = AiGroupingCache.KeyFor(lines, kept, config.Model, file.File.Limits.ToSnippetLimits(), config.ChunkTargetLines, config.ExtraInstructions);
        AiGroupingResult? cached = cache.TryGet(key);
        if (cached != null && cached.KeptJoins.Length != kept.Length) cached = null;
        rows.Add((file, key, cached));
      }
      return rows;
    }

    /// <summary>Null when the regression test can run, else why it is skipped.</summary>
    public static string? SkipReason()
    {
      try
      {
        EvalRegressionConfig? config = ReadConfig();
        if (config == null) return "no Fixtures/eval/regression.json with a model";
        List<(GroupingEvalFile file, string key, AiGroupingResult? cached)> rows = Lookup(config);
        if (rows.Count == 0) return "no hold-out validation files in Fixtures/eval/holdout";
        if (rows.All(r => r.cached == null))
          return FormattableString.Invariant($"no cached outputs of {config.Model} for prompt v{AiGroupingPrompt.PromptVersion} in Fixtures/eval/cache; refresh them as described in Fixtures/eval/README.md");
        return null;
      }
      catch (Exception ex) when (ex is IOException || ex is JsonException)
      {
        return "eval fixtures unreadable: " + ex.Message;
      }
    }
  }


  /// <summary>A [Fact] that runs only when the eval fixtures hold cached outputs for the current prompt version.</summary>
  public sealed class RequiresEvalFixturesFactAttribute : FactAttribute
  {
    public RequiresEvalFixturesFactAttribute()
    {
      string? reason = EvalFixtures.SkipReason();
      if (reason != null) Skip = reason;
    }
  }
}
