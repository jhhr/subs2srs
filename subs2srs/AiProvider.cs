using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>One structured-JSON completion: the raw JSON text and what it cost.</summary>
  public sealed class ChatCompletion
  {
    public string Text { get; init; } = "";
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    /// <summary>Model reported by the provider (may differ from the requested alias).</summary>
    public string Model { get; init; } = "";
  }


  /// <summary>
  /// The only thing the app asks of a language model: one JSON completion that follows a schema.
  /// No chat history, no tools, no streaming (plan §A.3, decision B1).
  /// </summary>
  public interface IChatProvider
  {
    /// <summary>Provider name used for pacing, logging and cost tables: "anthropic", "openai", "gemini", "fake".</summary>
    string Name { get; }
    /// <summary>Model ID as configured.</summary>
    string Model { get; }
    Task<ChatCompletion> CompleteJsonAsync(string system, string user, JsonElement schema, CancellationToken ct);
  }


  /// <summary>
  /// A provider call failed for good: bad key, unknown model, out of retries, unparseable answer.
  /// <see cref="ProviderMessage"/> is the provider's own error text so the UI can show it.
  /// </summary>
  public class ProviderException : Exception
  {
    public int? Status { get; }
    public string ProviderMessage { get; }
    public string Provider { get; }

    public ProviderException(string provider, int? status, string providerMessage, Exception? inner = null, string? detail = null)
      : base(FormatMessage(provider, status, providerMessage, detail), inner)
    {
      Provider = provider;
      Status = status;
      ProviderMessage = providerMessage ?? "";
    }

    private static string FormatMessage(string provider, int? status, string message, string? detail)
    {
      string prefix = status.HasValue
        ? FormattableString.Invariant($"{provider} request failed (HTTP {status.Value})")
        : $"{provider} request failed";
      string text = string.IsNullOrWhiteSpace(message) ? prefix : prefix + ": " + message;
      return string.IsNullOrWhiteSpace(detail) ? text : text + " " + detail;
    }
  }


  /// <summary>
  /// Retry, backoff and timeout knobs of <see cref="HttpChatProvider"/> (defaults from
  /// <c>api_client.py</c> in the Python reference); tests shrink the delays and inject a tracker
  /// and a clock.
  /// </summary>
  public sealed class RetryPolicy
  {
    /// <summary>Retries after the first send, so the request goes out at most MaxRetries + 1 times.</summary>
    public int MaxRetries { get; set; } = 5;
    /// <summary>A provider hint above this gives up at once instead of blocking the run; also caps a proactive hold.</summary>
    public TimeSpan MaxRetryWait { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Per-request timeout; a timed-out request counts as a retryable failure of this request only.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Backoff without a hint: min(<see cref="MaxBackoff"/>, BaseDelay · 2^attempt) + U(0, <see cref="Jitter"/>).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan Jitter { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How to wait; tests replace it to record delays instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    /// <summary>The shared per-model cooldowns; tests give each test its own.</summary>
    public RateLimitTracker Tracker { get; set; } = RateLimitTracker.Shared;
    /// <summary>Wall clock for absolute reset headers; tests pin it.</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly Random rng = new Random();

    /// <summary>Exponential backoff with additive jitter for a zero-based attempt number.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
      double ms = Math.Min(MaxBackoff.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt)));
      double jitter;
      lock (rng) jitter = rng.NextDouble() * Jitter.TotalMilliseconds;
      return TimeSpan.FromMilliseconds(Math.Max(0, ms + jitter));
    }

    /// <summary>429 is "too many requests" everywhere; Anthropic's 529 is "overloaded", the same message about the service as a whole.</summary>
    public static bool IsRateLimitStatus(int status) => status == 429 || status == 529;

    /// <summary>Whether a transient server error is worth another attempt (Anthropic adds 529).</summary>
    public static bool IsServerError(int status) => status == 500 || status == 502 || status == 503 || status == 504;

    /// <summary>The knobs the user set in Preferences (AI).</summary>
    public static RetryPolicy FromPreferences() => new RetryPolicy
    {
      MaxRetries = Math.Max(0, ConstantSettings.AiMaxRetries),
      MaxRetryWait = TimeSpan.FromSeconds(Math.Max(1, ConstantSettings.AiMaxRetryWaitSeconds)),
      RequestTimeout = TimeSpan.FromSeconds(Math.Max(1, ConstantSettings.AiRequestTimeoutSeconds)),
    };
  }


  /// <summary>
  /// Shared plumbing of the three raw-HTTP adapters, a port of <c>post_with_retry</c> from the
  /// Python reference: one <see cref="HttpClient"/>, a request timeout, and a retry loop driven by
  /// what the provider answers rather than a guessed requests-per-minute ceiling. Each adapter
  /// classifies its own responses (<see cref="Classify"/>): a rate-limit rejection (429/529)
  /// installs a cooldown for the model in the shared <see cref="RateLimitTracker"/> that every
  /// request to that model waits out before sending; a success whose headers say a limit bucket
  /// is spent installs one proactively (<see cref="ProactiveHold"/>); timeouts, connection errors
  /// and 5xx back off per request only. A hint above <see cref="RetryPolicy.MaxRetryWait"/> gives
  /// up at once. Subclasses build the request body and pick the answer out of the response.
  /// </summary>
  public abstract class HttpChatProvider : IChatProvider
  {
    /// <summary>Connections per host of the shared client; the concurrency backstop of <see cref="AiBulkRunner"/> stays below it.</summary>
    public const int MaxConnectionsPerServer = 64;

    private static readonly Lazy<HttpClient> sharedClient = new Lazy<HttpClient>(() =>
      new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = MaxConnectionsPerServer })
      {
        Timeout = Timeout.InfiniteTimeSpan
      });

    protected static readonly JsonSerializerOptions BodyJson = new JsonSerializerOptions
    {
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      WriteIndented = false,
    };

    private readonly HttpClient client;
    private readonly RetryPolicy retry;

    public abstract string Name { get; }
    public string Model { get; }
    protected string ApiKey { get; }

    public bool HasApiKey => ApiKey != "";

    /// <summary>Last request body sent, for logging and request-shape tests. Never contains the key.</summary>
    public string? LastRequestBody { get; private set; }

    protected HttpChatProvider(string model, string apiKey, HttpClient? client = null, RetryPolicy? retry = null)
    {
      Model = model ?? throw new ArgumentNullException(nameof(model));
      ApiKey = apiKey ?? "";
      this.client = client ?? sharedClient.Value;
      this.retry = retry ?? new RetryPolicy();
    }

    /// <summary>Build the request. <paramref name="droppedOptions"/> lets a provider drop a feature after a 400 (see <see cref="Degrade"/>).</summary>
    protected abstract HttpRequestMessage BuildRequest(string system, string user, JsonElement schema, ISet<string> droppedOptions);

    /// <summary>Extract the completion from a 200 body.</summary>
    protected abstract ChatCompletion ParseResponse(JsonDocument body);

    /// <summary>Decide what to do with a non-success response: retry (with the provider's wait hint, if any) or fail for good.</summary>
    protected abstract ResponseVerdict Classify(int status, HttpResponseHeaders headers, string body, DateTimeOffset now);

    /// <summary>
    /// On a success, how long to hold the model back because its headers say a limit bucket is
    /// spent; null when there is headroom or the provider sends no such headers.
    /// </summary>
    protected virtual TimeSpan? ProactiveHold(HttpResponseHeaders headers, DateTimeOffset now) => null;

    /// <summary>
    /// Called on a terminal 4xx. Return the name of a request option to drop and retry
    /// without (e.g. an "effort" field an older model rejects), or null to fail.
    /// </summary>
    protected virtual string? Degrade(int status, string providerMessage, ISet<string> alreadyDropped) => null;

    /// <summary>Error text from a provider error body; all three put it under <c>error.message</c>.</summary>
    protected virtual string ExtractErrorMessage(string body)
    {
      if (string.IsNullOrWhiteSpace(body)) return "";
      try
      {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out JsonElement err))
        {
          if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
            return msg.GetString() ?? "";
          if (err.ValueKind == JsonValueKind.String)
            return err.GetString() ?? "";
        }
      }
      catch (JsonException) { }
      return body.Length > 500 ? body.Substring(0, 500) : body;
    }

    /// <summary>The body's <c>error</c> member when it is an object, else default (a proxy may send a bare string).</summary>
    protected static JsonElement ErrorObject(string body)
    {
      if (string.IsNullOrWhiteSpace(body)) return default;
      try
      {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
          return err.Clone();
      }
      catch (JsonException) { }
      return default;
    }

    protected HttpRequestMessage JsonPost(string url, object body)
    {
      string json = JsonSerializer.Serialize(body, BodyJson);
      LastRequestBody = json;
      var req = new HttpRequestMessage(HttpMethod.Post, url)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
      };
      return req;
    }

    public async Task<ChatCompletion> CompleteJsonAsync(string system, string user, JsonElement schema, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(ApiKey))
        throw new ProviderException(Name, null, $"No API key configured for {Name}. Set it in Preferences or the environment.");

      string key = RateLimitTracker.KeyFor(Name, Model);
      RateLimitTracker tracker = retry.Tracker;
      var dropped = new HashSet<string>(StringComparer.Ordinal);
      int attempt = 0; // failed sends so far
      while (true)
      {
        ct.ThrowIfCancellationRequested();

        // Another request may already have been rejected for this model: wait rather than spend a request on the same rejection.
        TimeSpan cooldown = tracker.WaitTime(key);
        if (cooldown > TimeSpan.Zero)
        {
          Logger.Instance.info(FormattableString.Invariant($"{Name}: waiting {cooldown.TotalSeconds:0.0} s on the cooldown for {Model}"));
          await retry.Delay(cooldown, ct).ConfigureAwait(false);
        }

        // Stamped before the request goes out: a 200 only says the limit has cleared if the request was sent after the cooldown went up.
        TimeSpan sentAt = tracker.Now;
        int? status = null;
        string message;
        TimeSpan? hint = null;
        bool rateLimited = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(retry.RequestTimeout);
        try
        {
          using HttpRequestMessage request = BuildRequest(system, user, schema, dropped);
          using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
          string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
          status = (int)response.StatusCode;

          if (response.IsSuccessStatusCode)
          {
            tracker.NoteSuccess(key, sentAt);
            TimeSpan? hold = ProactiveHold(response.Headers, retry.UtcNow());
            if (hold.HasValue && hold.Value > TimeSpan.Zero)
            {
              TimeSpan h = hold.Value > retry.MaxRetryWait ? retry.MaxRetryWait : hold.Value;
              Logger.Instance.info(FormattableString.Invariant($"{Name}: {Model} is out of quota, holding off {h.TotalSeconds:0.0} s"));
              tracker.NoteRateLimited(key, h);
            }
            try
            {
              using JsonDocument doc = JsonDocument.Parse(body);
              return ParseResponse(doc);
            }
            catch (JsonException ex)
            {
              throw new ProviderException(Name, status, "Response was not valid JSON: " + ex.Message, ex);
            }
          }

          message = ExtractErrorMessage(body);
          ResponseVerdict verdict = Classify(status.Value, response.Headers, body, retry.UtcNow());
          if (verdict.Action == ResponseAction.Fail)
          {
            string? drop = Degrade(status.Value, message, dropped);
            if (drop != null && dropped.Add(drop))
            {
              Logger.Instance.info($"{Name}: {Model} rejected the request ({message}); retrying without \"{drop}\".");
              continue; // does not count as a retry attempt
            }
            throw new ProviderException(Name, status, message);
          }
          hint = verdict.Delay;
          rateLimited = RetryPolicy.IsRateLimitStatus(status.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (OperationCanceledException)
        {
          message = FormattableString.Invariant($"Request timed out after {retry.RequestTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
          message = "Connection error: " + ex.Message;
          status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null;
        }

        // Retryable.
        if (attempt >= retry.MaxRetries)
          throw new ProviderException(Name, status, message, null, FormattableString.Invariant($"(gave up after {attempt + 1} attempts)"));

        // A hint of zero is not a hint: a reset that has already passed would fire every attempt back to back.
        TimeSpan wait = hint.HasValue && hint.Value > TimeSpan.Zero ? hint.Value : retry.BackoffFor(attempt);
        if (wait > retry.MaxRetryWait)
          throw new ProviderException(Name, status, message, null, FormattableString.Invariant(
            $"(asked to wait {wait.TotalSeconds:0} s, above the {retry.MaxRetryWait.TotalSeconds:0} s maximum)"));

        Logger.Instance.info(FormattableString.Invariant(
          $"{Name}: attempt {attempt + 1} failed ({(status.HasValue ? "HTTP " + status.Value : "no response")}: {message}); retrying in {wait.TotalSeconds:0.0} s"));
        // Only a rate-limit rejection is worth holding the whole model back for; a timeout, a dropped connection or a 500 is this request's problem.
        if (rateLimited) tracker.NoteRateLimited(key, wait);
        await retry.Delay(wait, ct).ConfigureAwait(false);
        attempt++;
      }
    }

    /// <summary>Read an int property that may be missing or null.</summary>
    protected static int IntOrZero(JsonElement obj, string name)
    {
      if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
        return v.TryGetInt32(out int i) ? i : 0;
      return 0;
    }

    protected static string StringOrEmpty(JsonElement obj, string name)
    {
      if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
        return v.GetString() ?? "";
      return "";
    }
  }


  /// <summary>
  /// Which provider serves a model ID (prefix dispatch, like <c>get_response</c> in the Python
  /// reference), API keys from preferences with environment override, and the factory the app uses.
  /// Tests set <see cref="Override"/> to inject a fake.
  /// </summary>
  public static class ChatProviders
  {
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";

    /// <summary>Test hook: when set, <see cref="Create"/> returns whatever this makes.</summary>
    public static Func<string, IChatProvider>? Override { get; set; }

    /// <summary>Provider name for a model ID, or null when the prefix is unknown.</summary>
    public static string? ProviderFor(string model)
    {
      if (string.IsNullOrWhiteSpace(model)) return null;
      string m = model.Trim().ToLowerInvariant();
      if (m.StartsWith("claude", StringComparison.Ordinal) || m.StartsWith("anthropic", StringComparison.Ordinal)) return Anthropic;
      if (m.StartsWith("gpt", StringComparison.Ordinal) || m.StartsWith("o1", StringComparison.Ordinal)
          || m.StartsWith("o3", StringComparison.Ordinal) || m.StartsWith("o4", StringComparison.Ordinal)
          || m.StartsWith("chatgpt", StringComparison.Ordinal)) return OpenAi;
      if (m.StartsWith("gemini", StringComparison.Ordinal)) return Gemini;
      return null;
    }

    public static string EnvVarFor(string provider) => provider switch
    {
      Anthropic => "ANTHROPIC_API_KEY",
      OpenAi => "OPENAI_API_KEY",
      Gemini => "GEMINI_API_KEY",
      _ => ""
    };

    /// <summary>The key to use: the environment variable when set, else the preference. Never log the result.</summary>
    public static string ApiKeyFor(string provider)
    {
      string env = EnvVarFor(provider);
      string? fromEnv = env == "" ? null : Environment.GetEnvironmentVariable(env);
      if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
      string pref = provider switch
      {
        Anthropic => ConstantSettings.AnthropicApiKey,
        OpenAi => ConstantSettings.OpenAiApiKey,
        Gemini => ConstantSettings.GeminiApiKey,
        _ => ""
      };
      return (pref ?? "").Trim();
    }

    /// <summary>True when a key is available for the model's provider (used for UI hints; never reveals the key).</summary>
    public static bool HasApiKeyFor(string model)
    {
      string? provider = ProviderFor(model);
      return provider != null && ApiKeyFor(provider) != "";
    }

    /// <summary>
    /// Requests in flight at once for a bulk run: the caller's override, else the
    /// <c>AI Max Concurrent Requests</c> preference; 0 = auto = <see cref="AiBulkRunner.AutoConcurrency"/>.
    /// Pacing itself is response-driven (see <see cref="HttpChatProvider"/>), this is only a backstop.
    /// </summary>
    public static int MaxConcurrentRequests(int? requested = null)
    {
      int n = requested ?? ConstantSettings.AiMaxConcurrentRequests;
      return n <= 0 ? AiBulkRunner.AutoConcurrency : n;
    }

    /// <summary>Make the adapter for a model, or throw a <see cref="ProviderException"/> naming the problem.</summary>
    public static IChatProvider Create(string model)
    {
      if (Override != null) return Override(model);
      string? provider = ProviderFor(model);
      RetryPolicy retry = RetryPolicy.FromPreferences();
      switch (provider)
      {
        case Anthropic: return new AnthropicProvider(model, ApiKeyFor(Anthropic), retry: retry);
        case OpenAi: return new OpenAiProvider(model, ApiKeyFor(OpenAi), retry: retry);
        case Gemini: return new GeminiProvider(model, ApiKeyFor(Gemini), retry: retry);
        default:
          throw new ProviderException("ai", null,
            $"Unknown model \"{model}\": the name must start with claude, gpt/o1/o3/o4 or gemini.");
      }
    }
  }
}
