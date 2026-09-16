using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace subs2srs
{
  /// <summary>What to do with a finished <c>claude -p</c> process (plan §4).</summary>
  public enum CliAction
  {
    /// <summary>An answer came back.</summary>
    Ok,
    /// <summary>This process's problem (crash, kill, upstream 429/5xx): worth another run.</summary>
    Retry,
    /// <summary>The subscription's usage limit: nothing more goes through until it resets.</summary>
    Exhausted,
    /// <summary>A failure another run would repeat (unknown model, bad schema, refused request).</summary>
    Fail,
  }


  /// <summary>
  /// The verdict on one finished CLI process: what to do, the answer when there is one, and what
  /// the CLI said. Every field the classifier reads is optional, because the JSON the CLI prints
  /// is only half documented (plan §9); an unrecognised failure counts as retryable.
  /// </summary>
  public sealed class CliOutcome
  {
    public CliAction Action { get; init; }
    /// <summary>The schema object the CLI validated, as JSON text; null when it parsed none.</summary>
    public string? Json { get; init; }
    /// <summary>The model's text answer on success, otherwise what went wrong.</summary>
    public string Text { get; init; } = "";
    /// <summary>Upstream HTTP status the CLI reported (<c>api_error_status</c>), when it did.</summary>
    public int? Status { get; init; }
    /// <summary>The result message's <c>subtype</c>, e.g. <c>error_max_structured_output_retries</c>.</summary>
    public string Subtype { get; init; } = "";
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    /// <summary>Model the CLI reported, when the usage breakdown names one.</summary>
    public string Model { get; init; } = "";
  }


  /// <summary>
  /// Which optional flags the installed <c>claude</c> build has, read once from <c>claude --help</c>.
  /// The online reference and the installed build disagree in both directions (plan §3.1) and the
  /// CLI rejects unknown flags, so nothing is passed that the help did not mention.
  /// </summary>
  public sealed class ClaudeCliFeatures
  {
    public bool Print { get; init; } = true;
    public bool Model { get; init; } = true;
    public bool OutputFormat { get; init; } = true;
    public bool JsonSchema { get; init; } = true;
    public bool SystemPrompt { get; init; } = true;
    public bool SystemPromptFile { get; init; }
    public bool Tools { get; init; }
    public bool NoSessionPersistence { get; init; }
    public bool SafeMode { get; init; }
    public bool Effort { get; init; }

    /// <summary>
    /// What to assume when <c>claude --help</c> could not be read: the flags the feature cannot work
    /// without, and none of the hardening extras (passing one that does not exist fails the run).
    /// </summary>
    public static ClaudeCliFeatures Assumed { get; } = new ClaudeCliFeatures();

    /// <summary>Parse one <c>claude --help</c> text. A flag counts as present only as a whole word.</summary>
    public static ClaudeCliFeatures FromHelp(string help)
    {
      string text = help ?? "";
      bool has(string flag) => Regex.IsMatch(text, @"(?<![A-Za-z0-9_-])" + Regex.Escape(flag) + @"(?![A-Za-z0-9_-])");
      return new ClaudeCliFeatures
      {
        Print = has("--print") || has("-p"),
        Model = has("--model"),
        OutputFormat = has("--output-format"),
        JsonSchema = has("--json-schema"),
        SystemPrompt = has("--system-prompt"),
        SystemPromptFile = has("--system-prompt-file"),
        Tools = has("--tools"),
        NoSessionPersistence = has("--no-session-persistence"),
        SafeMode = has("--safe-mode"),
        Effort = has("--effort"),
      };
    }
  }


  /// <summary>
  /// Pure helpers for running Claude models through the <c>claude</c> command line instead of the
  /// Anthropic Messages API (plan <c>plans/claude-cli-plan.md</c>, a port of the Anki addon's
  /// <c>terminal_client.py</c>). A model configured as <c>terminal-claude-sonnet-5</c> is billed by
  /// the user's Claude subscription; everything else about AI grouping is unchanged.
  /// <see cref="ClaudeCliProvider"/> does the running, this does the deciding.
  /// </summary>
  public static class ClaudeCli
  {
    /// <summary>Model-name prefix that sends a request through the CLI ("terminal-claude-sonnet-5").</summary>
    public const string TerminalPrefix = "terminal-";
    /// <summary>Provider name in log lines, cooldown keys and <see cref="ProviderException"/> text.</summary>
    public const string ProviderName = "claude-cli";
    /// <summary>Windows caps a whole command line at 32,767 characters; a longer system prompt moves into the prompt.</summary>
    public const int MaxInlineSystemPrompt = 8000;
    /// <summary>Heading the overflowed system prompt gets when it is prepended to the stdin prompt.</summary>
    public const string SystemPromptHeading = "# Instructions";

    /// <summary>
    /// Wording of the subscription usage limit, which no retry within a run can clear. The CLI
    /// reports it as error text, not as a field (plan §9), so this is the detection.
    /// </summary>
    private static readonly Regex UsageLimit =
      new Regex(@"hit your .*limit|usage limit|limit reached|limit will reset", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The CLI has already retried the schema internally; another process would fail the same way.</summary>
    public const string StructuredOutputRetriesSubtype = "error_max_structured_output_retries";

    /// <summary>Model aliases the CLI accepts beside a full Claude ID.</summary>
    private static readonly string[] Aliases = { "opus", "sonnet", "haiku", "fable" };

    private static readonly ConcurrentDictionary<string, ClaudeCliFeatures> probed =
      new ConcurrentDictionary<string, ClaudeCliFeatures>(StringComparer.OrdinalIgnoreCase);

    public static bool IsTerminalModel(string? model) =>
      !string.IsNullOrWhiteSpace(model) && model.Trim().StartsWith(TerminalPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The model the CLI is told to use: "terminal-claude-sonnet-5" → "claude-sonnet-5".</summary>
    public static string CliModel(string? model)
    {
      string m = (model ?? "").Trim();
      return IsTerminalModel(m) ? m.Substring(TerminalPrefix.Length) : m;
    }

    /// <summary>The CLI serves Claude only: after the prefix the ID must be a Claude ID or an alias.</summary>
    public static bool IsSupportedModel(string? model)
    {
      if (!IsTerminalModel(model)) return false;
      string m = CliModel(model).ToLowerInvariant();
      if (m.Length == 0) return false;
      if (m.StartsWith("claude", StringComparison.Ordinal)) return true;
      foreach (string alias in Aliases)
        if (m == alias) return true;
      return false;
    }

    /// <summary>
    /// The working directory the process runs in: an empty folder, so no <c>CLAUDE.md</c>, settings
    /// or git state of whatever directory subs2srs was started from leaks into the prompt.
    /// </summary>
    public static string WorkDir => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "subs2srs", "claude-cwd");

    /// <summary>
    /// Environment variables the child process gets on top of the inherited ones, and the ones it
    /// must not see. An API key would make the CLI bill the Anthropic API instead of the
    /// subscription, which is the whole point of <c>terminal-</c>; thinking doubles the wall clock
    /// for no gain here; the telemetry channels are noise for dozens of short automated runs.
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> environment)
    {
      if (environment == null) return;
      foreach (string hidden in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" })
        environment[hidden] = null;
      environment["MAX_THINKING_TOKENS"] = "0";
      environment["DISABLE_TELEMETRY"] = "1";
      environment["DISABLE_ERROR_REPORTING"] = "1";
      environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }

    // ── Finding the executable ───────────────────────────────────────────

    /// <summary>
    /// The claude executable: the configured path if set, else the one on PATH. npm installs
    /// <c>claude</c> as .cmd/.ps1 shims; running one goes through a shell that mangles the schema
    /// argument's quoting and sits between us and the process we would kill, so the native binary
    /// they launch (next to them under <c>node_modules</c>) is used instead. Null when there is none.
    /// </summary>
    public static string? Find(string? configuredPath, Func<string, string?>? which = null)
    {
      string configured = (configuredPath ?? "").Trim();
      if (configured.Length > 0) return configured;

      string? found = (which ?? OnPath)("claude");
      if (string.IsNullOrWhiteSpace(found)) return null;

      string ext = Path.GetExtension(found).ToLowerInvariant();
      if (ext == ".exe") return found;

      string? dir = Path.GetDirectoryName(found);
      if (!string.IsNullOrEmpty(dir))
      {
        string bin = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin");
        foreach (string name in new[] { "claude.exe", "claude" })
        {
          string native = Path.Combine(bin, name);
          if (File.Exists(native)) return native;
        }
      }
      // A shim with no native binary beside it is worse than nothing.
      return ext == ".cmd" || ext == ".ps1" || ext == ".bat" ? null : found;
    }

    /// <summary>First match for a bare name on PATH, honouring PATHEXT on Windows. Null when there is none.</summary>
    public static string? OnPath(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return null;
      string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
      var extensions = new List<string> { "" };
      if (OperatingSystem.IsWindows())
      {
        string pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (string e in pathext.Split(';'))
          if (!string.IsNullOrWhiteSpace(e)) extensions.Add(e.Trim());
      }
      foreach (string dir in dirs)
      {
        if (string.IsNullOrWhiteSpace(dir)) continue;
        foreach (string e in extensions)
        {
          string candidate;
          try { candidate = Path.Combine(dir.Trim(), name + e); }
          catch (ArgumentException) { continue; } // an invalid PATH entry
          if (File.Exists(candidate)) return candidate;
        }
      }
      return null;
    }

    // ── Probing the flags ────────────────────────────────────────────────

    /// <summary>
    /// Which optional flags this build has, from one <c>claude --help</c> run per executable path,
    /// cached for the life of the process. <paramref name="runHelp"/> returns the help text, or null
    /// when the executable could not be run (then nothing optional is passed).
    /// </summary>
    public static ClaudeCliFeatures Probe(string exe, Func<string, string?> runHelp)
    {
      if (string.IsNullOrWhiteSpace(exe)) return ClaudeCliFeatures.Assumed;
      return probed.GetOrAdd(exe, path =>
      {
        string? help = null;
        try { help = runHelp(path); }
        catch (Exception ex) { Logger.Instance.info($"{ProviderName}: could not read \"{path} --help\": {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(help)) return ClaudeCliFeatures.Assumed;
        return ClaudeCliFeatures.FromHelp(help);
      });
    }

    /// <summary>Forget the cached probe (tests, and a changed Claude CLI Path preference).</summary>
    public static void ResetProbeCache() => probed.Clear();

    // ── Building the command ─────────────────────────────────────────────

    /// <summary>
    /// True when the system prompt is too long for the command line and this build can take it in a
    /// file; the caller writes the file and passes it to <see cref="BuildArguments"/>.
    /// </summary>
    public static bool WantsSystemPromptFile(ClaudeCliFeatures features, string? system) =>
      features != null && features.SystemPromptFile && (system ?? "").Length > MaxInlineSystemPrompt;

    /// <summary>
    /// The argument list for one request, for <c>ProcessStartInfo.ArgumentList</c> (never a joined
    /// string: no quoting bugs). The user prompt goes on stdin, prefixed with
    /// <paramref name="promptPrefix"/> when the system prompt did not fit on the command line and
    /// this build has no <c>--system-prompt-file</c>. <c>--bare</c> is never passed: it would make
    /// the CLI read only <c>ANTHROPIC_API_KEY</c> and bill the API.
    /// </summary>
    public static List<string> BuildArguments(ClaudeCliFeatures features, string model, string? system,
      JsonElement schema, string? systemPromptFile, out string promptPrefix)
    {
      features ??= ClaudeCliFeatures.Assumed;
      promptPrefix = "";
      var args = new List<string> { "-p", "--model", CliModel(model), "--output-format", "json" };
      if (features.Tools) { args.Add("--tools"); args.Add(""); }
      if (features.NoSessionPersistence) args.Add("--no-session-persistence");
      if (features.SafeMode) args.Add("--safe-mode");
      if (features.Effort) { args.Add("--effort"); args.Add("low"); }

      string prompt = system ?? "";
      if (!string.IsNullOrEmpty(systemPromptFile) && features.SystemPromptFile)
      {
        args.Add("--system-prompt-file");
        args.Add(systemPromptFile);
      }
      else if (prompt.Length > MaxInlineSystemPrompt || !features.SystemPrompt)
      {
        if (prompt.Length > 0) promptPrefix = SystemPromptHeading + "\n\n" + prompt + "\n\n";
      }
      else if (prompt.Length > 0)
      {
        args.Add("--system-prompt");
        args.Add(prompt);
      }

      if (schema.ValueKind == JsonValueKind.Object && features.JsonSchema)
      {
        args.Add("--json-schema");
        args.Add(JsonSerializer.Serialize(schema, SchemaJson));
      }
      return args;
    }

    private static readonly JsonSerializerOptions SchemaJson = new JsonSerializerOptions
    {
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      WriteIndented = false,
    };

    // ── Reading the answer ───────────────────────────────────────────────

    /// <summary>
    /// Decide what to do with a finished <c>claude -p --output-format json</c> process. Nothing in
    /// the printed object is required: a shape this does not recognise is a retryable failure and
    /// the caller logs the whole output so it can be taught here later.
    /// </summary>
    public static CliOutcome Classify(int? exitCode, string? stdout, string? stderr)
    {
      string outText = stdout ?? "";
      JsonElement body = default;
      JsonDocument? doc = null;
      try
      {
        doc = JsonDocument.Parse(outText);
        if (doc.RootElement.ValueKind == JsonValueKind.Object) body = doc.RootElement;
      }
      catch (JsonException) { }

      using (doc)
      {
        if (body.ValueKind != JsonValueKind.Object)
        {
          // A crash or a kill leaves no JSON: this process's problem, worth another try.
          string tail = Tail(string.IsNullOrWhiteSpace(stderr) ? outText : stderr);
          return new CliOutcome
          {
            Action = CliAction.Retry,
            Text = FormattableString.Invariant($"exit {(exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}, no JSON output: {tail}"),
          };
        }

        string text = StringOr(body, "result");
        string subtype = StringOr(body, "subtype");
        int? status = IntOrNull(body, "api_error_status");
        bool isError = body.TryGetProperty("is_error", out JsonElement err) && err.ValueKind == JsonValueKind.True;
        (int input, int output, string model) = ReadUsage(body);

        if (!isError && exitCode == 0)
        {
          string? json = null;
          if (body.TryGetProperty("structured_output", out JsonElement structured) && structured.ValueKind == JsonValueKind.Object)
            json = structured.GetRawText();
          return new CliOutcome
          {
            Action = CliAction.Ok,
            Json = json,
            Text = text,
            Subtype = subtype,
            InputTokens = input,
            OutputTokens = output,
            Model = model,
          };
        }

        CliAction action;
        if (UsageLimit.IsMatch(text)) action = CliAction.Exhausted;
        else if (subtype == StructuredOutputRetriesSubtype) action = CliAction.Fail;
        else if (status.HasValue && (RetryPolicy.IsRateLimitStatus(status.Value) || status.Value >= 500)) action = CliAction.Retry;
        else action = CliAction.Fail;

        return new CliOutcome
        {
          Action = action,
          Text = text.Length > 0 ? text : Tail(outText),
          Status = status,
          Subtype = subtype,
          InputTokens = input,
          OutputTokens = output,
          Model = model,
        };
      }
    }

    /// <summary>
    /// Token counts and the model out of a result object, as a real one looks (recorded 2026-09-16,
    /// <c>Fixtures/ai/claude-cli-ok.json</c>): nearly the whole prompt is served from the CLI's
    /// prompt cache, so <c>input_tokens</c> alone said 4 where the request really cost 3,376 — the
    /// cached and cache-writing tokens are input too and are counted with it. There is no top-level
    /// <c>model</c> field either; the ID is the key of the per-model usage breakdown, which is the
    /// canonical name an alias like <c>terminal-sonnet</c> resolved to.
    /// </summary>
    private static (int input, int output, string model) ReadUsage(JsonElement body)
    {
      string model = StringOr(body, "model");
      if (model.Length == 0 && body.TryGetProperty("modelUsage", out JsonElement byModel)
          && byModel.ValueKind == JsonValueKind.Object)
      {
        foreach (JsonProperty entry in byModel.EnumerateObject()) { model = entry.Name; break; }
      }
      if (!body.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        return (0, 0, model);
      int input = (IntOrNull(usage, "input_tokens") ?? 0)
        + (IntOrNull(usage, "cache_read_input_tokens") ?? 0)
        + (IntOrNull(usage, "cache_creation_input_tokens") ?? 0);
      return (input, IntOrNull(usage, "output_tokens") ?? 0, model);
    }

    private static string Tail(string text)
    {
      string t = (text ?? "").Trim();
      return t.Length > 500 ? t.Substring(t.Length - 500) : t;
    }

    private static string StringOr(JsonElement obj, string name) =>
      obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? IntOrNull(JsonElement obj, string name) =>
      obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : (int?)null;

    /// <summary>
    /// The last complete top-level JSON object in <paramref name="text"/>, null when there is none.
    /// Claude in the terminal sometimes answers, writes "Wait, let me reconsider…" and answers
    /// again; the later object is the one it settled on, and the first-brace-to-last-brace rule the
    /// API adapters use would splice the two together.
    /// </summary>
    public static string? LastJsonObject(string? text)
    {
      string s = text ?? "";
      string? found = null;
      int pos = 0;
      while (pos < s.Length)
      {
        int start = s.IndexOf('{', pos);
        if (start < 0) break;
        int end = MatchingBrace(s, start);
        if (end < 0) { pos = start + 1; continue; } // unbalanced from here; a later brace may still open one
        string candidate = s.Substring(start, end - start + 1);
        if (IsJsonObject(candidate))
        {
          found = candidate;
          pos = end + 1;
        }
        else pos = start + 1;
      }
      return found;
    }

    /// <summary>Index of the brace closing the one at <paramref name="start"/>, skipping strings; -1 when unbalanced.</summary>
    private static int MatchingBrace(string s, int start)
    {
      int depth = 0;
      bool inString = false, escaped = false;
      for (int i = start; i < s.Length; i++)
      {
        char c = s[i];
        if (inString)
        {
          if (escaped) escaped = false;
          else if (c == '\\') escaped = true;
          else if (c == '"') inString = false;
          continue;
        }
        if (c == '"') inString = true;
        else if (c == '{') depth++;
        else if (c == '}' && --depth == 0) return i;
      }
      return -1;
    }

    private static bool IsJsonObject(string candidate)
    {
      try
      {
        using JsonDocument doc = JsonDocument.Parse(candidate);
        return doc.RootElement.ValueKind == JsonValueKind.Object;
      }
      catch (JsonException) { return false; }
    }
  }
}
