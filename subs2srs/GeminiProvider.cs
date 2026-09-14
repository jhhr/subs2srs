using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace subs2srs
{
  /// <summary>
  /// Google Gemini <c>generateContent</c> with <c>responseMimeType: application/json</c> and
  /// <c>responseSchema</c>. The key goes in the <c>x-goog-api-key</c> header, never the URL, so it
  /// cannot leak into logs. Gemini's schema dialect is an OpenAPI subset that rejects
  /// <c>additionalProperties</c>, so the shared schema is cleaned on a copy exactly like
  /// <c>clean_response_schema_for_gemini</c> in the Python reference.
  /// </summary>
  public sealed class GeminiProvider : HttpChatProvider
  {
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    /// <summary>Covers thinking and output tokens together.</summary>
    public int MaxOutputTokens { get; init; } = 16384;
    /// <summary>Thinking budget in tokens; null sends nothing (the model's default). Dropped after a 400 that names it.</summary>
    public int? ThinkingBudget { get; init; } = 2048;

    public override string Name => ChatProviders.Gemini;

    public GeminiProvider(string model, string apiKey, HttpClient? client = null, RetryPolicy? retry = null)
      : base(model, apiKey, client, retry) { }

    protected override HttpRequestMessage BuildRequest(string system, string user, JsonElement schema, ISet<string> droppedOptions)
    {
      var generationConfig = new Dictionary<string, object>
      {
        ["responseMimeType"] = "application/json",
        ["responseSchema"] = CleanSchema(schema),
        ["maxOutputTokens"] = MaxOutputTokens,
      };
      if (ThinkingBudget.HasValue && !droppedOptions.Contains("thinkingConfig"))
        generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingBudget"] = ThinkingBudget.Value };

      var body = new Dictionary<string, object>
      {
        ["system_instruction"] = new Dictionary<string, object>
        {
          ["parts"] = new[] { new Dictionary<string, object> { ["text"] = system } }
        },
        ["contents"] = new[]
        {
          new Dictionary<string, object>
          {
            ["role"] = "user",
            ["parts"] = new[] { new Dictionary<string, object> { ["text"] = user } }
          }
        },
        ["generationConfig"] = generationConfig,
      };
      HttpRequestMessage req = JsonPost(BaseUrl + Uri.EscapeDataString(Model) + ":generateContent", body);
      req.Headers.Add("x-goog-api-key", ApiKey);
      return req;
    }

    protected override string? Degrade(int status, string providerMessage, ISet<string> alreadyDropped)
    {
      if (status == 400 && ThinkingBudget.HasValue && !alreadyDropped.Contains("thinkingConfig")
          && providerMessage.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0)
        return "thinkingConfig";
      return null;
    }

    /// <summary>Remove <c>additionalProperties</c> everywhere (objects and array items), on a copy.</summary>
    public static JsonNode CleanSchema(JsonElement schema)
    {
      JsonNode node = JsonNode.Parse(schema.GetRawText()) ?? new JsonObject();
      Clean(node);
      return node;
    }

    private static void Clean(JsonNode? node)
    {
      if (node is JsonObject obj)
      {
        obj.Remove("additionalProperties");
        foreach (KeyValuePair<string, JsonNode?> kv in new List<KeyValuePair<string, JsonNode?>>(obj))
          Clean(kv.Value);
      }
      else if (node is JsonArray arr)
      {
        foreach (JsonNode? item in arr) Clean(item);
      }
    }

    protected override ChatCompletion ParseResponse(JsonDocument body)
    {
      JsonElement root = body.RootElement;
      if (root.TryGetProperty("promptFeedback", out JsonElement feedback) && StringOrEmpty(feedback, "blockReason") != "")
        throw new ProviderException(Name, 200, "The prompt was blocked: " + StringOrEmpty(feedback, "blockReason"));
      if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        throw new ProviderException(Name, 200, "Response has no candidates.");
      JsonElement candidate = candidates[0];
      string finish = StringOrEmpty(candidate, "finishReason");
      var text = new System.Text.StringBuilder();
      if (candidate.TryGetProperty("content", out JsonElement content) && content.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement part in parts.EnumerateArray())
        {
          if (part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True) continue;
          text.Append(StringOrEmpty(part, "text"));
        }
      }
      if (finish == "MAX_TOKENS")
        throw new ProviderException(Name, 200, FormattableString.Invariant($"Answer was cut off at maxOutputTokens ({MaxOutputTokens})."));
      if (text.Length == 0)
        throw new ProviderException(Name, 200, "Empty candidate text (finishReason: " + finish + ").");

      JsonElement usage = root.TryGetProperty("usageMetadata", out JsonElement u) ? u : default;
      return new ChatCompletion
      {
        Text = text.ToString(),
        InputTokens = IntOrZero(usage, "promptTokenCount"),
        OutputTokens = IntOrZero(usage, "candidatesTokenCount") + IntOrZero(usage, "thoughtsTokenCount"),
        Model = StringOrEmpty(root, "modelVersion"),
      };
    }
  }
}
