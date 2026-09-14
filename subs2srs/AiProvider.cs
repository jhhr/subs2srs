using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
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


  /// <summary>Retry, backoff and timeout knobs of <see cref="HttpChatProvider"/>; tests shrink the delays.</summary>
  public sealed class RetryPolicy
  {
    /// <summary>Total attempts including the first one.</summary>
    public int MaxAttempts { get; set; } = 4;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Per-request timeout; a timed-out request counts as a retryable failure.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Jitter as a fraction of the computed delay (0.25 = ±25 %).</summary>
    public double Jitter { get; set; } = 0.25;
    /// <summary>How to wait; tests replace it to record delays instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private static readonly Random rng = new Random();

    /// <summary>Exponential backoff with jitter for a zero-based attempt number, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
      double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
      double jitter;
      lock (rng) jitter = (rng.NextDouble() * 2 - 1) * Jitter;
      ms *= 1 + jitter;
      return TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, ms), MaxDelay.TotalMilliseconds));
    }

    public static bool IsRetryableStatus(int status) =>
      status == 408 || status == 409 || status == 429 || status >= 500;
  }


  /// <summary>
  /// Shared plumbing of the three raw-HTTP adapters: one <see cref="HttpClient"/>, a request
  /// timeout, bounded retries with exponential backoff + jitter on 408/409/429/5xx, timeouts and
  /// connection errors, honouring <c>Retry-After</c>, and a typed error with the provider's text.
  /// Subclasses only build the request body and pick the answer out of the response.
  /// </summary>
  public abstract class HttpChatProvider : IChatProvider
  {
    private static readonly Lazy<HttpClient> sharedClient = new Lazy<HttpClient>(() =>
      new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

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

    /// <summary>Build the request. <paramref name="options"/> lets a provider drop a feature after a 400 (see <see cref="Degrade"/>).</summary>
    protected abstract HttpRequestMessage BuildRequest(string system, string user, JsonElement schema, ISet<string> droppedOptions);

    /// <summary>Extract the completion from a 200 body.</summary>
    protected abstract ChatCompletion ParseResponse(JsonDocument body);

    /// <summary>
    /// Called on a non-retryable 4xx. Return the name of a request option to drop and retry
    /// without (e.g. an "effort" field an older model rejects), or null to fail.
    /// </summary>
    protected virtual string? Degrade(int status, string providerMessage, ISet<string> alreadyDropped) => null;

    /// <summary>Whether a failed status is worth another attempt; a provider may veto (e.g. a spent quota behind a 429).</summary>
    protected virtual bool IsRetryable(int status, string body) => RetryPolicy.IsRetryableStatus(status);

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

      var dropped = new HashSet<string>(StringComparer.Ordinal);
      int attempt = 0;
      while (true)
      {
        ct.ThrowIfCancellationRequested();
        int? status = null;
        string message;
        TimeSpan? retryAfter = null;
        bool retryable;

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
          retryAfter = ParseRetryAfter(response.Headers);
          retryable = IsRetryable(status.Value, body);
          if (!retryable)
          {
            string? drop = Degrade(status.Value, message, dropped);
            if (drop != null && dropped.Add(drop))
            {
              Logger.Instance.info($"{Name}: {Model} rejected the request ({message}); retrying without \"{drop}\".");
              continue; // does not count as a retry attempt
            }
            throw new ProviderException(Name, status, message);
          }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (OperationCanceledException)
        {
          message = FormattableString.Invariant($"Request timed out after {retry.RequestTimeout.TotalSeconds:0} s");
          retryable = true;
        }
        catch (HttpRequestException ex)
        {
          message = "Connection error: " + ex.Message;
          status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null;
          retryable = true;
        }

        attempt++;
        if (attempt >= retry.MaxAttempts)
          throw new ProviderException(Name, status, message, null, FormattableString.Invariant($"(gave up after {attempt} attempts)"));

        TimeSpan wait = retryAfter ?? retry.BackoffFor(attempt - 1);
        if (wait > retry.MaxDelay) wait = retry.MaxDelay;
        Logger.Instance.info(FormattableString.Invariant(
          $"{Name}: attempt {attempt} failed ({(status.HasValue ? "HTTP " + status.Value : "no response")}: {message}); retrying in {wait.TotalSeconds:0.0} s"));
        await retry.Delay(wait, ct).ConfigureAwait(false);
      }
    }

    internal static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
      if (headers.RetryAfter != null)
      {
        if (headers.RetryAfter.Delta.HasValue) return headers.RetryAfter.Delta.Value;
        if (headers.RetryAfter.Date.HasValue)
        {
          TimeSpan d = headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
          return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
      }
      if (headers.TryGetValues("retry-after", out IEnumerable<string>? values))
      {
        foreach (string v in values)
          if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            return TimeSpan.FromSeconds(Math.Max(0, secs));
      }
      return null;
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

    public static (int rpm, int concurrency) LimitsFor(string provider) => provider switch
    {
      Anthropic => (ConstantSettings.AnthropicRpm, ConstantSettings.AnthropicConcurrency),
      OpenAi => (ConstantSettings.OpenAiRpm, ConstantSettings.OpenAiConcurrency),
      Gemini => (ConstantSettings.GeminiRpm, ConstantSettings.GeminiConcurrency),
      _ => (60, 2)
    };

    /// <summary>Make the adapter for a model, or throw a <see cref="ProviderException"/> naming the problem.</summary>
    public static IChatProvider Create(string model)
    {
      if (Override != null) return Override(model);
      string? provider = ProviderFor(model);
      switch (provider)
      {
        case Anthropic: return new AnthropicProvider(model, ApiKeyFor(Anthropic));
        case OpenAi: return new OpenAiProvider(model, ApiKeyFor(OpenAi));
        case Gemini: return new GeminiProvider(model, ApiKeyFor(Gemini));
        default:
          throw new ProviderException("ai", null,
            $"Unknown model \"{model}\": the name must start with claude, gpt/o1/o3/o4 or gemini.");
      }
    }
  }
}
