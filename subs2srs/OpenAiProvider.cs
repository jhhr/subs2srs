using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace subs2srs
{
  /// <summary>
  /// OpenAI Chat Completions (<c>POST /v1/chat/completions</c>) with
  /// <c>response_format: {type: json_schema, json_schema: {name, schema, strict: true}}</c>.
  /// Strict mode wants <c>additionalProperties: false</c> and every property required, which the
  /// grouping schema satisfies. <c>max_completion_tokens</c> is the current limit field (it covers
  /// reasoning + output); no temperature is sent because reasoning models reject non-default values.
  /// Verified against the official docs on 2026-09-14: Chat Completions is still supported (the
  /// Responses API is only recommended for new projects); <c>reasoning_effort</c> values differ
  /// per model, so an unsupported one is dropped after a 400.
  /// </summary>
  public sealed class OpenAiProvider : HttpChatProvider
  {
    public const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";

    public string Endpoint { get; init; } = DefaultEndpoint;
    public int MaxCompletionTokens { get; init; } = 16384;
    /// <summary>Reasoning effort ("minimal", "low", "medium", "high"); null sends nothing. Dropped after a 400 that names it.</summary>
    public string? ReasoningEffort { get; init; } = "low";

    public override string Name => ChatProviders.OpenAi;

    public OpenAiProvider(string model, string apiKey, HttpClient? client = null, RetryPolicy? retry = null)
      : base(model, apiKey, client, retry) { }

    protected override HttpRequestMessage BuildRequest(string system, string user, JsonElement schema, ISet<string> droppedOptions)
    {
      var body = new Dictionary<string, object>
      {
        ["model"] = Model,
        ["messages"] = new[]
        {
          new Dictionary<string, object> { ["role"] = "system", ["content"] = system },
          new Dictionary<string, object> { ["role"] = "user", ["content"] = user },
        },
        ["max_completion_tokens"] = MaxCompletionTokens,
        ["response_format"] = new Dictionary<string, object>
        {
          ["type"] = "json_schema",
          ["json_schema"] = new Dictionary<string, object>
          {
            ["name"] = "snippet_grouping",
            ["schema"] = schema,
            ["strict"] = true,
          }
        },
      };
      if (ReasoningEffort != null && !droppedOptions.Contains("reasoning_effort"))
        body["reasoning_effort"] = ReasoningEffort;

      HttpRequestMessage req = JsonPost(Endpoint, body);
      req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
      return req;
    }

    /// <summary>A 429 for a spent balance or spend limit does not clear by waiting (docs: insufficient_quota and the *_limit_exceeded codes).</summary>
    protected override bool IsRetryable(int status, string body)
    {
      if (status != 429) return base.IsRetryable(status, body);
      try
      {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out JsonElement err))
        {
          string type = StringOrEmpty(err, "type");
          string code = StringOrEmpty(err, "code");
          if (type == "insufficient_quota" || code == "insufficient_quota" || code == "credit_balance_exhausted"
              || code.EndsWith("_limit_exceeded", StringComparison.Ordinal))
            return false;
        }
      }
      catch (JsonException) { }
      return true;
    }

    protected override string? Degrade(int status, string providerMessage, ISet<string> alreadyDropped)
    {
      if (status == 400 && ReasoningEffort != null && !alreadyDropped.Contains("reasoning_effort")
          && providerMessage.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0)
        return "reasoning_effort";
      return null;
    }

    protected override ChatCompletion ParseResponse(JsonDocument body)
    {
      JsonElement root = body.RootElement;
      if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        throw new ProviderException(Name, 200, "Response has no choices.");
      JsonElement choice = choices[0];
      JsonElement message = choice.TryGetProperty("message", out JsonElement m) ? m : default;
      string refusal = StringOrEmpty(message, "refusal");
      if (refusal != "")
        throw new ProviderException(Name, 200, "The model refused the request: " + refusal);
      string text = StringOrEmpty(message, "content");
      string finish = StringOrEmpty(choice, "finish_reason");
      if (finish == "length")
        throw new ProviderException(Name, 200, FormattableString.Invariant($"Answer was cut off at max_completion_tokens ({MaxCompletionTokens})."));
      if (text == "")
        throw new ProviderException(Name, 200, "Empty message content (finish_reason: " + finish + ").");

      JsonElement usage = root.TryGetProperty("usage", out JsonElement u) ? u : default;
      return new ChatCompletion
      {
        Text = text,
        InputTokens = IntOrZero(usage, "prompt_tokens"),
        OutputTokens = IntOrZero(usage, "completion_tokens"),
        Model = StringOrEmpty(root, "model"),
      };
    }
  }
}
