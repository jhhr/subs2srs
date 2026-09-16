using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>Everything one <c>claude</c> process needs; what a test's fake runner gets to inspect.</summary>
  public sealed class CliRequest
  {
    public string Executable { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    /// <summary>Goes to stdin, which is then closed.</summary>
    public string Prompt { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    /// <summary>Changes to the inherited environment; a null value means "remove this variable".</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan Timeout { get; init; }
  }


  /// <summary>What one finished <c>claude</c> process left behind.</summary>
  public sealed class CliProcessResult
  {
    /// <summary>Null when the process was killed before it could report one.</summary>
    public int? ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public bool TimedOut { get; init; }
  }


  /// <summary>
  /// A model named <c>terminal-claude-…</c> answered by the <c>claude</c> command line on the user's
  /// Claude subscription instead of the Anthropic Messages API (plan
  /// <c>plans/claude-cli-plan.md</c>). One request is one process: the system prompt as
  /// <c>--system-prompt</c>, the response schema as <c>--json-schema</c>, the user prompt on stdin
  /// and the JSON result on stdout. Chunking, prompt, answer repair, cache, preview and eval are
  /// unchanged — only the transport differs, and switching back is one edit of the Model field.
  /// </summary>
  /// <remarks>
  /// Not an <see cref="HttpChatProvider"/>: there is no request to build, no response header to read
  /// and no option to degrade. It shares the part that matters, <see cref="AiRetryLoop"/>, so a
  /// rate-limit rejection paces every chunk of a run the same way it does on the API path.
  /// </remarks>
  public sealed class ClaudeCliProvider : IChatProvider
  {
    public string Name => ClaudeCli.ProviderName;
    public string Model { get; }
    /// <summary>The resolved executable, null when none was found (every request then fails with a hint).</summary>
    public string? Executable { get; }

    private readonly RetryPolicy retry;
    private readonly Func<CliRequest, CancellationToken, Task<CliProcessResult>> runner;
    private readonly Func<string, string?> help;

    /// <summary>Test hook: the process runner every provider built without an explicit one uses.</summary>
    public static Func<CliRequest, CancellationToken, Task<CliProcessResult>>? RunnerOverride { get; set; }
    /// <summary>Test hook: what <c>claude --help</c> prints.</summary>
    public static Func<string, string?>? HelpOverride { get; set; }
    /// <summary>Test hook: the executable to use instead of looking for one.</summary>
    public static string? ExecutableOverride { get; set; }

    public ClaudeCliProvider(string model, RetryPolicy? retry = null, string? executable = null,
      Func<CliRequest, CancellationToken, Task<CliProcessResult>>? runner = null, Func<string, string?>? help = null)
    {
      Model = model ?? throw new ArgumentNullException(nameof(model));
      this.retry = retry ?? new RetryPolicy();
      Executable = executable ?? ExecutableOverride ?? ClaudeCli.Find(ConstantSettings.ClaudeCliPath);
      this.runner = runner ?? RunnerOverride ?? RunProcessAsync;
      this.help = help ?? HelpOverride ?? ReadHelp;
    }

    /// <summary>What to tell the user when no executable could be found.</summary>
    public const string NoCliMessage =
      "No claude command line found. Install Claude Code and sign in to your Claude subscription, "
      + "or set Claude CLI Path in Preferences (AI).";

    // ── The subscription's usage limit ───────────────────────────────────

    private static readonly object limitLock = new object();
    private static string? usageLimit;

    /// <summary>
    /// The CLI's own words when the subscription's usage limit was reached, else null. It is
    /// terminal for the whole run: every other chunk would hit the same limit, so the rest fail at
    /// once and fall back to the rules instead of each waiting out a long cooldown first.
    /// </summary>
    public static string? UsageLimit
    {
      get { lock (limitLock) return usageLimit; }
    }

    private static void NoteUsageLimit(string message)
    {
      lock (limitLock) usageLimit ??= string.IsNullOrWhiteSpace(message) ? "usage limit reached" : message.Trim();
    }

    /// <summary>Forget the usage limit and the process cap (a new run, and tests).</summary>
    public static void Reset()
    {
      lock (limitLock) usageLimit = null;
      lock (semaphoreLock) { semaphore = null; semaphoreSize = 0; }
    }

    // ── The process cap ──────────────────────────────────────────────────

    private static readonly object semaphoreLock = new object();
    private static SemaphoreSlim? semaphore;
    private static int semaphoreSize;

    /// <summary>
    /// The process-wide cap on running <c>claude</c> processes, sized from the preference and on top
    /// of <see cref="AiBulkRunner"/>'s own cap. Resized only by replacing it, so a preference change
    /// takes effect for requests that start after it while the ones holding the old one finish.
    /// </summary>
    public static SemaphoreSlim ProcessGate(int? requested = null)
    {
      int size = Math.Max(1, requested ?? ConstantSettings.ClaudeCliMaxConcurrentProcesses);
      lock (semaphoreLock)
      {
        if (semaphore == null || size != semaphoreSize)
        {
          semaphore = new SemaphoreSlim(size, size);
          semaphoreSize = size;
        }
        return semaphore;
      }
    }

    // ── One completion ───────────────────────────────────────────────────

    public async Task<ChatCompletion> CompleteJsonAsync(string system, string user, JsonElement schema, CancellationToken ct)
    {
      string? limit = UsageLimit;
      if (limit != null) throw new ProviderException(Name, null, LimitMessage(limit));
      if (string.IsNullOrWhiteSpace(Executable)) throw new ProviderException(Name, null, NoCliMessage);

      ClaudeCliFeatures features = ClaudeCli.Probe(Executable, help);
      string? promptFile = null;
      try
      {
        if (ClaudeCli.WantsSystemPromptFile(features, system))
        {
          promptFile = Path.Combine(Path.GetTempPath(), "subs2srs-system-" + Guid.NewGuid().ToString("N") + ".md");
          File.WriteAllText(promptFile, system, new UTF8Encoding(false));
        }
        List<string> args = ClaudeCli.BuildArguments(features, Model, system, schema, promptFile, out string prefix);
        string prompt = prefix + (user ?? "");
        return await AiRetryLoop.RunAsync(Name, ClaudeCli.CliModel(Model), retry,
          (sentAt, token) => RunOnceAsync(args, prompt, sentAt, token), ct).ConfigureAwait(false);
      }
      finally
      {
        if (promptFile != null) { try { File.Delete(promptFile); } catch (IOException) { } }
      }
    }

    private async Task<AiAttempt<ChatCompletion>> RunOnceAsync(IReadOnlyList<string> args, string prompt,
      TimeSpan sentAt, CancellationToken ct)
    {
      // Another chunk may have hit the limit while this one waited for the gate.
      string? limit = UsageLimit;
      if (limit != null) throw new ProviderException(Name, null, LimitMessage(limit));

      var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
      ClaudeCli.ApplyEnvironment(environment);
      var request = new CliRequest
      {
        Executable = Executable!,
        Arguments = args,
        Prompt = prompt,
        WorkingDirectory = ClaudeCli.WorkDir,
        Environment = environment,
        Timeout = retry.RequestTimeout,
      };

      SemaphoreSlim gate = ProcessGate();
      await gate.WaitAsync(ct).ConfigureAwait(false);
      CliProcessResult finished;
      try
      {
        finished = await runner(request, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
      {
        throw new ProviderException(Name, null, $"Could not start \"{Executable}\": {ex.Message}", ex);
      }
      finally
      {
        gate.Release();
      }

      CliOutcome outcome = finished.TimedOut
        ? new CliOutcome
        {
          Action = CliAction.Retry,
          Text = FormattableString.Invariant($"timed out after {retry.RequestTimeout.TotalSeconds:0} s"),
        }
        : ClaudeCli.Classify(finished.ExitCode, finished.Stdout, finished.Stderr);

      switch (outcome.Action)
      {
        case CliAction.Ok:
          retry.Tracker.NoteSuccess(RateLimitTracker.KeyFor(Name, ClaudeCli.CliModel(Model)), sentAt);
          return AiAttempt<ChatCompletion>.Success(Completion(outcome));

        case CliAction.Exhausted:
          NoteUsageLimit(outcome.Text);
          Logger.Instance.info($"{Name}: {LimitMessage(outcome.Text)}");
          throw new ProviderException(Name, outcome.Status, LimitMessage(outcome.Text));

        case CliAction.Fail:
          // The whole output, so a failure this classifier does not know yet can be taught to it.
          Logger.Instance.info(FormattableString.Invariant(
            $"{Name}: {Model} failed, exit {finished.ExitCode}; stdout: {finished.Stdout}; stderr: {finished.Stderr}"));
          throw new ProviderException(Name, outcome.Status, outcome.Text);

        default:
          bool rateLimited = outcome.Status.HasValue && RetryPolicy.IsRateLimitStatus(outcome.Status.Value);
          return AiAttempt<ChatCompletion>.Retryable(outcome.Status, outcome.Text, null, rateLimited);
      }
    }

    private static string LimitMessage(string message) =>
      "The Claude subscription usage limit was reached: " + (message ?? "").Trim();

    /// <summary>
    /// The answer: the object the CLI validated against the schema when it managed to, else the last
    /// complete JSON object in the text (the models sometimes answer, reconsider and answer again,
    /// and the parser's first-brace-to-last-brace rule would splice the two together).
    /// </summary>
    private ChatCompletion Completion(CliOutcome outcome)
    {
      string text = outcome.Json ?? ClaudeCli.LastJsonObject(outcome.Text) ?? outcome.Text;
      return new ChatCompletion
      {
        Text = text,
        InputTokens = outcome.InputTokens,
        OutputTokens = outcome.OutputTokens,
        Model = outcome.Model.Length > 0 ? outcome.Model : ClaudeCli.CliModel(Model),
      };
    }

    // ── Actually running a process ───────────────────────────────────────

    /// <summary>
    /// The real runner, so a live test can wrap it and record what the CLI actually printed
    /// (<see cref="RunnerOverride"/> otherwise replaces it entirely).
    /// </summary>
    public static Func<CliRequest, CancellationToken, Task<CliProcessResult>> DefaultRunner => RunProcessAsync;

    /// <summary>
    /// Run one request to the end. Cancelling or timing out kills the whole tree: <c>claude</c>
    /// starts children (git, cmd, conhost) that would outlive a plain kill.
    /// </summary>
    private static async Task<CliProcessResult> RunProcessAsync(CliRequest request, CancellationToken ct)
    {
      Directory.CreateDirectory(request.WorkingDirectory);
      var psi = new ProcessStartInfo
      {
        FileName = request.Executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = request.WorkingDirectory,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
      };
      foreach (string arg in request.Arguments) psi.ArgumentList.Add(arg);
      foreach (KeyValuePair<string, string?> entry in request.Environment)
      {
        if (entry.Value == null) psi.Environment.Remove(entry.Key);
        else psi.Environment[entry.Key] = entry.Value;
      }

      using var process = new Process { StartInfo = psi };
      process.Start();
      Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
      Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
      try
      {
        await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), ct).ConfigureAwait(false);
      }
      catch (IOException) { /* it exited before reading the prompt; the output says why */ }
      finally
      {
        try { process.StandardInput.Close(); } catch (IOException) { }
      }

      bool timedOut = false;
      using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
      {
        timeoutCts.CancelAfter(request.Timeout);
        try
        {
          await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          Kill(process);
          if (ct.IsCancellationRequested) throw;
          timedOut = true;
          try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (SystemException) { }
        }
      }

      string outText = await Collect(stdout).ConfigureAwait(false);
      string errText = await Collect(stderr).ConfigureAwait(false);
      int? exitCode = null;
      try { if (process.HasExited) exitCode = process.ExitCode; } catch (InvalidOperationException) { }
      return new CliProcessResult { ExitCode = exitCode, Stdout = outText, Stderr = errText, TimedOut = timedOut };
    }

    private static async Task<string> Collect(Task<string> reader)
    {
      try { return await reader.ConfigureAwait(false); }
      catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) { return ""; }
    }

    private static void Kill(Process process)
    {
      try { process.Kill(entireProcessTree: true); }
      catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception) { }
    }

    /// <summary>One short <c>claude --help</c> run, for the flag probe. Null when it cannot be run.</summary>
    private static string? ReadHelp(string exe)
    {
      var psi = new ProcessStartInfo
      {
        FileName = exe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
      };
      psi.ArgumentList.Add("--help");
      try
      {
        using var process = new Process { StartInfo = psi };
        process.Start();
        string text = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) Kill(process);
        return text;
      }
      catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
      {
        Logger.Instance.info($"{ClaudeCli.ProviderName}: could not run \"{exe} --help\": {ex.Message}");
        return null;
      }
    }
  }
}
