using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace subs2srs
{
  /// <summary>
  /// Anthropic Messages API (<c>POST /v1/messages</c>) with structured output via
  /// <c>output_config.format</c> (json_schema, GA: no beta header). Verified against the official
  /// docs on 2026-09-14: current models reject <c>temperature</c>, so none is sent; adaptive
  /// thinking is the default and needs nothing; <c>output_config.effort</c> is accepted by Opus 5 /
  /// Sonnet 5 but not Haiku 4.5, so a 400 that names it makes us retry once without it.
  /// Answers are streamed (<c>"stream": true</c>): <c>max_tokens</c> caps thinking and answer
  /// together, and the docs advise against a large <c>max_tokens</c> without streaming because
  /// idle connections get dropped. The limit is <see cref="TargetOutputTokens"/>, or the model's
  /// own output cap when that is lower.
  /// </summary>
  public sealed class AnthropicProvider : HttpChatProvider
  {
    public const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    public string Endpoint { get; init; } = DefaultEndpoint;
    /// <summary>Output tokens to allow per request (thinking included) when the model's cap is not lower.</summary>
    public const int TargetOutputTokens = 32_000;

    /// <summary>
    /// Maximum output tokens of the synchronous Messages API per model, from
    /// https://platform.claude.com/docs/en/about-claude/models/overview (read 2026-09-15).
    /// Matched like the pricing table: the whole ID or the prefix followed by "-".
    /// </summary>
    private static readonly (string prefix, int maxOutput)[] OutputCaps =
    {
      ("claude-fable-5-1", 128_000),
      ("claude-opus-5", 128_000),
      ("claude-sonnet-5", 128_000),
      ("claude-haiku-4-5", 64_000),
    };

    /// <summary>The documented output cap of a model, or null when the table does not list it.</summary>
    public static int? OutputCapFor(string model)
    {
      if (string.IsNullOrWhiteSpace(model)) return null;
      string m = model.Trim().ToLowerInvariant();
      (string prefix, int maxOutput)? best = null;
      foreach (var row in OutputCaps)
      {
        bool matches = m.StartsWith(row.prefix, StringComparison.Ordinal)
          && (m.Length == row.prefix.Length || m[row.prefix.Length] == '-');
        if (matches && (best == null || row.prefix.Length > best.Value.prefix.Length))
          best = row;
      }
      return best?.maxOutput;
    }

    /// <summary><see cref="TargetOutputTokens"/>, lowered to the model's cap when the cap is smaller.</summary>
    public static int MaxTokensFor(string model) => Math.Min(TargetOutputTokens, OutputCapFor(model) ?? TargetOutputTokens);

    public int MaxTokens { get; init; }

    /// <summary>Ask for a server-sent-events stream (the default); a JSON answer is still accepted.</summary>
    public bool Stream { get; init; } = true;
    /// <summary>"low", "medium" or "high"; null sends nothing. Medium suits a classification-like task.</summary>
    public string? Effort { get; init; } = "medium";

    public override string Name => ChatProviders.Anthropic;

    public AnthropicProvider(string model, string apiKey, HttpClient? client = null, RetryPolicy? retry = null)
      : base(model, apiKey, client, retry)
    {
      MaxTokens = MaxTokensFor(model);
    }

    protected override HttpRequestMessage BuildRequest(string system, string user, JsonElement schema, ISet<string> droppedOptions)
    {
      var outputConfig = new Dictionary<string, object>
      {
        ["format"] = new Dictionary<string, object> { ["type"] = "json_schema", ["schema"] = schema }
      };
      if (Effort != null && !droppedOptions.Contains("effort"))
        outputConfig["effort"] = Effort;

      var body = new Dictionary<string, object>
      {
        ["model"] = Model,
        ["max_tokens"] = MaxTokens,
        ["system"] = system,
        ["messages"] = new[] { new Dictionary<string, object> { ["role"] = "user", ["content"] = user } },
        ["output_config"] = outputConfig,
      };
      if (Stream) body["stream"] = true;
      HttpRequestMessage req = JsonPost(Endpoint, body);
      req.Headers.Add("x-api-key", ApiKey);
      req.Headers.Add("anthropic-version", ApiVersion);
      return req;
    }

    protected override string? Degrade(int status, string providerMessage, ISet<string> alreadyDropped)
    {
      if (status == 400 && Effort != null && !alreadyDropped.Contains("effort")
          && providerMessage.IndexOf("effort", StringComparison.OrdinalIgnoreCase) >= 0)
        return "effort";
      return null;
    }

    /// <summary>The limit buckets a response can report as (remaining, reset); which ones come depends on the tier.</summary>
    public static readonly (string remaining, string reset)[] LimitBuckets =
    {
      ("anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-requests-reset"),
      ("anthropic-ratelimit-tokens-remaining", "anthropic-ratelimit-tokens-reset"),
      ("anthropic-ratelimit-input-tokens-remaining", "anthropic-ratelimit-input-tokens-reset"),
      ("anthropic-ratelimit-output-tokens-remaining", "anthropic-ratelimit-output-tokens-reset"),
    };

    /// <summary>
    /// 429 rate_limit_error, 529 overloaded_error and 500-504 are retried, waiting <c>retry-after</c>
    /// seconds or, without it, until the limit bucket that resets soonest (RFC 3339 <c>*-reset</c>
    /// headers). Everything else is terminal.
    /// </summary>
    public static ResponseVerdict ClassifyResponse(int status, HttpResponseHeaders headers, string body, DateTimeOffset now)
    {
      if (status == 429 || status == 529 || RetryPolicy.IsServerError(status))
      {
        TimeSpan? delay = RateLimitHeaders.ParseRetryAfter(RateLimitHeaders.First(headers, "retry-after"), now);
        if (!delay.HasValue)
        {
          delay = RateLimitHeaders.Soonest(
            RateLimitHeaders.ParseRfc3339Reset(RateLimitHeaders.First(headers, "anthropic-ratelimit-requests-reset"), now),
            RateLimitHeaders.ParseRfc3339Reset(RateLimitHeaders.First(headers, "anthropic-ratelimit-input-tokens-reset"), now),
            RateLimitHeaders.ParseRfc3339Reset(RateLimitHeaders.First(headers, "anthropic-ratelimit-output-tokens-reset"), now),
            RateLimitHeaders.ParseRfc3339Reset(RateLimitHeaders.First(headers, "anthropic-ratelimit-tokens-reset"), now));
        }
        return ResponseVerdict.Retry(delay);
      }
      return ResponseVerdict.Fail;
    }

    /// <summary>On a success: the latest reset among the spent buckets (tokens count as much as requests), or null with headroom.</summary>
    public static TimeSpan? ProactiveHoldFor(HttpResponseHeaders headers, DateTimeOffset now) =>
      RateLimitHeaders.LongestExhaustedWait(headers, LimitBuckets, v => RateLimitHeaders.ParseRfc3339Reset(v, now));

    protected override ResponseVerdict Classify(int status, HttpResponseHeaders headers, string body, DateTimeOffset now) =>
      ClassifyResponse(status, headers, body, now);

    protected override TimeSpan? ProactiveHold(HttpResponseHeaders headers, DateTimeOffset now) => ProactiveHoldFor(headers, now);

    protected override ChatCompletion ParseResponse(JsonDocument body)
    {
      JsonElement root = body.RootElement;
      var text = new StringBuilder();
      if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement block in content.EnumerateArray())
        {
          if (StringOrEmpty(block, "type") == "text")
            text.Append(StringOrEmpty(block, "text"));
        }
      }
      string stop = StringOrEmpty(root, "stop_reason");
      JsonElement usage = root.TryGetProperty("usage", out JsonElement u) ? u : default;
      int input = IntOrZero(usage, "input_tokens") + IntOrZero(usage, "cache_read_input_tokens") + IntOrZero(usage, "cache_creation_input_tokens");
      return Finish(text.ToString(), stop, input, IntOrZero(usage, "output_tokens"), StringOrEmpty(root, "model"));
    }

    /// <summary>
    /// A streamed answer: <c>message_start</c> (model, input usage), text from <c>text_delta</c>s,
    /// the stop reason and cumulative usage from <c>message_delta</c>. Thinking blocks, pings and
    /// unknown events are skipped; an <c>error</c> event becomes a <see cref="StreamErrorException"/>.
    /// </summary>
    protected override ChatCompletion ParseEventStream(IReadOnlyList<ServerSentEvent> events)
    {
      var text = new StringBuilder();
      string stop = "";
      string model = "";
      int input = 0;
      int output = 0;

      foreach (ServerSentEvent e in events)
      {
        if (e.Data.Length == 0) continue;
        JsonDocument doc;
        try
        {
          doc = JsonDocument.Parse(e.Data);
        }
        catch (JsonException ex)
        {
          throw new ProviderException(Name, 200, "Stream event was not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
          JsonElement root = doc.RootElement;
          string type = StringOrEmpty(root, "type");
          if (type == "") type = e.Event;
          switch (type)
          {
            case "message_start":
              if (root.TryGetProperty("message", out JsonElement message))
              {
                model = StringOrEmpty(message, "model");
                ReadStreamUsage(message, ref input, ref output);
              }
              break;
            case "content_block_start":
              if (root.TryGetProperty("content_block", out JsonElement block) && StringOrEmpty(block, "type") == "text")
                text.Append(StringOrEmpty(block, "text"));
              break;
            case "content_block_delta":
              if (root.TryGetProperty("delta", out JsonElement delta) && StringOrEmpty(delta, "type") == "text_delta")
                text.Append(StringOrEmpty(delta, "text"));
              break;
            case "message_delta":
              if (root.TryGetProperty("delta", out JsonElement messageDelta))
              {
                string s = StringOrEmpty(messageDelta, "stop_reason");
                if (s != "") stop = s;
              }
              ReadStreamUsage(root, ref input, ref output);
              break;
            case "error":
              JsonElement error = root.TryGetProperty("error", out JsonElement err) ? err : default;
              throw new StreamErrorException(StatusForStreamError(StringOrEmpty(error, "type")), e.Data, StringOrEmpty(error, "message"));
          }
        }
      }

      return Finish(text.ToString(), stop, input, output, model);
    }

    /// <summary>Usage in a stream event; <c>message_delta</c> counts are cumulative, so a present value replaces the earlier one.</summary>
    private static void ReadStreamUsage(JsonElement owner, ref int input, ref int output)
    {
      if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        return;
      if (usage.TryGetProperty("input_tokens", out JsonElement it) && it.ValueKind == JsonValueKind.Number)
        input = IntOrZero(usage, "input_tokens") + IntOrZero(usage, "cache_read_input_tokens") + IntOrZero(usage, "cache_creation_input_tokens");
      if (usage.TryGetProperty("output_tokens", out JsonElement ot) && ot.ValueKind == JsonValueKind.Number)
        output = IntOrZero(usage, "output_tokens");
    }

    /// <summary>The HTTP status an error type has outside a stream (https://platform.claude.com/docs/en/api/errors); unknown types are terminal.</summary>
    public static int StatusForStreamError(string errorType) => errorType switch
    {
      "overloaded_error" => 529,
      "rate_limit_error" => 429,
      "api_error" => 500,
      "timeout_error" => 504,
      "authentication_error" => 401,
      "billing_error" => 402,
      "permission_error" => 403,
      "not_found_error" => 404,
      "request_too_large" => 413,
      _ => 400,
    };

    /// <summary>The checks shared by a JSON and a streamed answer.</summary>
    private ChatCompletion Finish(string text, string stop, int inputTokens, int outputTokens, string model)
    {
      if (text.Length == 0)
        throw new ProviderException(Name, 200, stop == "refusal" ? "The model refused the request." : "No text block in the response (stop_reason: " + stop + ").");
      if (stop == "max_tokens")
        throw new ProviderException(Name, 200, FormattableString.Invariant($"Answer was cut off at max_tokens ({MaxTokens})."));
      return new ChatCompletion
      {
        Text = text,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        Model = model,
      };
    }
  }
}
