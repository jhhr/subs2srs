using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  public class AiGroupingTests
  {
    private static InfoCombined Line(double start, double end, string text, bool active = true, string actor = "")
      => SnippetGroupingTests.Line(start, end, text, active, actor);

    private static SnippetLimits Limits(int maxMs = 15_000) => new(maxMs, 500, 0);

    /// <summary>n lines, 1 s each, with a gap of <paramref name="gapSeconds"/> every <paramref name="every"/> lines.</summary>
    private static List<InfoCombined> Episode(int n, int every, double gapSeconds = 20)
    {
      var lines = new List<InfoCombined>();
      double t = 0;
      for (int i = 0; i < n; i++)
      {
        if (i > 0 && i % every == 0) t += gapSeconds;
        lines.Add(Line(t, t + 1, "line " + i));
        t += 1.2;
      }
      return lines;
    }

    // ── chunker ───────────────────────────────────────────────────────

    [Fact]
    public void Chunker_ZeroTarget_IsWholeEpisode()
    {
      var lines = Episode(50, 10);
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var chunks = AiChunker.Split(lines, kept, 15_000, 0);
      Assert.Single(chunks);
      Assert.Equal(0, chunks[0].KeptStart);
      Assert.Equal(49, chunks[0].KeptEnd);
    }

    [Fact]
    public void Chunker_CutsOnlyAtLongGaps_ClosestToTarget()
    {
      var lines = Episode(50, 10); // boundaries at 10, 20, 30, 40
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var chunks = AiChunker.Split(lines, kept, 15_000, 25);
      Assert.Equal(new[] { (0, 19), (20, 39), (40, 49) }, chunks.Select(c => (c.KeptStart, c.KeptEnd)).ToArray());
      Assert.Equal(50, chunks.Sum(c => c.Count));
    }

    [Fact]
    public void Chunker_ShortGapsAreNotBoundaries()
    {
      var lines = Episode(50, 10, gapSeconds: 10); // 10 s < 15 s limit
      int[] kept = SnippetGrouping.KeptIndices(lines);
      Assert.Single(AiChunker.Split(lines, kept, 15_000, 25));
      Assert.Equal(5, AiChunker.Split(lines, kept, 8_000, 10).Count);
    }

    [Fact]
    public void Chunker_SkipsInactiveLines()
    {
      var lines = Episode(30, 10);
      lines[5].Active = false;
      lines[25].Active = false;
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var chunks = AiChunker.Split(lines, kept, 15_000, 10);
      Assert.Equal(28, chunks.Sum(c => c.Count));
      Assert.Equal(3, chunks.Count);
    }

    // ── prompt ────────────────────────────────────────────────────────

    [Fact]
    public void Prompt_UserContent_HasKeptPositionsTimesAndText()
    {
      var lines = new List<InfoCombined>
      {
        Line(1.0, 2.0, "Where?", actor: "A"),
        Line(2.5, 3.0, "skip me", active: false),
        Line(3.5, 4.0, "Here."),
      };
      int[] kept = SnippetGrouping.KeptIndices(lines);
      string user = AiGroupingPrompt.BuildUser(lines, kept, new AiChunk { KeptStart = 0, KeptEnd = 1 }, hasSubs2: true);
      using JsonDocument doc = JsonDocument.Parse(user);
      JsonElement arr = doc.RootElement;
      Assert.Equal(2, arr.GetArrayLength());
      Assert.Equal(0, arr[0].GetProperty("i").GetInt32());
      Assert.Equal("00:00:01.000", arr[0].GetProperty("s").GetString());
      Assert.Equal("00:00:02.000", arr[0].GetProperty("e").GetString());
      Assert.Equal("A", arr[0].GetProperty("actor").GetString());
      Assert.Equal("Where?", arr[0].GetProperty("t").GetString());
      Assert.Equal("WHERE?", arr[0].GetProperty("t2").GetString());
      Assert.Equal(1, arr[1].GetProperty("i").GetInt32());
      Assert.Equal("Here.", arr[1].GetProperty("t").GetString());
      Assert.DoesNotContain("skip me", user);
    }

    [Fact]
    public void Prompt_System_MentionsLimitAndExtraInstructions()
    {
      string s = AiGroupingPrompt.BuildSystem(12, true, "the show is a workplace comedy");
      Assert.Contains("12 seconds", s);
      Assert.Contains("t2", s);
      Assert.EndsWith("the show is a workplace comedy", s);
      Assert.DoesNotContain("Additional instructions", AiGroupingPrompt.BuildSystem(15, false, "  "));
      Assert.Equal(1, AiGroupingPrompt.PromptVersion);
    }

    [Fact]
    public void Schema_IsStrictModeCompatible()
    {
      JsonElement s = AiGroupingPrompt.Schema;
      Assert.False(s.GetProperty("additionalProperties").GetBoolean());
      JsonElement item = s.GetProperty("properties").GetProperty("snippets").GetProperty("items");
      Assert.Equal(3, item.GetProperty("required").GetArrayLength());
      Assert.Equal("integer", item.GetProperty("properties").GetProperty("first").GetProperty("type").GetString());
    }

    // ── parser ────────────────────────────────────────────────────────

    private static AiChunk Chunk(int a, int b) => new AiChunk { KeptStart = a, KeptEnd = b };

    [Fact]
    public void Parser_ReadsRangesAndNotes()
    {
      var a = AiAnswerParser.Parse("{\"snippets\":[{\"first\":2,\"last\":4,\"note\":\"why\"},{\"first\":7,\"last\":8,\"note\":\"\"}]}", Chunk(0, 9));
      Assert.Equal(new[] { (2, 4, "why"), (7, 8, "") }, a.Snippets.ToArray());
      Assert.Empty(a.Repairs);

      var joins = new bool[10];
      var notes = new Dictionary<int, string>();
      AiAnswerParser.ApplyToKeptJoins(a, joins, notes);
      Assert.Equal(new[] { 2, 3, 7 }, Enumerable.Range(0, 10).Where(i => joins[i]).ToArray());
      Assert.Equal("why", notes[2]);
      Assert.False(notes.ContainsKey(7));
    }

    [Fact]
    public void Parser_RepairsOutOfRangeOverlapAndSwapped()
    {
      string json = "{\"snippets\":[{\"first\":8,\"last\":12,\"note\":\"\"},{\"first\":3,\"last\":1,\"note\":\"\"},"
        + "{\"first\":2,\"last\":5,\"note\":\"\"},{\"first\":20,\"last\":25,\"note\":\"\"},{\"first\":6,\"last\":6,\"note\":\"\"},{\"first\":\"x\"}]}";
      var a = AiAnswerParser.Parse(json, Chunk(0, 9));
      Assert.Equal(new[] { (1, 3, ""), (4, 5, ""), (8, 9, "") }, a.Snippets.ToArray());
      Assert.Contains(a.Repairs, r => r.Contains("clipped 8..12"));
      Assert.Contains(a.Repairs, r => r.Contains("swapped"));
      Assert.Contains(a.Repairs, r => r.Contains("overlaps"));
      Assert.Contains(a.Repairs, r => r.Contains("dropped 20..25"));
      Assert.Contains(a.Repairs, r => r.Contains("without integer"));
    }

    [Fact]
    public void Parser_ToleratesProseAndBrokenJson()
    {
      var ok = AiAnswerParser.Parse("Sure! ```json\n{\"snippets\":[{\"first\":0,\"last\":1,\"note\":\"n\"}]}\n```", Chunk(0, 5));
      Assert.Single(ok.Snippets);

      var broken = AiAnswerParser.Parse("{\"snippets\":[{\"first\":0,", Chunk(0, 5));
      Assert.Empty(broken.Snippets);
      Assert.Contains(broken.Repairs, r => r.Contains("not valid JSON"));

      var wrong = AiAnswerParser.Parse("{\"groups\":[]}", Chunk(0, 5));
      Assert.Empty(wrong.Snippets);
      Assert.Single(wrong.Repairs);
    }

    // ── cache ─────────────────────────────────────────────────────────

    [Fact]
    public void CacheKey_IsStableAndSensitiveToInputs()
    {
      var lines = Episode(6, 3);
      int[] kept = SnippetGrouping.KeptIndices(lines);
      string k1 = AiGroupingCache.KeyFor(lines, kept, "claude-sonnet-5", Limits(), 200, "");
      string k2 = AiGroupingCache.KeyFor(Episode(6, 3), kept, "claude-sonnet-5", Limits(), 200, null);
      Assert.Equal(k1, k2);
      Assert.Equal(64, k1.Length);
      Assert.NotEqual(k1, AiGroupingCache.KeyFor(lines, kept, "gpt-5", Limits(), 200, ""));
      Assert.NotEqual(k1, AiGroupingCache.KeyFor(lines, kept, "claude-sonnet-5", Limits(12_000), 200, ""));
      Assert.NotEqual(k1, AiGroupingCache.KeyFor(lines, kept, "claude-sonnet-5", Limits(), 0, ""));
      Assert.NotEqual(k1, AiGroupingCache.KeyFor(lines, kept, "claude-sonnet-5", Limits(), 200, "comedy"));
      lines[2].Active = false;
      Assert.NotEqual(k1, AiGroupingCache.KeyFor(lines, SnippetGrouping.KeptIndices(lines), "claude-sonnet-5", Limits(), 200, ""));
    }

    [Fact]
    public void Cache_RoundTrips()
    {
      using var scope = new TestScope();
      var cache = new AiGroupingCache(Path.Combine(scope.TempDir, "cache"));
      Assert.Null(cache.TryGet("nope"));
      var result = new AiGroupingResult
      {
        Model = "m", KeptJoins = new[] { true, false, false }, Notes = { [0] = "why" }, InputTokens = 5, OutputTokens = 6, Chunks = 1,
      };
      cache.Put("abc", result);
      AiGroupingResult? back = cache.TryGet("abc");
      Assert.NotNull(back);
      Assert.True(back!.FromCache);
      Assert.Equal(result.KeptJoins, back.KeptJoins);
      Assert.Equal("why", back.Notes[0]);
      Assert.Equal(5, back.InputTokens);
      Assert.Equal(AiGroupingPrompt.PromptVersion, back.PromptVersion);

      File.WriteAllText(cache.PathFor("bad"), "{not json");
      Assert.Null(cache.TryGet("bad"));
    }

    [Fact]
    public void Cache_DefaultDirComesFromThePreference()
    {
      using var scope = new TestScope();
      ConstantSettings.AiCacheDir = "";
      Assert.EndsWith("ai-cache", new AiGroupingCache().Dir);
      ConstantSettings.AiCacheDir = scope.TempDir;
      Assert.Equal(scope.TempDir, new AiGroupingCache().Dir);
    }

    // ── runner ────────────────────────────────────────────────────────

    [Fact]
    public async Task Runner_BoundsConcurrencyAndKeepsOrder()
    {
      var runner = new AiBulkRunner(maxConcurrent: 2, rpm: 0);
      int inFlight = 0, maxSeen = 0;
      var results = await runner.RunAsync(Enumerable.Range(0, 8).ToList(), async (i, ct) =>
      {
        int now = Interlocked.Increment(ref inFlight);
        lock (runner) maxSeen = Math.Max(maxSeen, now);
        await Task.Delay(20, ct);
        Interlocked.Decrement(ref inFlight);
        return i * 10;
      }, null, "test", CancellationToken.None);
      Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 10), results.Select(r => r.Value));
      Assert.InRange(maxSeen, 1, 2);
    }

    [Fact]
    public async Task Runner_PacesRequestStarts()
    {
      var delays = new List<TimeSpan>();
      var runner = new AiBulkRunner(maxConcurrent: 4, rpm: 120) { Delay = (d, ct) => { lock (delays) delays.Add(d); return Task.CompletedTask; } };
      await runner.RunAsync(Enumerable.Range(0, 4).ToList(), (i, ct) => Task.FromResult(i), null, "test", CancellationToken.None);
      // 4 starts at 0.5 s spacing: three of them had to wait roughly 0.5, 1.0 and 1.5 s
      Assert.Equal(3, delays.Count);
      var sorted = delays.OrderBy(d => d).ToList();
      Assert.InRange(sorted[0].TotalSeconds, 0.4, 0.6);
      Assert.InRange(sorted[2].TotalSeconds, 1.4, 1.6);
    }

    [Fact]
    public async Task Runner_FailedItemDoesNotStopOthers_AndProgressCounts()
    {
      var progress = new NullProgressReporter();
      var runner = new AiBulkRunner(2, 0);
      var results = await runner.RunAsync(new[] { 1, 2, 3 }, (i, ct) =>
        i == 2 ? Task.FromException<int>(new ProviderException("fake", 500, "boom")) : Task.FromResult(i), progress, "AI grouping", CancellationToken.None);
      Assert.True(results[0].Ok);
      Assert.False(results[1].Ok);
      Assert.IsType<ProviderException>(results[1].Error);
      Assert.True(results[2].Ok);
      Assert.Equal(100, progress.LastPercent);
      Assert.Equal("AI grouping: 3 of 3 chunks", progress.LastText);
    }

    [Fact]
    public async Task Runner_CancelsThroughTheReporter()
    {
      var progress = new CancellingProgressReporter(cancelAfterSteps: 0);
      var runner = new AiBulkRunner(2, 0);
      int calls = 0;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new[] { 1, 2, 3 }, (i, ct) =>
      {
        Interlocked.Increment(ref calls);
        return Task.FromResult(i);
      }, progress, "AI grouping", CancellationToken.None));
      Assert.Equal(0, calls);
    }

    // ── grouper end to end with the fake provider ─────────────────────

    private static AiGroupingOptions Options(TestScope scope, bool force = false) => new AiGroupingOptions
    {
      Model = "fake-model",
      ChunkTargetLines = 10,
      CacheDir = Path.Combine(scope.TempDir, "cache"),
      ForceRefresh = force,
      Concurrency = 2,
      Rpm = 0,
    };

    [Fact]
    public async Task Grouper_JoinsPerAnswer_PutsNotesOnLines_AndCaches()
    {
      using var scope = new TestScope();
      var lines = Episode(20, 10);
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinPairs("pair"));
      using var _ = fake.Install();
      var progress = new NullProgressReporter();

      AiGroupingResult r = await AiGrouper.GroupAsync(lines, Limits(), Options(scope), progress, CancellationToken.None);

      Assert.Equal(2, fake.Requests.Count);
      Assert.Equal(2, r.Chunks);
      Assert.Equal(0, r.FailedChunks);
      Assert.Equal(2000, r.InputTokens);
      Assert.False(r.FromCache);
      Assert.Equal("AI grouping: 2 of 2 chunks", progress.LastText);
      Assert.Contains("15 seconds", fake.Requests[0].system);

      bool[] joins = AiGrouper.ApplyToLines(r, lines);
      Assert.Equal(Enumerable.Range(0, 20).Where(i => i % 2 == 0), Enumerable.Range(0, 20).Where(i => joins[i]));
      Assert.Equal("pair", lines[0].GroupNote);
      Assert.Null(lines[1].GroupNote);
      Assert.Equal("pair", lines[18].GroupNote);

      // second run: no request
      AiGroupingResult again = await AiGrouper.GroupAsync(lines, Limits(), Options(scope), null, CancellationToken.None);
      Assert.Equal(2, fake.Requests.Count);
      Assert.True(again.FromCache);
      Assert.Equal(r.KeptJoins, again.KeptJoins);
      Assert.Equal("pair", again.Notes[0]);

      // force refresh: asks again
      await AiGrouper.GroupAsync(lines, Limits(), Options(scope, force: true), null, CancellationToken.None);
      Assert.Equal(4, fake.Requests.Count);
    }

    [Fact]
    public async Task Grouper_RepairsOverlongAnswers()
    {
      using var scope = new TestScope();
      var lines = Episode(20, 10, gapSeconds: 20); // each chunk spans ~12 s of lines
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll());
      using var _ = fake.Install();
      AiGroupingResult r = await AiGrouper.GroupAsync(lines, Limits(8_000), Options(scope), null, CancellationToken.None);
      int[] kept = SnippetGrouping.KeptIndices(lines);
      foreach ((int first, int last) in SnippetGrouping.JoinsToRanges(r.KeptJoins, kept.Length))
        Assert.True(SnippetGrouping.TrimmedDurationMs(lines, kept[first], kept[last], Limits(8_000)) <= 8_000);
      Assert.Contains(r.KeptJoins, j => j);
    }

    [Fact]
    public async Task Grouper_FailedChunkFallsBackToRules_AndIsNotCached()
    {
      using var scope = new TestScope();
      var lines = Episode(20, 10);
      int calls = 0;
      var fake = new FakeChatProvider(answer: (s, u) =>
      {
        if (Interlocked.Increment(ref calls) == 1) throw new ProviderException("fake", 500, "boom");
        return FakeChatProvider.JoinAll("ai")(s, u);
      });
      using var _ = fake.Install();
      AiGroupingResult r = await AiGrouper.GroupAsync(lines, Limits(), Options(scope), null, CancellationToken.None);
      Assert.Equal(1, r.FailedChunks);
      Assert.Equal(2, r.Chunks);
      Assert.False(Directory.Exists(Path.Combine(scope.TempDir, "cache")) && Directory.GetFiles(Path.Combine(scope.TempDir, "cache")).Length > 0);

      AiGroupingResult again = await AiGrouper.GroupAsync(lines, Limits(), Options(scope), null, CancellationToken.None);
      Assert.Equal(0, again.FailedChunks);
      Assert.Equal(4, fake.Requests.Count);
    }

    [Fact]
    public async Task Grouper_NoKey_ThrowsProviderException()
    {
      using var scope = new TestScope();
      string? saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
      try
      {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        ConstantSettings.AnthropicApiKey = "";
        var options = Options(scope);
        options.Model = "claude-sonnet-5";
        var ex = await Assert.ThrowsAsync<ProviderException>(() => AiGrouper.GroupAsync(Episode(4, 2), Limits(), options, null, CancellationToken.None));
        Assert.Contains("No API key", ex.Message);
      }
      finally
      {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
      }
    }

    [Fact]
    public void Estimate_CountsChunksTokensAndPrice()
    {
      using var scope = new TestScope();
      var lines = Episode(20, 10);
      var options = Options(scope);
      options.Model = "claude-sonnet-5";
      AiCostEstimate e = AiGrouper.Estimate(lines, Limits(), options);
      Assert.Equal(2, e.Chunks);
      Assert.True(e.InputTokens > 200);
      Assert.True(e.OutputTokens > 0);
      Assert.NotNull(e.Usd);
      Assert.Contains("2 request(s)", e.Describe());
      Assert.Contains("$", e.Describe());

      options.Model = "unknown-model";
      Assert.Null(AiGrouper.Estimate(lines, Limits(), options).Usd);
    }

    [Fact]
    public void Pricing_LongestPrefixWins()
    {
      Assert.Equal(1.25 + 10, AiPricing.Usd("gpt-5-2025-08-07", 1_000_000, 1_000_000)!.Value, 6);
      Assert.Equal(0.2 + 1.2, AiPricing.Usd("gpt-5.6-luna", 1_000_000, 1_000_000)!.Value, 6);
      Assert.Equal(0.75 + 3.75, AiPricing.Usd("gemini-3.8-flash", 1_000_000, 1_000_000)!.Value, 6);
      Assert.Equal(0.25 + 2, AiPricing.Usd("gpt-5-mini", 1_000_000, 1_000_000)!.Value, 6);
      Assert.Equal(5 + 25, AiPricing.Usd("claude-opus-5", 1_000_000, 1_000_000)!.Value, 6);
      Assert.Null(AiPricing.Usd("llama", 1, 1));
    }

    [Fact]
    public void Options_FromSettings_ReadTheProject()
    {
      using var scope = new TestScope();
      Settings.Instance.Snippets.AiModel = "gemini-2.5-flash";
      Settings.Instance.Snippets.ChunkTargetLines = 0;
      Settings.Instance.Snippets.AiExtraInstructions = "comedy";
      AiGroupingOptions o = AiGroupingOptions.FromSettings(forceRefresh: true);
      Assert.Equal("gemini-2.5-flash", o.Model);
      Assert.Equal(0, o.ChunkTargetLines);
      Assert.Equal("comedy", o.ExtraInstructions);
      Assert.True(o.ForceRefresh);
    }
  }
}
