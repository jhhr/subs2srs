using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>A clock the tracker and the retry loop read; tests move it by hand.</summary>
  internal sealed class FakeClock
  {
    public TimeSpan Now { get; private set; } = TimeSpan.FromSeconds(1000);
    public TimeSpan TotalSlept { get; private set; }
    public void Advance(TimeSpan by) { Now += by; }
    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
    /// <summary>A <see cref="RetryPolicy.Delay"/> that advances the clock instead of sleeping.</summary>
    public Task Sleep(TimeSpan d, CancellationToken ct)
    {
      ct.ThrowIfCancellationRequested();
      TotalSlept += d;
      Advance(d);
      return Task.CompletedTask;
    }
  }


  /// <summary>
  /// Response-driven rate limiting without network, mirroring <c>test_api_client.py</c> of the
  /// Python reference: header parsers, per-provider classification, the proactive hold, the shared
  /// cooldown tracker and the retry loop of <see cref="HttpChatProvider"/>.
  /// </summary>
  public class AiRateLimitTests
  {
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static string Rfc3339In(double seconds) => Now.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static HttpResponseHeaders Headers(params (string name, string value)[] headers)
    {
      var r = new HttpResponseMessage(HttpStatusCode.OK);
      foreach ((string name, string value) in headers) r.Headers.TryAddWithoutValidation(name, value);
      return r.Headers;
    }

    private static double Secs(TimeSpan? t) => t.HasValue ? t.Value.TotalSeconds : double.NaN;

    // ── parsers ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("20", 20.0)]
    [InlineData("1.5", 1.5)]
    [InlineData(" 7 ", 7.0)]
    [InlineData("0", 0.0)]
    [InlineData("-5", 0.0)]
    public void ParseSeconds_ReadsNumbers_NeverNegative(string value, double expected)
    {
      Assert.Equal(expected, Secs(RateLimitHeaders.ParseSeconds(value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("20s")]
    public void ParseSeconds_RejectsJunk(string? value)
    {
      Assert.Null(RateLimitHeaders.ParseSeconds(value));
    }

    [Fact]
    public void ParseRetryAfter_SecondsOrHttpDate()
    {
      Assert.Equal(20.0, Secs(RateLimitHeaders.ParseRetryAfter("20", Now)));
      Assert.Equal(30.0, Secs(RateLimitHeaders.ParseRetryAfter(Now.AddSeconds(30).ToString("R", CultureInfo.InvariantCulture), Now)));
      Assert.Equal(0.0, Secs(RateLimitHeaders.ParseRetryAfter(Now.AddSeconds(-30).ToString("R", CultureInfo.InvariantCulture), Now)));
      Assert.Null(RateLimitHeaders.ParseRetryAfter("later", Now));
      Assert.Null(RateLimitHeaders.ParseRetryAfter(null, Now));
    }

    [Theory]
    [InlineData("1s", 1.0)]
    [InlineData("6m0s", 360.0)]
    [InlineData("1m30s", 90.0)]
    [InlineData("20ms", 0.02)]
    [InlineData("1.5s", 1.5)]
    [InlineData("1h", 3600.0)]
    [InlineData("2h3m4.5s", 7384.5)]
    [InlineData(" 45s ", 45.0)]
    public void ParseGoDuration_ReadsGoStyleDurations(string value, double expected)
    {
      Assert.Equal(expected, Secs(RateLimitHeaders.ParseGoDuration(value)), 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("6m0s extra")]
    [InlineData("abc")]
    [InlineData("30")]
    [InlineData("1d")]
    public void ParseGoDuration_RejectsAnythingNotFullyUnderstood(string? value)
    {
      Assert.Null(RateLimitHeaders.ParseGoDuration(value));
    }

    [Fact]
    public void ParseGoogleDuration_PlainSecondsOnly()
    {
      Assert.Equal(34.0, Secs(RateLimitHeaders.ParseGoogleDuration("34s")));
      Assert.Equal(0.5, Secs(RateLimitHeaders.ParseGoogleDuration("0.5s")));
      Assert.Null(RateLimitHeaders.ParseGoogleDuration("34"));
      Assert.Null(RateLimitHeaders.ParseGoogleDuration("34m"));
      Assert.Null(RateLimitHeaders.ParseGoogleDuration(""));
      Assert.Null(RateLimitHeaders.ParseGoogleDuration(null));
    }

    [Fact]
    public void ParseRfc3339Reset_SecondsFromNow_NeverNegative_NaiveIsUtc()
    {
      Assert.Equal(25.0, Secs(RateLimitHeaders.ParseRfc3339Reset(Rfc3339In(25), Now)), 3);
      Assert.Equal(0.0, Secs(RateLimitHeaders.ParseRfc3339Reset(Rfc3339In(-25), Now)));
      Assert.Equal(60.0, Secs(RateLimitHeaders.ParseRfc3339Reset("2026-09-14T12:01:00", Now)));
      Assert.Equal(60.0, Secs(RateLimitHeaders.ParseRfc3339Reset("2026-09-14T13:01:00+01:00", Now)));
      Assert.Null(RateLimitHeaders.ParseRfc3339Reset("soon", Now));
      Assert.Null(RateLimitHeaders.ParseRfc3339Reset("2026-09-14", Now));
      Assert.Null(RateLimitHeaders.ParseRfc3339Reset("", Now));
      Assert.Null(RateLimitHeaders.ParseRfc3339Reset(null, Now));
    }

    // ── classify: Anthropic ───────────────────────────────────────────

    [Fact]
    public void Anthropic_429_UsesRetryAfter()
    {
      ResponseVerdict v = AnthropicProvider.ClassifyResponse(429, Headers(("retry-after", "20")), "{}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(20.0, Secs(v.Delay));
    }

    [Fact]
    public void Anthropic_Overloaded529_IsRetryable_WithoutAHintWhenNoneGiven()
    {
      ResponseVerdict v = AnthropicProvider.ClassifyResponse(529, Headers(), "{\"error\":{\"type\":\"overloaded_error\"}}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Null(v.Delay);
    }

    [Fact]
    public void Anthropic_FallsBackToTheBucketThatResetsSoonest()
    {
      ResponseVerdict v = AnthropicProvider.ClassifyResponse(429, Headers(
        ("anthropic-ratelimit-requests-reset", Rfc3339In(30)),
        ("anthropic-ratelimit-tokens-reset", Rfc3339In(5)),
        ("anthropic-ratelimit-input-tokens-reset", "garbage")), "{}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(5.0, Secs(v.Delay), 3);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(413)]
    public void Anthropic_ClientErrors_AreTerminal(int status)
    {
      Assert.Equal(ResponseAction.Fail, AnthropicProvider.ClassifyResponse(status, Headers(("retry-after", "5")), "{}", Now).Action);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Anthropic_ServerErrors_AreRetryable(int status)
    {
      Assert.Equal(ResponseAction.Retry, AnthropicProvider.ClassifyResponse(status, Headers(), "", Now).Action);
    }

    // ── classify: Gemini ──────────────────────────────────────────────

    private const string GeminiDaily = @"{""error"":{""code"":429,""message"":""quota"",""status"":""RESOURCE_EXHAUSTED"",""details"":[
      {""@type"":""type.googleapis.com/google.rpc.QuotaFailure"",""violations"":[{""quotaMetric"":""generate_content_free_tier_requests"",""quotaId"":""GenerateRequestsPerDayPerProjectPerModel-FreeTier""}]},
      {""@type"":""type.googleapis.com/google.rpc.RetryInfo"",""retryDelay"":""3600s""}]}}";

    private const string GeminiPerMinute = @"{""error"":{""code"":429,""message"":""quota"",""status"":""RESOURCE_EXHAUSTED"",""details"":[
      {""@type"":""type.googleapis.com/google.rpc.QuotaFailure"",""violations"":[{""quotaId"":""GenerateRequestsPerMinutePerProjectPerModel""}]},
      {""@type"":""type.googleapis.com/google.rpc.RetryInfo"",""retryDelay"":""34s""}]}}";

    [Fact]
    public void Gemini_DailyQuota_IsTerminal()
    {
      Assert.True(GeminiProvider.IsDailyQuota(GeminiDaily));
      Assert.Equal(ResponseAction.Fail, GeminiProvider.ClassifyResponse(429, Headers(), GeminiDaily, Now).Action);
    }

    [Fact]
    public void Gemini_PerMinuteQuota_IsRetryableWithItsHint()
    {
      ResponseVerdict v = GeminiProvider.ClassifyResponse(429, Headers(), GeminiPerMinute, Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(34.0, Secs(v.Delay));
    }

    [Theory]
    [InlineData("{\"error\":{\"details\":\"not a list\"}}")]
    [InlineData("{\"error\":{\"details\":[1, \"two\", {\"@type\":\"x/google.rpc.QuotaFailure\",\"violations\":\"junk\"}]}}")]
    [InlineData("{\"error\":\"rate limited\"}")]
    [InlineData("not json")]
    [InlineData("")]
    public void Gemini_SurvivesMalformedDetails_AndStillRetries(string body)
    {
      ResponseVerdict v = GeminiProvider.ClassifyResponse(429, Headers(), body, Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Null(v.Delay);
    }

    [Fact]
    public void Gemini_ServerErrorRetryable_ClientErrorTerminal()
    {
      Assert.Equal(ResponseAction.Retry, GeminiProvider.ClassifyResponse(503, Headers(), "{}", Now).Action);
      Assert.Equal(34.0, Secs(GeminiProvider.ClassifyResponse(500, Headers(), GeminiPerMinute, Now).Delay));
      Assert.Equal(ResponseAction.Fail, GeminiProvider.ClassifyResponse(400, Headers(), "{}", Now).Action);
      Assert.Equal(ResponseAction.Fail, GeminiProvider.ClassifyResponse(403, Headers(), "{}", Now).Action);
    }

    // ── classify: OpenAI ──────────────────────────────────────────────

    [Theory]
    [InlineData("{\"error\":{\"message\":\"quota\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}")]
    [InlineData("{\"error\":{\"message\":\"quota\",\"type\":\"insufficient_quota\",\"code\":null}}")]
    [InlineData("{\"error\":{\"message\":\"limit\",\"type\":\"requests\",\"code\":\"billing_hard_limit_exceeded\"}}")]
    public void OpenAi_SpentQuota_IsTerminal(string body)
    {
      Assert.Equal(ResponseAction.Fail, OpenAiProvider.ClassifyResponse(429, Headers(("Retry-After", "5")), body, Now).Action);
    }

    [Fact]
    public void OpenAi_OrdinaryRateLimitCode_IsNotASpentQuota()
    {
      // The phase 3 rule "any *_limit_exceeded code is terminal" would have made every plain rate limit fatal.
      const string body = "{\"error\":{\"message\":\"Rate limit reached for gpt-5-mini\",\"type\":\"requests\",\"code\":\"rate_limit_exceeded\"}}";
      Assert.False(OpenAiProvider.IsSpentQuota(body));
      Assert.Equal(ResponseAction.Retry, OpenAiProvider.ClassifyResponse(429, Headers(), body, Now).Action);
    }

    [Fact]
    public void OpenAi_429_PrefersRetryAfterOverResetHeaders()
    {
      ResponseVerdict v = OpenAiProvider.ClassifyResponse(429, Headers(
        ("Retry-After", "12"), ("x-ratelimit-reset-requests", "6m0s"), ("x-ratelimit-reset-tokens", "20ms")),
        "{\"error\":{\"message\":\"Rate limit reached\",\"type\":\"requests\",\"code\":\"rate_limit_exceeded\"}}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(12.0, Secs(v.Delay));
    }

    [Fact]
    public void OpenAi_429_FallsBackToTheBucketThatTakesLongest()
    {
      ResponseVerdict v = OpenAiProvider.ClassifyResponse(429, Headers(
        ("x-ratelimit-reset-requests", "6s"), ("x-ratelimit-reset-tokens", "1m30s")), "{}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(90.0, Secs(v.Delay));
    }

    [Fact]
    public void OpenAi_429_WithoutHints_LeavesTheDelayToBackoff()
    {
      ResponseVerdict v = OpenAiProvider.ClassifyResponse(429, Headers(), "{}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Null(v.Delay);
    }

    [Fact]
    public void OpenAi_AnErrorMemberThatIsNotAnObject_IsStillClassified()
    {
      // A proxy in front of the API may send a bare string.
      ResponseVerdict v = OpenAiProvider.ClassifyResponse(429, Headers(("Retry-After", "3")), "{\"error\":\"rate limit exceeded\"}", Now);
      Assert.Equal(ResponseAction.Retry, v.Action);
      Assert.Equal(3.0, Secs(v.Delay));
    }

    [Fact]
    public void OpenAi_ServerErrorsRetryable_ClientErrorsTerminal()
    {
      Assert.Equal(ResponseAction.Retry, OpenAiProvider.ClassifyResponse(500, Headers(), "{}", Now).Action);
      Assert.Equal(7.0, Secs(OpenAiProvider.ClassifyResponse(503, Headers(("Retry-After", "7")), "{}", Now).Delay));
      Assert.Equal(ResponseAction.Fail, OpenAiProvider.ClassifyResponse(400, Headers(), "{}", Now).Action);
      Assert.Equal(ResponseAction.Fail, OpenAiProvider.ClassifyResponse(401, Headers(), "{}", Now).Action);
      Assert.Equal(ResponseAction.Fail, OpenAiProvider.ClassifyResponse(408, Headers(), "{}", Now).Action);
    }

    // ── proactive hold ────────────────────────────────────────────────

    [Fact]
    public void Hold_Anthropic_OutOfRequests()
    {
      TimeSpan? hold = AnthropicProvider.ProactiveHoldFor(Headers(
        ("anthropic-ratelimit-requests-remaining", "0"), ("anthropic-ratelimit-requests-reset", Rfc3339In(25))), Now);
      Assert.Equal(25.0, Secs(hold), 3);
    }

    [Fact]
    public void Hold_Anthropic_HeadroomLeft_IsNoHold()
    {
      Assert.Null(AnthropicProvider.ProactiveHoldFor(Headers(
        ("anthropic-ratelimit-requests-remaining", "5"), ("anthropic-ratelimit-requests-reset", Rfc3339In(25)),
        ("anthropic-ratelimit-tokens-remaining", "12000"), ("anthropic-ratelimit-tokens-reset", Rfc3339In(25))), Now));
    }

    [Fact]
    public void Hold_Anthropic_OutOfTokensAlthoughRequestsAreFine()
    {
      TimeSpan? hold = AnthropicProvider.ProactiveHoldFor(Headers(
        ("anthropic-ratelimit-requests-remaining", "48"), ("anthropic-ratelimit-requests-reset", Rfc3339In(5)),
        ("anthropic-ratelimit-tokens-remaining", "0"), ("anthropic-ratelimit-tokens-reset", Rfc3339In(25))), Now);
      Assert.Equal(25.0, Secs(hold), 3);
    }

    [Fact]
    public void Hold_Anthropic_OutOfInputTokens_SeparateBuckets()
    {
      TimeSpan? hold = AnthropicProvider.ProactiveHoldFor(Headers(
        ("anthropic-ratelimit-input-tokens-remaining", "0"), ("anthropic-ratelimit-input-tokens-reset", Rfc3339In(25)),
        ("anthropic-ratelimit-output-tokens-remaining", "8000"), ("anthropic-ratelimit-output-tokens-reset", Rfc3339In(25))), Now);
      Assert.Equal(25.0, Secs(hold), 3);
    }

    [Fact]
    public void Hold_Anthropic_WaitsForTheLastBucketToClear_NotTheFirst()
    {
      TimeSpan? hold = AnthropicProvider.ProactiveHoldFor(Headers(
        ("anthropic-ratelimit-requests-remaining", "0"), ("anthropic-ratelimit-requests-reset", Rfc3339In(5)),
        ("anthropic-ratelimit-input-tokens-remaining", "0"), ("anthropic-ratelimit-input-tokens-reset", Rfc3339In(50))), Now);
      Assert.Equal(50.0, Secs(hold), 3);
    }

    [Fact]
    public void Hold_OpenAi_OutOfRequests_OrTokens_LongestWins()
    {
      Assert.Equal(30.0, Secs(OpenAiProvider.ProactiveHoldFor(Headers(
        ("x-ratelimit-remaining-requests", "0"), ("x-ratelimit-reset-requests", "30s")))));
      Assert.Equal(45.0, Secs(OpenAiProvider.ProactiveHoldFor(Headers(
        ("x-ratelimit-remaining-requests", "120"), ("x-ratelimit-reset-requests", "2s"),
        ("x-ratelimit-remaining-tokens", "0"), ("x-ratelimit-reset-tokens", "45s")))));
      Assert.Equal(90.0, Secs(OpenAiProvider.ProactiveHoldFor(Headers(
        ("x-ratelimit-remaining-requests", "0"), ("x-ratelimit-reset-requests", "6s"),
        ("x-ratelimit-remaining-tokens", "0"), ("x-ratelimit-reset-tokens", "1m30s")))));
    }

    [Fact]
    public void Hold_SpentBucketWithoutReset_UnparseableRemaining_OrNoHeaders_IsNoHold()
    {
      Assert.Null(OpenAiProvider.ProactiveHoldFor(Headers(("x-ratelimit-remaining-tokens", "0"))));
      Assert.Null(OpenAiProvider.ProactiveHoldFor(Headers(("x-ratelimit-remaining-requests", "lots"), ("x-ratelimit-reset-requests", "30s"))));
      Assert.Null(OpenAiProvider.ProactiveHoldFor(Headers(("x-ratelimit-remaining-requests", "0"), ("x-ratelimit-reset-requests", "soon"))));
      Assert.Null(OpenAiProvider.ProactiveHoldFor(Headers()));
      Assert.Null(AnthropicProvider.ProactiveHoldFor(Headers(), Now));
    }

    // ── tracker ───────────────────────────────────────────────────────

    private const string Key = "openai:gpt-5-mini";

    [Fact]
    public void Tracker_UnknownModelIsClear_CooldownCountsDownAndClearsItself()
    {
      var clock = new FakeClock();
      var t = new RateLimitTracker(() => clock.Now);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(10));
      Assert.Equal(10.0, t.WaitTime(Key).TotalSeconds);
      clock.Advance(4);
      Assert.Equal(6.0, t.WaitTime(Key).TotalSeconds);
      clock.Advance(6);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
    }

    [Fact]
    public void Tracker_NeverShortens_ButExtends()
    {
      var clock = new FakeClock();
      var t = new RateLimitTracker(() => clock.Now);
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(30));
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(5));
      Assert.Equal(30.0, t.WaitTime(Key).TotalSeconds);
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(60));
      Assert.Equal(60.0, t.WaitTime(Key).TotalSeconds);
    }

    [Fact]
    public void Tracker_IsPerModel_AndResetClearsAll()
    {
      var t = new RateLimitTracker(() => TimeSpan.Zero);
      t.NoteRateLimited("openai:a", TimeSpan.FromSeconds(10));
      Assert.Equal(TimeSpan.Zero, t.WaitTime("openai:b"));
      Assert.Equal(TimeSpan.Zero, t.WaitTime("anthropic:a"));
      Assert.Equal("anthropic:claude-sonnet-5", RateLimitTracker.KeyFor("anthropic", "claude-sonnet-5"));
      t.NoteRateLimited("openai:b", TimeSpan.FromSeconds(10));
      t.Reset();
      Assert.Equal(TimeSpan.Zero, t.WaitTime("openai:a"));
      Assert.Equal(TimeSpan.Zero, t.WaitTime("openai:b"));
    }

    [Fact]
    public void Tracker_StaleOrTiedSuccess_DoesNotClear_LaterSuccessDoes()
    {
      var clock = new FakeClock();
      var t = new RateLimitTracker(() => clock.Now);
      TimeSpan sentBefore = t.Now;
      clock.Advance(0.5);
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(30));
      TimeSpan setAt = t.Now;
      t.NoteSuccess(Key, sentBefore);
      Assert.Equal(30.0, t.WaitTime(Key).TotalSeconds);
      t.NoteSuccess(Key, setAt); // a tie counts as stale
      Assert.Equal(30.0, t.WaitTime(Key).TotalSeconds);
      clock.Advance(0.1);
      t.NoteSuccess(Key, t.Now);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
    }

    [Fact]
    public void Tracker_SuccessWithoutCooldownIsHarmless_WithoutTimestampClearsUnconditionally()
    {
      var clock = new FakeClock();
      var t = new RateLimitTracker(() => clock.Now);
      t.NoteSuccess(Key, t.Now);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(30));
      t.NoteSuccess(Key);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
    }

    [Fact]
    public void Tracker_StaleSuccessAfterExpiry_LeavesNothingBehind()
    {
      var clock = new FakeClock();
      var t = new RateLimitTracker(() => clock.Now);
      TimeSpan sentBefore = t.Now;
      clock.Advance(1);
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(5));
      clock.Advance(10);
      t.NoteSuccess(Key, sentBefore);
      Assert.Equal(TimeSpan.Zero, t.WaitTime(Key));
      t.NoteRateLimited(Key, TimeSpan.FromSeconds(5));
      Assert.Equal(5.0, t.WaitTime(Key).TotalSeconds); // not extended by a leftover entry
    }

    // ── the retry loop ────────────────────────────────────────────────

    private sealed class Loop
    {
      public FakeClock Clock { get; } = new FakeClock();
      public RateLimitTracker Tracker { get; }
      public RetryPolicy Policy { get; }
      public ScriptedHandler Handler { get; } = new ScriptedHandler();
      public List<TimeSpan> Delays { get; } = new List<TimeSpan>();
      /// <summary>What the tracker had for the model at each wait: tells a rate-limit hold from a per-request backoff.</summary>
      public List<TimeSpan> CooldownsAtWait { get; } = new List<TimeSpan>();
      public string Key { get; }
      public HttpChatProvider Provider { get; }

      public Loop(string model = "gpt-5-mini", int maxRetries = 5, double maxRetryWait = 120)
      {
        Tracker = new RateLimitTracker(() => Clock.Now);
        string provider = ChatProviders.ProviderFor(model)!;
        Key = RateLimitTracker.KeyFor(provider, model);
        Policy = new RetryPolicy
        {
          MaxRetries = maxRetries,
          MaxRetryWait = TimeSpan.FromSeconds(maxRetryWait),
          BaseDelay = TimeSpan.FromSeconds(1),
          MaxBackoff = TimeSpan.FromSeconds(60),
          Jitter = TimeSpan.Zero,
          Tracker = Tracker,
          UtcNow = () => Now,
          Delay = (d, ct) =>
          {
            Delays.Add(d);
            CooldownsAtWait.Add(Tracker.WaitTime(Key));
            return Clock.Sleep(d, ct);
          },
        };
        var client = new HttpClient(Handler);
        Provider = provider switch
        {
          ChatProviders.Anthropic => new AnthropicProvider(model, "k", client, Policy),
          ChatProviders.Gemini => new GeminiProvider(model, "k", client, Policy),
          _ => new OpenAiProvider(model, "k", client, Policy),
        };
      }

      public string Ok => Provider.Name switch
      {
        ChatProviders.Anthropic => AiProviderTests.Fixture("anthropic-ok.json"),
        ChatProviders.Gemini => AiProviderTests.Fixture("gemini-ok.json"),
        _ => AiProviderTests.Fixture("openai-ok.json"),
      };

      public Task<ChatCompletion> Post(CancellationToken ct = default) =>
        Provider.CompleteJsonAsync("s", "u", AiGroupingPrompt.Schema, ct);

      public int Calls => Handler.Requests.Count;
      public double Slept => Clock.TotalSlept.TotalSeconds;
    }

    [Fact]
    public async Task Loop_SuccessIsReturnedWithoutRetrying_AndReportsTheSendTime()
    {
      var loop = new Loop();
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok);
      ChatCompletion c = await loop.Post();
      Assert.Equal(120, c.InputTokens);
      Assert.Equal(1, loop.Calls);
      Assert.Equal(0.0, loop.Slept);
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key));
    }

    [Fact]
    public async Task Loop_RateLimit_IsWaitedOut_HoldsTheModel_AndTheRetryIsReturned()
    {
      var loop = new Loop();
      loop.Handler.Reply(HttpStatusCode.TooManyRequests, "{}", retryAfter: "20").Reply(HttpStatusCode.OK, loop.Ok);
      await loop.Post();
      Assert.Equal(2, loop.Calls);
      Assert.Equal(new[] { TimeSpan.FromSeconds(20) }, loop.Delays);
      Assert.Equal(new[] { TimeSpan.FromSeconds(20) }, loop.CooldownsAtWait); // installed for every other task too
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key)); // the success after the cooldown cleared it
    }

    [Fact]
    public async Task Loop_AnExistingCooldown_IsWaitedOutBeforeSending()
    {
      var loop = new Loop();
      loop.Tracker.NoteRateLimited(loop.Key, TimeSpan.FromSeconds(15));
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok);
      await loop.Post();
      Assert.Equal(1, loop.Calls);
      Assert.Equal(new[] { TimeSpan.FromSeconds(15) }, loop.Delays);
    }

    [Fact]
    public async Task Loop_ASuccessThatUsedUpTheLastOfABucket_HoldsTheModelOff()
    {
      var loop = new Loop();
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok, headers: new Dictionary<string, string>
      {
        ["x-ratelimit-remaining-requests"] = "0",
        ["x-ratelimit-reset-requests"] = "45s",
      });
      await loop.Post();
      Assert.Equal(1, loop.Calls);
      Assert.Equal(45.0, loop.Tracker.WaitTime(loop.Key).TotalSeconds);
    }

    [Fact]
    public async Task Loop_AProactiveHold_IsCappedAtMaxRetryWait()
    {
      // Decision recorded in the notes: unlike the Python, a hold longer than the retry ceiling is clamped,
      // so the run never stalls silently for longer than a rejection would be allowed to.
      var loop = new Loop(maxRetryWait: 120);
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok, headers: new Dictionary<string, string>
      {
        ["x-ratelimit-remaining-tokens"] = "0",
        ["x-ratelimit-reset-tokens"] = "10m0s",
      });
      await loop.Post();
      Assert.Equal(120.0, loop.Tracker.WaitTime(loop.Key).TotalSeconds);
    }

    [Fact]
    public async Task Loop_GeminiSuccess_InstallsNoHold()
    {
      var loop = new Loop("gemini-2.5-flash");
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok, headers: new Dictionary<string, string>
      {
        ["x-ratelimit-remaining-requests"] = "0",
        ["x-ratelimit-reset-requests"] = "45s",
      });
      await loop.Post();
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key));
    }

    [Fact]
    public async Task Loop_ATerminalResponse_IsNotRetried()
    {
      var loop = new Loop();
      loop.Handler.Reply(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad request\"}}");
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(400, ex.Status);
      Assert.Equal("bad request", ex.ProviderMessage);
      Assert.Equal(1, loop.Calls);
      Assert.Empty(loop.Delays);
    }

    [Fact]
    public async Task Loop_RetriesAreBounded_MaxRetriesIsAfterTheFirstSend()
    {
      var loop = new Loop(maxRetries: 2);
      for (int i = 0; i < 3; i++) loop.Handler.Reply(HttpStatusCode.TooManyRequests, "{}", retryAfter: "1");
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(3, loop.Calls);
      Assert.Equal(429, ex.Status);
      Assert.Contains("gave up after 3 attempts", ex.Message);
      Assert.Equal(2.0, loop.Slept);
    }

    [Fact]
    public async Task Loop_AWaitLongerThanTheMaximum_GivesUpAtOnce_WithoutACooldown()
    {
      var loop = new Loop(maxRetryWait: 120);
      loop.Handler.Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Rate limit\"}}", retryAfter: "3600");
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(1, loop.Calls);
      Assert.Equal(0.0, loop.Slept);
      Assert.Contains("asked to wait 3600 s, above the 120 s maximum", ex.Message);
      Assert.Equal("Rate limit", ex.ProviderMessage);
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key)); // nothing held back for a wait we did not take
    }

    [Fact]
    public async Task Loop_TransportFailures_AreRetried_AndGiveUpWhenTheyNeverRecover()
    {
      var loop = new Loop(maxRetries: 1);
      loop.Handler.Throw(new HttpRequestException("no route")).Throw(new HttpRequestException("no route"));
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(2, loop.Calls);
      Assert.Null(ex.Status);
      Assert.Contains("no route", ex.Message);
    }

    [Fact]
    public async Task Loop_AnExpiredHint_BacksOffRatherThanRetryingAtOnce()
    {
      // A reset that has already passed parses as 0; taken at face value every attempt would fire back to back
      // and the model's cooldown would be set to nothing.
      var loop = new Loop(maxRetries: 3);
      for (int i = 0; i < 4; i++) loop.Handler.Reply(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0");
      await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(4, loop.Calls);
      Assert.Equal(new[] { 1.0, 2.0, 4.0 }, loop.Delays.ConvertAll(d => d.TotalSeconds));
      Assert.All(loop.CooldownsAtWait, c => Assert.True(c > TimeSpan.Zero));
    }

    [Fact]
    public async Task Loop_BackoffGrows_WhenTheProviderGivesNoHint()
    {
      var loop = new Loop(maxRetries: 3);
      for (int i = 0; i < 4; i++) loop.Handler.Reply(HttpStatusCode.InternalServerError, "{}");
      await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(7.0, loop.Slept);
    }

    [Fact]
    public async Task Loop_AnOverloaded529_HoldsTheModelBack()
    {
      var loop = new Loop("claude-sonnet-5");
      loop.Handler.Reply((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", retryAfter: "10")
        .Reply(HttpStatusCode.OK, loop.Ok);
      await loop.Post();
      Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, loop.CooldownsAtWait);
    }

    [Fact]
    public async Task Loop_TimeoutDroppedConnectionAndServerError_NeverHoldTheModelBack()
    {
      var loop = new Loop();
      loop.Handler
        .Throw(new TaskCanceledException("timed out"))
        .Throw(new HttpRequestException("reset by peer"))
        .Reply(HttpStatusCode.InternalServerError, "{}")
        .Reply(HttpStatusCode.OK, loop.Ok);
      await loop.Post();
      Assert.Equal(4, loop.Calls);
      Assert.Equal(3, loop.Delays.Count);
      Assert.All(loop.CooldownsAtWait, c => Assert.Equal(TimeSpan.Zero, c));
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key));
    }

    [Fact]
    public async Task Loop_RepeatedTimeouts_NeverCompoundIntoAStall()
    {
      var loop = new Loop(maxRetries: 4);
      for (int i = 0; i < 5; i++) loop.Handler.Throw(new TaskCanceledException("timed out"));
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Contains("timed out", ex.Message);
      Assert.All(loop.CooldownsAtWait, c => Assert.Equal(TimeSpan.Zero, c));
      Assert.Equal(TimeSpan.Zero, loop.Tracker.WaitTime(loop.Key));
    }

    [Fact]
    public async Task Loop_ASuccess_DoesNotClearACooldownSetWhileItWasInFlight()
    {
      // Another task's 429 lands, and time passes, while this request is in flight; its 200 must not
      // release every waiting task straight back into the same limit.
      var loop = new Loop();
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok, beforeReply: () =>
      {
        loop.Clock.Advance(0.4);
        loop.Tracker.NoteRateLimited(loop.Key, TimeSpan.FromSeconds(30));
      });
      await loop.Post();
      Assert.Equal(30.0, loop.Tracker.WaitTime(loop.Key).TotalSeconds);
    }

    [Fact]
    public async Task Loop_CancellationDuringAWait_StopsWithoutSending()
    {
      var loop = new Loop();
      loop.Tracker.NoteRateLimited(loop.Key, TimeSpan.FromSeconds(15));
      loop.Handler.Reply(HttpStatusCode.OK, loop.Ok);
      var cts = new CancellationTokenSource();
      loop.Policy.Delay = (d, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.Post(cts.Token));
      Assert.Equal(0, loop.Calls);
    }

    [Fact]
    public async Task Loop_GeminiDailyQuota_FailsAtOnce()
    {
      var loop = new Loop("gemini-2.5-flash");
      loop.Handler.Reply(HttpStatusCode.TooManyRequests, GeminiDaily);
      var ex = await Assert.ThrowsAsync<ProviderException>(() => loop.Post());
      Assert.Equal(429, ex.Status);
      Assert.Equal(1, loop.Calls);
      Assert.Empty(loop.Delays);
    }

    [Fact]
    public async Task Loop_OptionDropAfterA400_DoesNotCountAsARetry()
    {
      var loop = new Loop("claude-haiku-4-5", maxRetries: 0);
      loop.Handler.Reply(HttpStatusCode.BadRequest, AiProviderTests.Fixture("anthropic-error.json")).Reply(HttpStatusCode.OK, loop.Ok);
      await loop.Post();
      Assert.Equal(2, loop.Calls);
      Assert.Empty(loop.Delays);
    }

    // ── preferences → policy ──────────────────────────────────────────

    [Fact]
    public void FromPreferences_ReadsTheThreeKnobs()
    {
      using var scope = new Harness.TestScope();
      ConstantSettings.AiMaxRetries = 7;
      ConstantSettings.AiMaxRetryWaitSeconds = 30;
      ConstantSettings.AiRequestTimeoutSeconds = 45;
      RetryPolicy p = RetryPolicy.FromPreferences();
      Assert.Equal(7, p.MaxRetries);
      Assert.Equal(TimeSpan.FromSeconds(30), p.MaxRetryWait);
      Assert.Equal(TimeSpan.FromSeconds(45), p.RequestTimeout);
      Assert.Same(RateLimitTracker.Shared, p.Tracker);
    }

    [Fact]
    public void MaxConcurrentRequests_OverrideThenPreference_ZeroIsAuto()
    {
      using var scope = new Harness.TestScope();
      ConstantSettings.AiMaxConcurrentRequests = 0;
      Assert.Equal(AiBulkRunner.AutoConcurrency, ChatProviders.MaxConcurrentRequests());
      Assert.Equal(AiBulkRunner.AutoConcurrency, ChatProviders.MaxConcurrentRequests(0));
      Assert.Equal(3, ChatProviders.MaxConcurrentRequests(3));
      ConstantSettings.AiMaxConcurrentRequests = 6;
      Assert.Equal(6, ChatProviders.MaxConcurrentRequests());
      Assert.Equal(2, ChatProviders.MaxConcurrentRequests(2));
    }
  }
}
