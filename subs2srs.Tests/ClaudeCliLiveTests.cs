using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The one test that starts a real <c>claude</c> process. Skipped unless SUBS2SRS_AI_CLI_LIVE is
  /// set; Claude Code must be installed and signed in to a Claude subscription. Run by hand:
  ///   SUBS2SRS_AI_CLI_LIVE=1 dotnet test --filter FullyQualifiedName~ClaudeCliLiveTests
  /// Optionally set SUBS2SRS_AI_CLI_LIVE_MODEL (default terminal-claude-sonnet-5) and
  /// SUBS2SRS_AI_CLI_LIVE_DUMP to a file the raw CLI output is written to, which is how the open
  /// points of plans/claude-cli-plan.md §3.2 get closed: the exact result JSON and the wall clock.
  /// Costs subscription usage, not API tokens. No API key is passed to the process.
  /// </summary>
  public class ClaudeCliLiveTests
  {
    public const string Variable = "SUBS2SRS_AI_CLI_LIVE";
    public const string ModelVariable = "SUBS2SRS_AI_CLI_LIVE_MODEL";
    public const string DumpVariable = "SUBS2SRS_AI_CLI_LIVE_DUMP";

    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public ClaudeCliLiveTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [RequiresEnvFact(Variable)]
    public async Task LiveCli_GroupsAQuestionAndItsAnswer()
    {
      string model = (Environment.GetEnvironmentVariable(ModelVariable) ?? "terminal-claude-sonnet-5").Trim();
      Assert.True(ClaudeCli.IsTerminalModel(model), model + " is not a terminal- model");

      using var scope = new TestScope();
      ClaudeCliProvider.Reset();
      ClaudeCli.ResetProbeCache();

      string? exe = ClaudeCli.Find(ConstantSettings.ClaudeCliPath);
      Assert.False(string.IsNullOrWhiteSpace(exe), ClaudeCliProvider.NoCliMessage);

      // Everything the CLI actually printed, so the classifier can be taught shapes it does not know.
      string? dump = Environment.GetEnvironmentVariable(DumpVariable);
      var log = new List<string>();
      Func<CliRequest, CancellationToken, Task<CliProcessResult>>? saved = ClaudeCliProvider.RunnerOverride;

      var lines = new List<InfoCombined>
      {
        SnippetGroupingTests.Line(1.0, 2.0, "どこに行くの？", actor: "A"),
        SnippetGroupingTests.Line(2.4, 3.4, "駅だよ。", actor: "B"),
        SnippetGroupingTests.Line(30.0, 31.0, "今日はいい天気ですね。"),
        SnippetGroupingTests.Line(60.0, 61.5, "さようなら。"),
      };
      var options = new AiGroupingOptions
      {
        Model = model,
        ChunkTargetLines = 0,
        CacheDir = Path.Combine(scope.TempDir, "cache"),
        ForceRefresh = true,
        Concurrency = 1,
      };

      ClaudeCliProvider.RunnerOverride = async (request, ct) =>
      {
        CliProcessResult r = await ClaudeCliProvider.DefaultRunner(request, ct).ConfigureAwait(false);
        log.Add("args: " + string.Join(" ", request.Arguments));
        log.Add(FormattableString.Invariant($"exit {r.ExitCode}, timedOut {r.TimedOut}"));
        log.Add("stdout: " + r.Stdout);
        log.Add("stderr: " + r.Stderr);
        return r;
      };

      var clock = Stopwatch.StartNew();
      AiGroupingResult result;
      try
      {
        result = await AiGrouper.GroupAsync(lines, new SnippetLimits(15_000, 500, 0), options, null, CancellationToken.None);
      }
      finally
      {
        clock.Stop();
        ClaudeCliProvider.RunnerOverride = saved;
        log.Add(FormattableString.Invariant($"{model} via {exe}: {clock.Elapsed.TotalSeconds:0.0} s wall clock"));
        foreach (string line in log) output.WriteLine(line);
        if (!string.IsNullOrWhiteSpace(dump)) File.AppendAllLines(dump, log);
      }

      Assert.Equal(0, result.FailedChunks);
      // The only defensible group is the question and its answer; the two later lines are 30 s apart.
      Assert.True(result.KeptJoins[0], "expected the question and its answer to be joined");
      Assert.False(result.KeptJoins[1]);
      Assert.False(result.KeptJoins[2]);
    }

    /// <summary>
    /// What this build of the CLI actually offers, so the probe's assumptions can be checked
    /// against a real <c>claude --help</c> without spending any subscription usage.
    /// </summary>
    [RequiresEnvFact(Variable)]
    public void LiveCli_HelpMentionsTheFlagsWeRelyOn()
    {
      string? exe = ClaudeCli.Find(ConstantSettings.ClaudeCliPath);
      Assert.False(string.IsNullOrWhiteSpace(exe), ClaudeCliProvider.NoCliMessage);

      ClaudeCli.ResetProbeCache();
      var provider = new ClaudeCliProvider("terminal-claude-sonnet-5");
      ClaudeCliFeatures features = ClaudeCli.Probe(exe!, ReadHelp);

      Assert.True(features.Print, "-p is gone from this build of the CLI");
      Assert.True(features.Model, "--model is gone from this build of the CLI");
      Assert.True(features.OutputFormat, "--output-format is gone from this build of the CLI");
      Assert.True(features.JsonSchema, "--json-schema is gone; structured output would have to come from the text");
      Assert.NotNull(provider.Executable);
    }

    private static string? ReadHelp(string exe)
    {
      var psi = new System.Diagnostics.ProcessStartInfo
      {
        FileName = exe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      psi.ArgumentList.Add("--help");
      using var process = Process.Start(psi)!;
      string text = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
      process.WaitForExit(30_000);
      return text;
    }
  }
}
