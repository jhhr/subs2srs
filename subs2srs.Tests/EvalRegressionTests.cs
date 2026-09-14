using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace subs2srs.Tests
{
  /// <summary>
  /// The regression gate of plan §9.3: score the *cached* answers of the configured model
  /// (Fixtures/eval/regression.json) against the hold-out validation files, with no network.
  /// Skipped until the fixtures exist; see Fixtures/eval/README.md for how to fill them.
  /// </summary>
  public class EvalRegressionTests
  {
    private readonly ITestOutputHelper output;

    public EvalRegressionTests(ITestOutputHelper output) { this.output = output; }

    [RequiresEvalFixturesFact]
    public void CachedOutputs_ReachTheF1Floor_OnTheHoldoutSet()
    {
      EvalRegressionConfig config = EvalFixtures.ReadConfig()!;
      (GroupingScore all, List<string> missing) = ScoreCached(config, output.WriteLine);
      Assert.True(missing.Count == 0,
        "hold-out files without a cached answer for the configured run (refresh the cache, see Fixtures/eval/README.md): " + string.Join(", ", missing));
      Assert.True(all.F1 >= config.MinBoundaryF1,
        FormattableString.Invariant($"boundary F1 {all.F1:0.000} on the hold-out set is below the floor {config.MinBoundaryF1:0.000} ({config.Model}, prompt v{AiGroupingPrompt.PromptVersion})"));
    }

    /// <summary>Score every hold-out file that has a cached answer; report the ones that have none.</summary>
    internal static (GroupingScore all, List<string> missing) ScoreCached(EvalRegressionConfig config, Action<string> log)
    {
      var scores = new List<GroupingScore>();
      var missing = new List<string>();
      foreach ((GroupingEvalFile file, string key, AiGroupingResult? cached) in EvalFixtures.Lookup(config))
      {
        if (cached == null) { missing.Add(file.RelativePath); continue; }
        GroupingScore s = GroupingScorer.Score(file.File.Joins, cached.KeptJoins, file.File.Lines.Count);
        s.InputTokens = cached.InputTokens;
        s.OutputTokens = cached.OutputTokens;
        scores.Add(s);
        log(FormattableString.Invariant($"{file.RelativePath}: F1 {s.F1:0.000} (P {s.Precision:0.000}, R {s.Recall:0.000}), exact {s.ExactSnippets}/{s.TruthSnippets}, over {s.OverMergedSnippets}, under {s.UnderMergedSnippets} [cache {key.Substring(0, 8)}]"));
      }
      GroupingScore all = GroupingScore.Sum(scores);
      log(FormattableString.Invariant($"hold-out: {all.Files} file(s), F1 {all.F1:0.000}, floor {config.MinBoundaryF1:0.000}"));
      return (all, missing);
    }

    // ── the gate itself, on a synthetic fixture directory ──────────────

    [Fact]
    public async Task Gate_SkipsWithoutFixtures_AndScoresCachedAnswersWhenTheyExist()
    {
      using var scope = new TestScope();
      string dir = Path.Combine(scope.TempDir, "fixtures");
      Directory.CreateDirectory(dir);
      string? previous = EvalFixtures.Override;
      EvalFixtures.Override = dir;
      try
      {
        Assert.Contains("no Fixtures/eval/regression.json", EvalFixtures.SkipReason());

        File.WriteAllText(Path.Combine(dir, "regression.json"),
          "{ \"model\": \"claude-sonnet-5\", \"chunkTargetLines\": 0, \"minBoundaryF1\": 0.9 }", new UTF8Encoding(false));
        Assert.Contains("no hold-out validation files", EvalFixtures.SkipReason());

        var lines = SnippetGroupingTests.Dialogue();
        GroupingValidationFile.Build(lines, new[] { true, false, false, false }, null, "rules", new SnippetLimits(15_000, 500, 0), 1, "ep01.srt", null)
          .Write(Path.Combine(dir, "holdout", "show_1.grouping.json"));
        Assert.Contains("no cached outputs of claude-sonnet-5 for prompt v" + AiGroupingPrompt.PromptVersion, EvalFixtures.SkipReason());

        // Fill the cache exactly as the README says: subs2srs.Eval --set <dir> --cache <dir>/cache, here with the fake provider.
        var fake = new FakeChatProvider("claude-sonnet-5", FakeChatProvider.JoinPairs());
        using (fake.Install())
        {
          var options = new subs2srs.Eval.EvalOptions
          {
            SetDir = dir, Model = "claude-sonnet-5", ChunkTargetLines = 0, CacheDir = Path.Combine(dir, "cache"),
            OutDir = Path.Combine(scope.TempDir, "reports"), Concurrency = 1,
          };
          await new subs2srs.Eval.EvalRunner(options, new StringWriter(), new StringWriter()).RunAsync(CancellationToken.None);
        }
        Assert.Single(fake.Requests);
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "cache"), "*.json"));
        Assert.Null(EvalFixtures.SkipReason());

        EvalRegressionConfig config = EvalFixtures.ReadConfig()!;
        var log = new List<string>();
        (GroupingScore all, List<string> missing) = ScoreCached(config, log.Add);
        Assert.Empty(missing);
        Assert.Equal(1, all.Files);
        // JoinPairs joins 0-1 and 2-3; truth joins only 0-1.
        Assert.Equal(1, all.TruePositives);
        Assert.Equal(1, all.FalsePositives);
        Assert.Equal(2.0 / 3, all.F1, 9);
        Assert.Contains(log, l => l.StartsWith("holdout/show_1.grouping.json: F1 0.667", StringComparison.Ordinal));
        Assert.True(all.F1 < config.MinBoundaryF1); // this synthetic answer would fail the gate

        // A second hold-out file without a cached answer is reported as missing, not silently skipped.
        var lines2 = SnippetGroupingTests.Dialogue();
        lines2[0].Subs1.Text = "Where to?";
        GroupingValidationFile.Build(lines2, new[] { true, false, false, false }, null, "rules", new SnippetLimits(15_000, 500, 0), 2, "ep02.srt", null)
          .Write(Path.Combine(dir, "holdout", "show_2.grouping.json"));
        Assert.Null(EvalFixtures.SkipReason()); // one cached answer is enough to run
        (_, missing) = ScoreCached(config, log.Add);
        Assert.Equal(new[] { "holdout/show_2.grouping.json" }, missing);
      }
      finally
      {
        EvalFixtures.Override = previous;
      }
    }

    [Fact]
    public void CheckedInFixtures_HaveAConfigButNoData_SoTheGateSkips()
    {
      // The repository ships regression.json as the template of the convention; the gate is armed by adding data.
      Assert.True(File.Exists(EvalFixtures.ConfigPath), "Fixtures/eval/regression.json should be copied next to the tests");
      EvalRegressionConfig? config = EvalFixtures.ReadConfig();
      Assert.NotNull(config);
      Assert.InRange(config!.MinBoundaryF1, 0.0, 1.0);
      string? reason = EvalFixtures.SkipReason();
      if (reason != null) output.WriteLine("gate skipped: " + reason);
      else output.WriteLine("gate armed: the hold-out fixtures and cache exist");
    }
  }
}
