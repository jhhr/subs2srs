using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Eval;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The subs2srs.Eval console through its runner, with the fake provider for the model path.
  /// No network: every model call goes to <see cref="FakeChatProvider"/>.
  /// </summary>
  public class EvalRunnerTests
  {
    private static readonly SnippetLimits Limits = new SnippetLimits(15_000, 500, 0);

    /// <summary>Set with one tuning file (truth = what the rules produce) and one hold-out file (truth joins 0-1-2).</summary>
    private static string MakeSet(TestScope scope)
    {
      string set = Path.Combine(scope.TempDir, "validation");
      var lines = SnippetGroupingTests.Dialogue();
      GroupingValidationFile.Build(lines, new[] { true, false, false, false }, new[] { true, false, true, false }, "rules", Limits, 1, "ep01.srt", null)
        .Write(Path.Combine(set, "show_1.grouping.json"));
      var lines2 = SnippetGroupingTests.Dialogue();
      lines2[3].Subs1.Text = "Bye bye."; // a different episode text, so the two files never share a cache entry
      GroupingValidationFile.Build(lines2, new[] { true, true, false, false }, null, "rules", Limits, 2, "ep02.srt", null)
        .Write(Path.Combine(set, "holdout", "show_2.grouping.json"));
      return set;
    }

    private static EvalOptions Options(string set, string? model = null) => new EvalOptions
    {
      SetDir = set,
      Model = model,
      ChunkTargetLines = 0,
      CacheDir = Path.Combine(set, "..", "cache"),
      Concurrency = 1,
      Rpm = 0,
    };

    private static async Task<(EvalRun? run, string output)> Run(EvalOptions options)
    {
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      EvalRun? run = await new EvalRunner(options, stdout, stderr).RunAsync(CancellationToken.None);
      return (run, stdout.ToString() + stderr.ToString());
    }

    // ── rules ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Rules_ScoresEveryFile_AggregatesBySet_AndWritesReports()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      EvalOptions options = Options(set);

      (EvalRun? run, string output) = await Run(options);

      Assert.NotNull(run);
      Assert.Equal("rules", run!.Name);
      Assert.Equal("rules", run.Producer);
      Assert.Null(run.Model);
      Assert.Equal(2, run.Files.Count);

      EvalFileRecord holdout = run.Files.Single(f => f.IsHoldout);
      EvalFileRecord tuning = run.Files.Single(f => !f.IsHoldout);
      Assert.Equal("holdout/show_2.grouping.json", holdout.RelativePath);
      Assert.Equal(new[] { true, false, false }, tuning.Predicted!.Take(3));
      Assert.Equal(1.0, tuning.Score!.F1);
      Assert.Equal(1, tuning.ProposalFlips);             // human flipped join 2 of the rules proposal
      Assert.Equal("rules", tuning.ProposalProducer);
      Assert.Null(holdout.ProposalFlips);
      Assert.Equal(0.5, holdout.Score!.Recall);          // truth joins 0-1 and 1-2, rules only 0-1
      Assert.Equal(1, holdout.Score.UnderMergedSnippets);

      Assert.Equal(1.0, run.Tuning.F1);
      Assert.Equal(2.0 / 3, run.Holdout.F1, 9);
      Assert.Equal(2, run.All.Files);
      Assert.Equal(2, run.All.TruePositives);
      Assert.Equal(1, run.All.FalseNegatives);
      Assert.Null(run.SpentUsd);

      Assert.Contains("TUNING", output);
      Assert.Contains("HOLD-OUT", output);
      Assert.Contains("F1 100.0", output);
      Assert.Contains("labelled from rules, 1 flip(s)", output);

      string outDir = Path.Combine(set, "eval-reports");
      Assert.True(File.Exists(Path.Combine(outDir, "rules.run.json")));
      string csv = File.ReadAllText(Path.Combine(outDir, "rules.csv"));
      string[] csvLines = csv.TrimEnd().Split('\n');
      Assert.Equal(1 + 2 + 3, csvLines.Length); // header, 2 files, TUNING/HOLD-OUT/ALL
      Assert.StartsWith("file,set,lines,boundaries,tp,fp,fn,precision,recall,f1", csvLines[0]);
      Assert.Contains("holdout/show_2.grouping.json,holdout,4,3,1,0,1,1.0000,0.5000,0.6667", csv);
      string md = File.ReadAllText(Path.Combine(outDir, "rules.md"));
      Assert.Contains("# Grouping eval: rules", md);
      Assert.Contains("| **ALL** |", md);

      EvalRun back = EvalRun.Read(Path.Combine(outDir, "rules.run.json"));
      Assert.Equal(run.Files.Count, back.Files.Count);
      Assert.Equal(run.Files[0].Predicted, back.Files[0].Predicted);
      Assert.Equal(run.All.TruePositives, back.All.TruePositives);
      Assert.Equal(1500, back.RuleOptions!.MaxJoinGapMs);
    }

    [Fact]
    public async Task Rules_OptionsChangeTheGrouping()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      EvalOptions options = Options(set);
      options.RuleOptions.RequireCue = false;
      options.RuleOptions.MaxJoinGapMs = 300; // the 0.4 s question/answer gap no longer qualifies; 0.2 s "See you later." / "Bye." does
      options.Name = "gap300";

      (EvalRun? run, _) = await Run(options);
      Assert.Equal(new[] { false, false, true }, run!.Files.Single(f => !f.IsHoldout).Predicted!.Take(3));
      Assert.Equal("gap300", run.Name);
      Assert.Contains("no cue needed", run.Describe());
    }

    [Fact]
    public async Task EmptySet_Fails_WithExitCode2()
    {
      using var scope = new TestScope();
      string set = Path.Combine(scope.TempDir, "empty");
      Directory.CreateDirectory(set);
      var ex = await Assert.ThrowsAsync<EvalException>(() => Run(Options(set)));
      Assert.Equal(EvalOptions.ExitNoFiles, ex.ExitCode);
    }

    // ── model path with the fake provider ──────────────────────────────

    [Fact]
    public async Task Model_RunsThroughTheFakeProvider_ScoresTokensAndCost_AndCachesTheSecondRun()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      var fake = new FakeChatProvider("claude-sonnet-5", FakeChatProvider.JoinAll("q&a"));
      using var installed = fake.Install();
      EvalOptions options = Options(set, "claude-sonnet-5");

      (EvalRun? run, string output) = await Run(options);

      Assert.NotNull(run);
      Assert.Equal("ai", run!.Producer);
      Assert.Equal("claude-sonnet-5_v" + AiGroupingPrompt.PromptVersion + "_c0", run.Name); // chunk 0 is not the default
      Assert.Equal(AiGroupingPrompt.PromptVersion, run.PromptVersion);
      Assert.Equal(2, fake.Requests.Count); // one chunk per file
      EvalFileRecord tuning = run.Files.Single(f => !f.IsHoldout);
      Assert.Equal(new[] { true, true, true }, tuning.Predicted!.Take(3)); // JoinAll, 7.2 s span fits the limit
      Assert.Equal(1, tuning.Score!.TruePositives);
      Assert.Equal(2, tuning.Score.FalsePositives);
      Assert.Equal(1.0, tuning.Score.Recall);
      Assert.False(tuning.Cached);
      Assert.Equal(1, tuning.Chunks);
      Assert.Equal(1000, tuning.InputTokens);
      Assert.Equal(50, tuning.OutputTokens);
      Assert.Equal(AiPricing.Usd("claude-sonnet-5", 1000, 50), tuning.Usd);
      Assert.Equal(2000, run.SpentInputTokens);
      Assert.Equal(AiPricing.Usd("claude-sonnet-5", 2000, 100), run.SpentUsd);
      Assert.Equal(run.SpentUsd, run.All.Usd);
      Assert.Contains("Estimate: 2 request(s)", output);
      Assert.Contains("Spent this run: 2,000 input + 100 output tokens", output);
      Assert.Contains("0 of 2 file(s) from the cache", output);

      // Same options again: everything is served from the cache, nothing is spent.
      (EvalRun? again, string output2) = await Run(options);
      Assert.Equal(2, fake.Requests.Count);
      Assert.All(again!.Files, f => Assert.True(f.Cached));
      Assert.Equal(0, again.SpentInputTokens);
      Assert.Equal(2000, again.All.InputTokens); // the cached answers still carry their usage
      Assert.Contains("2 of 2 file(s) already cached", output2);
      Assert.Contains("2 of 2 file(s) from the cache", output2);

      // --refresh asks again.
      options.Refresh = true;
      (EvalRun? refreshed, _) = await Run(options);
      Assert.Equal(4, fake.Requests.Count);
      Assert.All(refreshed!.Files, f => Assert.False(f.Cached));
    }

    [Fact]
    public async Task Model_EstimateOnly_AndCostCap_StopBeforeAnyRequest()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      var fake = new FakeChatProvider("claude-sonnet-5");
      using var installed = fake.Install();

      EvalOptions estimate = Options(set, "claude-sonnet-5");
      estimate.EstimateOnly = true;
      (EvalRun? run, string output) = await Run(estimate);
      Assert.Null(run);
      Assert.Empty(fake.Requests);
      Assert.Contains("--estimate: stopping before any request.", output);
      Assert.Contains("about $", output);

      EvalOptions capped = Options(set, "claude-sonnet-5");
      capped.MaxCostUsd = 0.0;
      var ex = await Assert.ThrowsAsync<EvalException>(() => Run(capped));
      Assert.Equal(EvalOptions.ExitCostCap, ex.ExitCode);
      Assert.Contains("exceeds --max-cost", ex.Message);
      Assert.Empty(fake.Requests);
      Assert.False(Directory.Exists(Path.Combine(set, "eval-reports")));

      EvalOptions unpriced = Options(set, "mystery-model");
      unpriced.MaxCostUsd = 5;
      var ex2 = await Assert.ThrowsAsync<EvalException>(() => Run(unpriced));
      Assert.Contains("no price in the table", ex2.Message);
      Assert.Empty(fake.Requests);

      EvalOptions generous = Options(set, "claude-sonnet-5");
      generous.MaxCostUsd = 5;
      (EvalRun? ok, _) = await Run(generous);
      Assert.NotNull(ok);
      Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Model_ProviderFailure_IsRecordedPerFile_ReportsWritten_ExitCode4()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      var fake = new FakeChatProvider("claude-sonnet-5") { Failure = new ProviderException("fake", 401, "invalid x-api-key") };
      using var installed = fake.Install();
      EvalOptions options = Options(set, "claude-sonnet-5");

      var ex = await Assert.ThrowsAsync<EvalException>(() => Run(options));
      Assert.Equal(EvalOptions.ExitAllFailed, ex.ExitCode);
      Assert.Contains("invalid x-api-key", ex.Message);

      EvalRun run = EvalRun.Read(Path.Combine(set, "eval-reports", options.EffectiveName + EvalRun.Extension));
      Assert.All(run.Files, f => Assert.Contains("invalid x-api-key", f.Error));
      Assert.All(run.Files, f => Assert.Null(f.Score));
      Assert.Equal(0, run.All.Files);
      Assert.Contains("| - | - | - |", run.ToMarkdown()); // no numbers for an empty aggregate
    }

    [Fact]
    public async Task Model_ExtraInstructions_ReachThePrompt_AndNameTheRun()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      var fake = new FakeChatProvider("claude-sonnet-5");
      using var installed = fake.Install();
      EvalOptions options = Options(set, "claude-sonnet-5");
      options.ExtraInstructions = "Never group across speakers.";
      options.ChunkTargetLines = 50;

      (EvalRun? run, _) = await Run(options);
      Assert.StartsWith("claude-sonnet-5_v" + AiGroupingPrompt.PromptVersion + "_p", run!.Name);
      Assert.EndsWith("_c50", run.Name);
      Assert.Equal("Never group across speakers.", run.ExtraInstructions);
      Assert.All(fake.Requests, r => Assert.Contains("Never group across speakers.", r.system));
      Assert.Contains("with extra instructions", run.Describe());
    }

    // ── compare ────────────────────────────────────────────────────────

    [Fact]
    public async Task Compare_DiffsBoundaries_AgainstAnEarlierRun()
    {
      using var scope = new TestScope();
      string set = MakeSet(scope);
      (EvalRun? rules, _) = await Run(Options(set));
      Assert.NotNull(rules);

      var fake = new FakeChatProvider("claude-sonnet-5", FakeChatProvider.JoinAll());
      using var installed = fake.Install();
      EvalOptions options = Options(set, "claude-sonnet-5");
      options.Compare = "rules"; // by run name in the out dir
      (EvalRun? ai, string output) = await Run(options);

      Assert.Contains("## Compared with rules", output);
      // tuning file: truth 100, rules 100, ai 111 -> boundaries 1 and 2 differ, both broken
      Assert.Contains("show_1.grouping.json: F1 100.0 -> 50.0 (-50.0), 2 boundary(ies) differ: 0 fixed, 2 broken", output);
      // hold-out: truth 110, rules 100, ai 111 -> boundary 1 fixed, boundary 2 broken
      Assert.Contains("holdout/show_2.grouping.json: F1 66.7 -> 80.0 (+13.3), 2 boundary(ies) differ: 1 fixed, 1 broken", output);
      Assert.Contains("2 file(s) compared; F1 (all) 80.0 -> 66.7 (-13.3); 1 boundary(ies) fixed, 3 broken.", output);
      Assert.Contains("| 1 | join | join | - | To the station. | See you later. |", output);
      Assert.Contains("| 2 | - | join | - | See you later. | Bye. |", output);

      string md = File.ReadAllText(Path.Combine(set, "eval-reports", ai!.Name + ".md"));
      Assert.Contains("## Compared with rules", md);

      options.Compare = Path.Combine(set, "eval-reports", "nope.run.json");
      var ex = await Assert.ThrowsAsync<EvalException>(() => Run(options));
      Assert.Contains("--compare: no run file", ex.Message);
    }

    // ── command line ───────────────────────────────────────────────────

    [Fact]
    public void Options_Parse_CoversEveryFlag()
    {
      using var scope = new TestScope();
      string promptFile = Path.Combine(scope.TempDir, "prompt.txt");
      File.WriteAllText(promptFile, "  Be strict.\n");
      EvalOptions o = EvalOptions.Parse(new[]
      {
        "--set", "v", "--model", "gpt-5.6-luna", "--prompt", promptFile, "--chunk", "0", "--refresh", "--cache", "c",
        "--rpm", "10", "--concurrency", "0", "--max-cost", "1.5", "--estimate", "--out", "o", "--name", "my run/1",
        "--compare", "rules", "--rules-gap", "900", "--rules-cues", "?", "--rules-no-cue", "--rules-no-actor",
        "--prefs", "p.json", "--verbose",
      });
      Assert.Equal("v", o.SetDir);
      Assert.Equal("gpt-5.6-luna", o.Model);
      Assert.Equal("Be strict.", o.ExtraInstructions);
      Assert.Equal(0, o.ChunkTargetLines);
      Assert.True(o.Refresh);
      Assert.Equal("c", o.CacheDir);
      Assert.Equal(10, o.Rpm);
      Assert.Equal(1, o.Concurrency);
      Assert.Equal(1.5, o.MaxCostUsd);
      Assert.True(o.EstimateOnly);
      Assert.Equal("o", o.OutDir);
      Assert.Equal("o", o.EffectiveOutDir);
      Assert.Equal("my-run-1", o.EffectiveName);
      Assert.Equal("rules", o.Compare);
      Assert.Equal(900, o.RuleOptions.MaxJoinGapMs);
      Assert.Equal("?", o.RuleOptions.CueChars);
      Assert.False(o.RuleOptions.RequireCue);
      Assert.False(o.RuleOptions.JoinOnActorChange);
      Assert.Equal("p.json", o.PrefsPath);
      Assert.True(o.Verbose);

      EvalOptions literal = EvalOptions.Parse(new[] { "--set", "v", "--model", "m", "--prompt", "Keep it short." });
      Assert.Equal("Keep it short.", literal.ExtraInstructions);
      Assert.Equal("m_v" + AiGroupingPrompt.PromptVersion + "_p" + Sha8("Keep it short."), literal.EffectiveName);

      EvalOptions rules = EvalOptions.Parse(new[] { "--set", "v", "--no-prefs" });
      Assert.True(rules.Rules);
      Assert.True(rules.NoPrefs);
      Assert.Equal("rules", rules.EffectiveName);
      Assert.Equal(Path.Combine("v", "eval-reports"), rules.EffectiveOutDir);
      Assert.True(EvalOptions.Parse(new[] { "--help" }).Help);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--set")]
    [InlineData("--set v --chunk x")]
    [InlineData("--set v --chunk -1")]
    [InlineData("--set v --max-cost lots")]
    [InlineData("--set v --model")]
    [InlineData("--set v --model m --prompt @missing.txt")]
    [InlineData("--set v --rules --estimate")]
    [InlineData("--set v --refresh")]
    [InlineData("--set v --frobnicate")]
    public void Options_Parse_RejectsBadUsage(string args)
    {
      var ex = Assert.Throws<EvalException>(() => EvalOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
      Assert.Equal(EvalOptions.ExitUsage, ex.ExitCode);
    }

    private static string Sha8(string s) =>
      Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).Substring(0, 8).ToLowerInvariant();

    // ── the built console, end to end (rules baseline sanity, plan phase 4) ──

    [Fact]
    public async Task Console_RulesBaseline_OnAValidationFileBuiltFromTheTestMedia()
    {
      using var scope = new TestScope();
      // The validation file comes from the real SRT parser on the test dialogue, as the preview would produce it.
      string srt = TestMedia.WriteDialogueSrt(scope.TempDir);
      List<InfoLine> parsed = new SubsParserSRT(srt, Encoding.UTF8).parse();
      Assert.Equal(TestMedia.DialogueLines.Length, parsed.Count);
      var lines = parsed.Select(l => new InfoCombined(l, new InfoLine(l.StartTime, l.EndTime, ""), true)).ToList();
      string set = Path.Combine(scope.TempDir, "validation");
      GroupingValidationFile.Build(lines, new[] { true, false, false, false }, null, "rules", Limits, 1, srt, null)
        .Write(Path.Combine(set, "dialogue_1.grouping.json"));

      string dll = Path.Combine(AppContext.BaseDirectory, "subs2srs.Eval.dll");
      Assert.True(File.Exists(dll), "subs2srs.Eval.dll is not next to the tests");
      var psi = new ProcessStartInfo("dotnet")
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = scope.TempDir,
      };
      foreach (string a in new[] { dll, "--set", set, "--rules", "--no-prefs", "--out", Path.Combine(scope.TempDir, "reports") })
        psi.ArgumentList.Add(a);
      using Process process = Process.Start(psi)!;
      string stdout = await process.StandardOutput.ReadToEndAsync();
      string stderr = await process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync();

      Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}\n{stdout}\n{stderr}");
      Assert.Contains("dialogue_1.grouping.json: F1 100.0 (P 100.0, R 100.0), exact 3/3, over 0, under 0", stdout);
      Assert.Contains("ALL", stdout);
      EvalRun run = EvalRun.Read(Path.Combine(scope.TempDir, "reports", "rules.run.json"));
      Assert.Equal(1.0, run.All.F1);
      Assert.Equal(4, run.All.Lines);
      Assert.True(File.Exists(Path.Combine(scope.TempDir, "reports", "rules.csv")));
      Assert.True(File.Exists(Path.Combine(scope.TempDir, "reports", "rules.md")));
    }
  }
}
