using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// HttpMessageHandler that answers from a queue of canned responses and records every request
  /// (URL, headers, body). No network.
  /// </summary>
  internal sealed class ScriptedHandler : HttpMessageHandler
  {
    public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Script { get; } = new();
    public List<(string url, string body, Dictionary<string, string> headers)> Requests { get; } = new();

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
      var r = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
      if (retryAfter != null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
      return r;
    }

    public ScriptedHandler Reply(HttpStatusCode status, string body, string? retryAfter = null)
    {
      Script.Enqueue(_ => Json(status, body, retryAfter));
      return this;
    }

    public ScriptedHandler Throw(Exception ex)
    {
      Script.Enqueue(_ => throw ex);
      return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
      var headers = new Dictionary<string, string>();
      foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
      Requests.Add((request.RequestUri!.ToString(), body, headers));
      if (Script.Count == 0) throw new InvalidOperationException("No scripted response left");
      return Script.Dequeue()(request);
    }
  }


  public class AiProviderTests
  {
    internal static string Fixture(string name) =>
      File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ai", name));

    private static RetryPolicy FastRetry(List<TimeSpan>? delays = null) => new RetryPolicy
    {
      MaxAttempts = 3,
      BaseDelay = TimeSpan.FromMilliseconds(10),
      MaxDelay = TimeSpan.FromSeconds(5),
      Jitter = 0,
      Delay = (d, ct) => { delays?.Add(d); return Task.CompletedTask; },
    };

    private static JsonElement Schema => AiGroupingPrompt.Schema;

    // ── dispatch and keys ─────────────────────────────────────────────

    [Theory]
    [InlineData("claude-sonnet-5", "anthropic")]
    [InlineData("anthropic/claude", "anthropic")]
    [InlineData("gpt-5-mini", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("gemini-2.5-flash", "gemini")]
    [InlineData("GEMINI-2.5-pro", "gemini")]
    public void ProviderFor_DispatchesOnPrefix(string model, string expected)
    {
      Assert.Equal(expected, ChatProviders.ProviderFor(model));
    }

    [Theory]
    [InlineData("")]
    [InlineData("llama-3")]
    [InlineData("mistral/large")]
    public void ProviderFor_UnknownPrefix_IsNull(string model)
    {
      Assert.Null(ChatProviders.ProviderFor(model));
      Assert.Throws<ProviderException>(() => ChatProviders.Create(model));
    }

    [Fact]
    public void Create_PicksTheAdapterForThePrefix()
    {
      using var scope = new TestScope();
      Assert.IsType<AnthropicProvider>(ChatProviders.Create("claude-sonnet-5"));
      Assert.IsType<OpenAiProvider>(ChatProviders.Create("gpt-5"));
      Assert.IsType<GeminiProvider>(ChatProviders.Create("gemini-2.5-flash"));
    }

    [Fact]
    public void ApiKeyFor_EnvironmentOverridesPreference()
    {
      using var scope = new TestScope();
      string? saved = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
      try
      {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        ConstantSettings.GeminiApiKey = " pref-key ";
        Assert.Equal("pref-key", ChatProviders.ApiKeyFor(ChatProviders.Gemini));
        Assert.True(ChatProviders.HasApiKeyFor("gemini-2.5-flash"));

        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "env-key");
        Assert.Equal("env-key", ChatProviders.ApiKeyFor(ChatProviders.Gemini));

        ConstantSettings.GeminiApiKey = "";
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        Assert.False(ChatProviders.HasApiKeyFor("gemini-2.5-flash"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", saved);
      }
    }

    [Fact]
    public void Override_RoutesCreateToTheFake()
    {
      var fake = new FakeChatProvider();
      using (fake.Install())
        Assert.Same(fake, ChatProviders.Create("claude-sonnet-5"));
      Assert.Null(ChatProviders.Override);
    }

    // ── retry loop ────────────────────────────────────────────────────

    [Fact]
    public async Task MissingKey_ThrowsWithoutARequest()
    {
      var handler = new ScriptedHandler();
      var p = new AnthropicProvider("claude-sonnet-5", "", new HttpClient(handler), FastRetry());
      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Contains("No API key", ex.Message);
      Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimited_RetriesAfterTheHeaderThenSucceeds()
    {
      var delays = new List<TimeSpan>();
      var handler = new ScriptedHandler()
        .Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}", retryAfter: "3")
        .Reply(HttpStatusCode.OK, Fixture("anthropic-ok.json"));
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), FastRetry(delays));

      ChatCompletion c = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(2, handler.Requests.Count);
      Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);
      Assert.Contains("\"first\":0", c.Text);
    }

    [Fact]
    public async Task ServerErrors_UseBackoffAndGiveUpAfterMaxAttempts()
    {
      var delays = new List<TimeSpan>();
      var handler = new ScriptedHandler()
        .Reply(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")
        .Reply(HttpStatusCode.ServiceUnavailable, "overloaded")
        .Reply((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}");
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), FastRetry(delays));

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Equal(3, handler.Requests.Count);
      Assert.Equal(529, ex.Status);
      Assert.Equal("Overloaded", ex.ProviderMessage);
      Assert.Contains("gave up after 3 attempts", ex.Message);
      Assert.Equal(2, delays.Count);
      Assert.Equal(TimeSpan.FromMilliseconds(10), delays[0]);
      Assert.Equal(TimeSpan.FromMilliseconds(20), delays[1]);
    }

    [Fact]
    public async Task ConnectionErrorAndTimeout_AreRetried()
    {
      var handler = new ScriptedHandler()
        .Throw(new HttpRequestException("connection refused"))
        .Throw(new TaskCanceledException("timed out"))
        .Reply(HttpStatusCode.OK, Fixture("anthropic-ok.json"));
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);
      Assert.Equal(3, handler.Requests.Count);
      Assert.Equal(83, c.InputTokens); // input + cache read
    }

    [Fact]
    public async Task ClientError_IsNotRetried_AndCarriesTheProviderText()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.Unauthorized, Fixture("openai-error.json"));
      var p = new OpenAiProvider("gpt-5-mini", "k", new HttpClient(handler), FastRetry());

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Single(handler.Requests);
      Assert.Equal(401, ex.Status);
      Assert.StartsWith("Incorrect API key provided", ex.ProviderMessage);
      Assert.Equal("openai", ex.Provider);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoop()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.InternalServerError, "{}").Reply(HttpStatusCode.OK, Fixture("anthropic-ok.json"));
      var cts = new CancellationTokenSource();
      var policy = FastRetry();
      policy.Delay = (d, ct) => { cts.Cancel(); return Task.CompletedTask; };
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), policy);

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.CompleteJsonAsync("s", "u", Schema, cts.Token));
      Assert.Single(handler.Requests);
    }

    [Fact]
    public void BackoffFor_IsExponentialAndCapped()
    {
      var policy = new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5), Jitter = 0 };
      Assert.Equal(TimeSpan.FromSeconds(1), policy.BackoffFor(0));
      Assert.Equal(TimeSpan.FromSeconds(2), policy.BackoffFor(1));
      Assert.Equal(TimeSpan.FromSeconds(4), policy.BackoffFor(2));
      Assert.Equal(TimeSpan.FromSeconds(5), policy.BackoffFor(3));

      var jittered = new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(1), Jitter = 0.25 };
      for (int i = 0; i < 20; i++)
      {
        double ms = jittered.BackoffFor(0).TotalMilliseconds;
        Assert.InRange(ms, 750, 1250);
      }
    }

    // ── Anthropic ─────────────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_RequestShape()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Fixture("anthropic-ok.json"));
      var p = new AnthropicProvider("claude-sonnet-5", "secret-key", new HttpClient(handler), FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("SYS", "USER", Schema, CancellationToken.None);

      var (url, body, headers) = handler.Requests[0];
      Assert.Equal(AnthropicProvider.DefaultEndpoint, url);
      Assert.Equal("secret-key", headers["x-api-key"]);
      Assert.Equal("2023-06-01", headers["anthropic-version"]);
      using JsonDocument doc = JsonDocument.Parse(body);
      JsonElement root = doc.RootElement;
      Assert.Equal("claude-sonnet-5", root.GetProperty("model").GetString());
      Assert.Equal("SYS", root.GetProperty("system").GetString());
      Assert.Equal("USER", root.GetProperty("messages")[0].GetProperty("content").GetString());
      Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
      Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
      Assert.Equal("object", root.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("type").GetString());
      Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
      Assert.False(root.TryGetProperty("temperature", out _));
      Assert.DoesNotContain("secret-key", body);

      Assert.Equal(83, c.InputTokens);
      Assert.Equal(27, c.OutputTokens);
      Assert.Equal("claude-sonnet-5", c.Model);
    }

    [Fact]
    public async Task Anthropic_EffortRejected_RetriesWithoutIt()
    {
      var handler = new ScriptedHandler()
        .Reply(HttpStatusCode.BadRequest, Fixture("anthropic-error.json"))
        .Reply(HttpStatusCode.OK, Fixture("anthropic-ok.json"));
      var p = new AnthropicProvider("claude-haiku-4-5", "k", new HttpClient(handler), FastRetry());

      await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(2, handler.Requests.Count);
      Assert.Contains("\"effort\"", handler.Requests[0].body);
      Assert.DoesNotContain("\"effort\"", handler.Requests[1].body);
    }

    [Fact]
    public async Task Anthropic_MaxTokensStop_IsAnError()
    {
      string body = Fixture("anthropic-ok.json").Replace("\"end_turn\"", "\"max_tokens\"");
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, body);
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), FastRetry());
      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Contains("max_tokens", ex.Message);
    }

    // ── OpenAI ────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAi_RequestShape()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Fixture("openai-ok.json"));
      var p = new OpenAiProvider("gpt-5-mini", "secret-key", new HttpClient(handler), FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("SYS", "USER", Schema, CancellationToken.None);

      var (url, body, headers) = handler.Requests[0];
      Assert.Equal(OpenAiProvider.DefaultEndpoint, url);
      Assert.Equal("Bearer secret-key", headers["Authorization"]);
      using JsonDocument doc = JsonDocument.Parse(body);
      JsonElement root = doc.RootElement;
      Assert.Equal("gpt-5-mini", root.GetProperty("model").GetString());
      Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
      Assert.Equal("SYS", root.GetProperty("messages")[0].GetProperty("content").GetString());
      Assert.Equal("USER", root.GetProperty("messages")[1].GetProperty("content").GetString());
      JsonElement format = root.GetProperty("response_format");
      Assert.Equal("json_schema", format.GetProperty("type").GetString());
      Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
      Assert.False(format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
      Assert.True(root.TryGetProperty("max_completion_tokens", out _));
      Assert.False(root.TryGetProperty("max_tokens", out _));
      Assert.False(root.TryGetProperty("temperature", out _));

      Assert.Equal(120, c.InputTokens);
      Assert.Equal(40, c.OutputTokens);
      Assert.Contains("\"first\":0", c.Text);
    }

    [Fact]
    public async Task OpenAi_Refusal_IsAnError()
    {
      string body = Fixture("openai-ok.json").Replace("\"refusal\": null", "\"refusal\": \"I cannot help with that\"");
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, body);
      var p = new OpenAiProvider("gpt-5-mini", "k", new HttpClient(handler), FastRetry());
      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Contains("refused", ex.Message);
    }

    // ── Gemini ────────────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_RequestShape_KeyInHeaderAndSchemaCleaned()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Fixture("gemini-ok.json"));
      var p = new GeminiProvider("gemini-2.5-flash", "secret-key", new HttpClient(handler), FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("SYS", "USER", Schema, CancellationToken.None);

      var (url, body, headers) = handler.Requests[0];
      Assert.Equal(GeminiProvider.DefaultBaseUrl + "gemini-2.5-flash:generateContent", url);
      Assert.DoesNotContain("secret-key", url);
      Assert.Equal("secret-key", headers["x-goog-api-key"]);
      using JsonDocument doc = JsonDocument.Parse(body);
      JsonElement root = doc.RootElement;
      Assert.Equal("SYS", root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
      Assert.Equal("USER", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
      JsonElement gen = root.GetProperty("generationConfig");
      Assert.Equal("application/json", gen.GetProperty("responseMimeType").GetString());
      Assert.False(gen.TryGetProperty("responseSchema", out _));
      Assert.False(gen.TryGetProperty("thinkingConfig", out _));
      Assert.Equal("array", gen.GetProperty("responseJsonSchema").GetProperty("properties").GetProperty("snippets").GetProperty("type").GetString());
      Assert.False(gen.GetProperty("responseJsonSchema").GetProperty("additionalProperties").GetBoolean());

      Assert.Equal(150, c.InputTokens);
      Assert.Equal(100, c.OutputTokens); // candidates + thoughts
      Assert.Equal("gemini-2.5-flash", c.Model);
    }

    [Fact]
    public async Task Gemini_QuotaError_IsRetriedThenSurfaced()
    {
      var handler = new ScriptedHandler()
        .Reply(HttpStatusCode.TooManyRequests, Fixture("gemini-error.json"))
        .Reply(HttpStatusCode.TooManyRequests, Fixture("gemini-error.json"))
        .Reply(HttpStatusCode.TooManyRequests, Fixture("gemini-error.json"));
      var p = new GeminiProvider("gemini-2.5-flash", "k", new HttpClient(handler), FastRetry());
      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Equal(429, ex.Status);
      Assert.StartsWith("You exceeded your current quota", ex.ProviderMessage);
    }

    [Fact]
    public async Task Gemini_LegacySchema_IsCleaned_AndThinkingBudgetSent()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Fixture("gemini-ok.json"));
      var p = new GeminiProvider("gemini-2.5-flash", "k", new HttpClient(handler), FastRetry()) { UseLegacyResponseSchema = true, ThinkingBudget = 0 };
      await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);
      string body = handler.Requests[0].body;
      Assert.DoesNotContain("additionalProperties", body);
      Assert.Contains("\"responseSchema\"", body);
      Assert.Contains("\"thinkingBudget\":0", body);
    }

    [Fact]
    public async Task OpenAi_SpentQuota429_IsNotRetried()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.TooManyRequests,
        "{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\",\"param\":null}}");
      var p = new OpenAiProvider("gpt-5-mini", "k", new HttpClient(handler), FastRetry());
      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));
      Assert.Single(handler.Requests);
      Assert.Equal(429, ex.Status);
    }

    [Fact]
    public void Gemini_CleanSchema_RemovesAdditionalPropertiesEverywhere()
    {
      string cleaned = GeminiProvider.CleanSchema(Schema).ToJsonString();
      Assert.DoesNotContain("additionalProperties", cleaned);
      Assert.Contains("\"required\":[\"first\",\"last\",\"note\"]", cleaned);
      // the shared schema is untouched
      Assert.Contains("additionalProperties", Schema.GetRawText());
    }
  }
}
