using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <see cref="ClaudeCliProvider"/> without ever starting a process: a scripted runner stands in
  /// for <c>claude</c> and a fake clock for the waits (plan <c>plans/claude-cli-plan.md</c> §7,
  /// mirroring the Python addon's <c>test_terminal_client.py</c>).
  /// </summary>
  public class ClaudeCliProviderTests : IDisposable
  {
    private const string Exe = @"C:\fake\claude.exe";
    private static readonly string Help =
      "-p, --print --model <m> --output-format <f> --json-schema <s> --system-prompt <p> "
      + "--tools <t> --no-session-persistence --safe-mode --effort <e>";

    private readonly List<CliRequest> requests = new List<CliRequest>();
    private readonly List<TimeSpan> delays = new List<TimeSpan>();
    private readonly FakeClock clock = new FakeClock();

    public ClaudeCliProviderTests()
    {
      ClaudeCli.ResetProbeCache();
      ClaudeCliProvider.Reset();
    }

    public void Dispose()
    {
      ClaudeCli.ResetProbeCache();
      ClaudeCliProvider.Reset();
      ClaudeCliProvider.RunnerOverride = null;
      ClaudeCliProvider.HelpOverride = null;
      ClaudeCliProvider.ExecutableOverride = null;
    }

    private static JsonElement Schema => AiGroupingPrompt.Schema;

    private RetryPolicy Retry(int maxRetries = 2) => new RetryPolicy
    {
      MaxRetries = maxRetries,
      BaseDelay = TimeSpan.FromMilliseconds(10),
      MaxBackoff = TimeSpan.FromSeconds(5),
      MaxRetryWait = TimeSpan.FromSeconds(120),
      Jitter = TimeSpan.Zero,
      Delay = (d, ct) => { delays.Add(d); return clock.Sleep(d, ct); },
      Tracker = new RateLimitTracker(() => clock.Now),
    };

    /// <summary>A runner that plays <paramref name="results"/> in order, repeating the last one.</summary>
    private ClaudeCliProvider Provider(RetryPolicy retry, params CliProcessResult[] results)
    {
      int n = 0;
      return new ClaudeCliProvider("terminal-claude-sonnet-5", retry, Exe,
        (request, ct) =>
        {
          requests.Add(request);
          CliProcessResult r = results[Math.Min(n, results.Length - 1)];
          n++;
          return Task.FromResult(r);
        },
        _ => Help);
    }

    private static CliProcessResult Ok(string structured = "{\"snippets\":[{\"first\":0,\"last\":1}]}",
      int input = 1000, int output = 30) => new CliProcessResult
      {
        ExitCode = 0,
        Stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\","
          + "\"structured_output\":" + structured + ","
          + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}",
      };

    private static CliProcessResult Error(int? status, string message, int exitCode = 1, string subtype = "error") =>
      new CliProcessResult
      {
        ExitCode = exitCode,
        Stdout = "{\"is_error\":true,\"subtype\":" + JsonSerializer.Serialize(subtype)
          + ",\"result\":" + JsonSerializer.Serialize(message)
          + (status.HasValue ? ",\"api_error_status\":" + status.Value : "") + "}",
      };

    // ── the happy path ────────────────────────────────────────────────

    [Fact]
    public async Task Success_ReturnsTheStructuredOutputAndItsTokens()
    {
      ClaudeCliProvider p = Provider(Retry(), Ok());
      ChatCompletion done = await p.CompleteJsonAsync("system", "user", Schema, CancellationToken.None);

      Assert.Equal("{\"snippets\":[{\"first\":0,\"last\":1}]}", done.Text);
      Assert.Equal(1000, done.InputTokens);
      Assert.Equal(30, done.OutputTokens);
      Assert.Equal("claude-sonnet-5", done.Model);
      Assert.Single(requests);
      Assert.Empty(delays);
    }

    [Fact]
    public async Task Success_WithoutStructuredOutput_TakesTheLastJsonObjectOfTheText()
    {
      var result = new CliProcessResult
      {
        ExitCode = 0,
        Stdout = "{\"is_error\":false,\"result\":"
          + JsonSerializer.Serialize("{\"snippets\":[1]}\nWait, let me reconsider.\n{\"snippets\":[2]}") + "}",
      };
      ChatCompletion done = await Provider(Retry(), result).CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal("{\"snippets\":[2]}", done.Text);
    }

    // ── what the process is told ──────────────────────────────────────

    [Fact]
    public async Task TheRequest_HidesTheApiKeyAndRunsInItsOwnDirectory()
    {
      await Provider(Retry(), Ok()).CompleteJsonAsync("be brief", "line 1", Schema, CancellationToken.None);

      CliRequest r = requests.Single();
      Assert.Equal(Exe, r.Executable);
      Assert.Equal("line 1", r.Prompt);
      Assert.Equal(ClaudeCli.WorkDir, r.WorkingDirectory);

      // An API key would make the CLI bill the API instead of the subscription.
      Assert.True(r.Environment.ContainsKey("ANTHROPIC_API_KEY"));
      Assert.Null(r.Environment["ANTHROPIC_API_KEY"]);
      Assert.Equal("0", r.Environment["MAX_THINKING_TOKENS"]);
      Assert.Equal("1", r.Environment["DISABLE_TELEMETRY"]);
      Assert.Equal("1", r.Environment["DISABLE_ERROR_REPORTING"]);
      Assert.Equal("1", r.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);

      Assert.Contains("--safe-mode", r.Arguments);
      Assert.Equal("claude-sonnet-5", r.Arguments[r.Arguments.ToList().IndexOf("--model") + 1]);
      // --bare would make the CLI read ANTHROPIC_API_KEY only and never the subscription's OAuth.
      Assert.DoesNotContain("--bare", r.Arguments);
    }

    [Fact]
    public async Task TheRequest_CarriesTheRequestTimeout()
    {
      RetryPolicy retry = Retry();
      retry.RequestTimeout = TimeSpan.FromSeconds(42);
      await Provider(retry, Ok()).CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(TimeSpan.FromSeconds(42), requests.Single().Timeout);
    }

    [Fact]
    public async Task LongSystemPrompt_MovesIntoTheStdinPrompt()
    {
      string system = new string('x', ClaudeCli.MaxInlineSystemPrompt + 1);
      await Provider(Retry(), Ok()).CompleteJsonAsync(system, "the lines", Schema, CancellationToken.None);

      CliRequest r = requests.Single();
      Assert.DoesNotContain("--system-prompt", r.Arguments); // this build has no --system-prompt-file
      Assert.StartsWith(ClaudeCli.SystemPromptHeading, r.Prompt);
      Assert.EndsWith("the lines", r.Prompt);
    }

    // ── retrying ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpstreamRateLimit_IsRetriedAndHoldsTheModelBack()
    {
      RetryPolicy retry = Retry();
      var results = new[] { Error(429, "rate limited"), Ok() };
      int n = 0;
      var p = new ClaudeCliProvider("terminal-claude-sonnet-5", retry, Exe,
        (request, ct) => { requests.Add(request); return Task.FromResult(results[Math.Min(n++, 1)]); }, _ => Help);

      ChatCompletion done = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(2, requests.Count);
      Assert.Single(delays);
      // The cooldown is shared per claude-cli:<model>, so every other chunk waits it out too.
      Assert.Equal(TimeSpan.Zero, retry.Tracker.WaitTime(RateLimitTracker.KeyFor("claude-cli", "claude-sonnet-5")));
      Assert.NotNull(done);
    }

    [Fact]
    public async Task Timeout_IsRetried()
    {
      var timedOut = new CliProcessResult { ExitCode = null, TimedOut = true };
      int n = 0;
      var results = new[] { timedOut, Ok() };
      var p = new ClaudeCliProvider("terminal-claude-sonnet-5", Retry(), Exe,
        (request, ct) => { requests.Add(request); return Task.FromResult(results[Math.Min(n++, 1)]); }, _ => Help);

      await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);
      Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task CrashWithoutJson_IsRetriedUntilTheRetriesRunOut()
    {
      var crash = new CliProcessResult { ExitCode = 134, Stdout = "", Stderr = "Aborted" };
      ClaudeCliProvider p = Provider(Retry(maxRetries: 3), crash);

      ProviderException ex = await Assert.ThrowsAsync<ProviderException>(
        () => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Equal(4, requests.Count); // the first send plus three retries
      Assert.Contains("gave up after 4 attempts", ex.Message);
    }

    [Fact]
    public async Task TerminalFailure_IsNotRetried()
    {
      ClaudeCliProvider p = Provider(Retry(), Error(404, "Invalid model name: claude-nope"));

      ProviderException ex = await Assert.ThrowsAsync<ProviderException>(
        () => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Single(requests);
      Assert.Contains("Invalid model name", ex.Message);
    }

    [Fact]
    public async Task MaxStructuredOutputRetries_IsNotRetried()
    {
      // The CLI already retried the schema itself; another process would fail the same way.
      ClaudeCliProvider p = Provider(Retry(), Error(null, "schema never validated", subtype: ClaudeCli.StructuredOutputRetriesSubtype));

      await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Single(requests);
    }

    // ── the subscription's usage limit ────────────────────────────────

    [Fact]
    public async Task UsageLimit_FailsThisRequestAndEveryLaterOneWithoutRunningAnything()
    {
      ClaudeCliProvider p = Provider(Retry(), Error(null, "You've hit your 5-hour limit. It will reset at 3pm."));

      ProviderException first = await Assert.ThrowsAsync<ProviderException>(
        () => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Contains("usage limit was reached", first.Message);
      Assert.Single(requests);

      // Every other chunk would hit the same limit: fail at once so they fall back to the rules.
      ProviderException second = await Assert.ThrowsAsync<ProviderException>(
        () => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Contains("usage limit was reached", second.Message);
      Assert.Single(requests);
      Assert.NotNull(ClaudeCliProvider.UsageLimit);
    }

    [Fact]
    public async Task Reset_ClearsTheUsageLimit()
    {
      ClaudeCliProvider p = Provider(Retry(), Error(null, "usage limit reached"));
      await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      ClaudeCliProvider.Reset();
      Assert.Null(ClaudeCliProvider.UsageLimit);
    }

    // ── no executable, cancellation, the process cap ──────────────────

    [Fact]
    public async Task NoExecutable_SaysWhereToSetOne()
    {
      var p = new ClaudeCliProvider("terminal-claude-sonnet-5", Retry(), "",
        (request, ct) => { requests.Add(request); return Task.FromResult(Ok()); }, _ => Help);

      ProviderException ex = await Assert.ThrowsAsync<ProviderException>(
        () => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Contains("Claude CLI Path", ex.Message);
      Assert.Empty(requests);
    }

    [Fact]
    public async Task CancelledToken_RunsNothing()
    {
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      ClaudeCliProvider p = Provider(Retry(), Ok());

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => p.CompleteJsonAsync("s", "u", Schema, cts.Token));
      Assert.Empty(requests);
    }

    [Fact]
    public async Task CancelDuringARun_Propagates()
    {
      using var cts = new CancellationTokenSource();
      var p = new ClaudeCliProvider("terminal-claude-sonnet-5", Retry(), Exe,
        (request, ct) =>
        {
          requests.Add(request);
          cts.Cancel();
          ct.ThrowIfCancellationRequested();
          return Task.FromResult(Ok());
        }, _ => Help);

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.CompleteJsonAsync("s", "u", Schema, cts.Token));
      Assert.Single(requests);
    }

    [Fact]
    public async Task ProcessGate_NeverLetsMoreThanTheCapRunAtOnce()
    {
      ClaudeCliProvider.Reset();
      int cap = 2;
      int savedCap = ConstantSettings.ClaudeCliMaxConcurrentProcesses;
      ConstantSettings.ClaudeCliMaxConcurrentProcesses = cap;

      int running = 0, peak = 0;
      var block = new SemaphoreSlim(0);
      var p = new ClaudeCliProvider("terminal-claude-sonnet-5", Retry(), Exe,
        async (request, ct) =>
        {
          int now = Interlocked.Increment(ref running);
          lock (requests) peak = Math.Max(peak, now);
          await block.WaitAsync(ct);
          Interlocked.Decrement(ref running);
          return Ok();
        }, _ => Help);

      try
      {
        Task[] calls = Enumerable.Range(0, 6)
          .Select(_ => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None))
          .ToArray();

        // Let them all through, one at a time.
        for (int i = 0; i < 6; i++)
        {
          await Task.Delay(5);
          block.Release();
        }
        await Task.WhenAll(calls);
      }
      finally
      {
        ConstantSettings.ClaudeCliMaxConcurrentProcesses = savedCap;
      }

      Assert.True(peak <= cap, "peak was " + peak);
    }

    [Fact]
    public void ProcessGate_IsReplacedWhenTheCapChanges()
    {
      SemaphoreSlim four = ClaudeCliProvider.ProcessGate(4);
      Assert.Same(four, ClaudeCliProvider.ProcessGate(4));
      SemaphoreSlim eight = ClaudeCliProvider.ProcessGate(8);
      Assert.NotSame(four, eight);
      Assert.Equal(8, eight.CurrentCount);
    }
  }
}
