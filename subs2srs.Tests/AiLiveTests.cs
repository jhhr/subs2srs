using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The one test that talks to a real provider. Skipped unless SUBS2SRS_AI_LIVE_MODEL names a
  /// model (e.g. claude-haiku-4-5, gpt-5.6-luna, gemini-2.5-flash-lite) and the provider's key
  /// is in the environment (ANTHROPIC_API_KEY / OPENAI_API_KEY / GEMINI_API_KEY). Run by hand:
  ///   SUBS2SRS_AI_LIVE_MODEL=claude-haiku-4-5 dotnet test --filter FullyQualifiedName~AiLiveTests
  /// Costs a fraction of a cent. Never writes the key anywhere.
  /// </summary>
  public class AiLiveTests
  {
    public const string ModelVariable = "SUBS2SRS_AI_LIVE_MODEL";

    [RequiresEnvFact(ModelVariable)]
    public async Task LiveModel_GroupsAQuestionAndItsAnswer()
    {
      string model = Environment.GetEnvironmentVariable(ModelVariable)!.Trim();
      string? provider = ChatProviders.ProviderFor(model);
      Assert.NotNull(provider);
      Assert.True(ChatProviders.ApiKeyFor(provider!) != "", $"{ChatProviders.EnvVarFor(provider!)} is not set");

      using var scope = new TestScope();
      var lines = new List<InfoCombined>
      {
        SnippetGroupingTests.Line(1.0, 2.0, "どこに行くの？", actor: "A"),
        SnippetGroupingTests.Line(2.4, 3.4, "駅だよ。", actor: "B"),
        SnippetGroupingTests.Line(30.0, 31.0, "今日はいい天気ですね。"),
        SnippetGroupingTests.Line(60.0, 61.5, "さようなら。"),
      };
      var options = new AiGroupingOptions
      {
        Model = model,
        ChunkTargetLines = 0,
        CacheDir = Path.Combine(scope.TempDir, "cache"),
        ForceRefresh = true,
        Concurrency = 1,
        Rpm = 0,
      };

      AiGroupingResult result = await AiGrouper.GroupAsync(lines, new SnippetLimits(15_000, 500, 0), options, null, CancellationToken.None);

      Assert.Equal(0, result.FailedChunks);
      Assert.True(result.InputTokens > 0, "no input token count reported");
      Assert.True(result.OutputTokens > 0, "no output token count reported");
      // The only defensible group is the question and its answer; the two later lines are 30 s apart.
      Assert.True(result.KeptJoins[0], "expected the question and its answer to be joined");
      Assert.False(result.KeptJoins[1]);
      Assert.False(result.KeptJoins[2]);
    }
  }
}
