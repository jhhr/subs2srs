using System;
using System.Collections.Generic;
using System.Net.Http;
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
  /// </summary>
  public sealed class AnthropicProvider : HttpChatProvider
  {
    public const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    public string Endpoint { get; init; } = DefaultEndpoint;
    public int MaxTokens { get; init; } = 8192;
    /// <summary>"low", "medium" or "high"; null sends nothing. Medium suits a classification-like task.</summary>
    public string? Effort { get; init; } = "medium";

    public override string Name => ChatProviders.Anthropic;

    public AnthropicProvider(string model, string apiKey, HttpClient? client = null, RetryPolicy? retry = null)
      : base(model, apiKey, client, retry) { }

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
      if (text.Length == 0)
        throw new ProviderException(Name, 200, stop == "refusal" ? "The model refused the request." : "No text block in the response (stop_reason: " + stop + ").");
      if (stop == "max_tokens")
        throw new ProviderException(Name, 200, FormattableString.Invariant($"Answer was cut off at max_tokens ({MaxTokens})."));

      JsonElement usage = root.TryGetProperty("usage", out JsonElement u) ? u : default;
      return new ChatCompletion
      {
        Text = text.ToString(),
        InputTokens = IntOrZero(usage, "input_tokens") + IntOrZero(usage, "cache_read_input_tokens") + IntOrZero(usage, "cache_creation_input_tokens"),
        OutputTokens = IntOrZero(usage, "output_tokens"),
        Model = StringOrEmpty(root, "model"),
      };
    }
  }
}
