using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The AI grouping pass inside the real pipeline, with <see cref="FakeChatProvider"/> standing in
  /// for the network (plan §6 e2e). Media is the bundled test clip; snapshots and audio are produced.
  /// </summary>
  public class AiGroupingE2ETests
  {
    private sealed class StepRecorder : NullProgressReporter
    {
      public List<string> Steps { get; } = new();
      public override void NextStep(int step, string description)
      {
        base.NextStep(step, description);
        Steps.Add(description);
      }
    }

    private static void ConfigureAi(TestScope scope, bool onGo)
    {
      SnippetE2ETests.Configure(scope, SnippetMode.AI, gapRemoval: true);
      Settings.Instance.Snippets.AiModel = "fake-model";
      Settings.Instance.Snippets.ChunkTargetLines = 0;
      ConstantSettings.AiGroupingOnGo = onGo;
    }

    [RequiresFfmpegFact]
    public async Task AiOnGo_GroupsWithTheModel_AndAddsAStep()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      ConfigureAi(scope, onGo: true);
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll("one scene"));
      using var _ = fake.Install();
      var progress = new StepRecorder();

      await new SubsProcessor().StartAsync(progress);

      Assert.Empty(scope.Msgs.Errors);
      Assert.Single(fake.Requests);
      Assert.Contains("AI grouping", progress.Steps);
      Assert.Equal(progress.Steps.Count, progress.StepsTotal);
      Assert.True(progress.Steps.IndexOf("AI grouping") < progress.Steps.IndexOf("Group into snippets"));

      var lines = SnippetE2ETests.TsvLines(scope);
      Assert.Single(lines); // all four lines in one snippet
      Assert.Contains("Where are you going?<br>To the station.<br>See you later.<br>Bye.", lines[0]);
      Assert.Single(Directory.GetFiles(SnippetE2ETests.MediaDir(scope), "*.jpg"));

      // the answer is cached: a second Go asks nothing
      await new SubsProcessor().StartAsync(new NullProgressReporter());
      Assert.Single(fake.Requests);
      Assert.NotEmpty(Directory.GetFiles(Path.Combine(scope.TempDir, "ai-cache"), "*.json"));
    }

    /// <summary>
    /// The grouper decides on subs1 alone (nothing of subs2 reaches the model), and the subs2 text
    /// follows the grouping in the card because the snippet merges both tracks with the same parts.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task AiOnGo_SendsSubs1Only_AndSubs2FollowsTheGrouping()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      ConfigureAi(scope, onGo: true);
      string srt2 = TestMedia.WriteDialogueTranslationSrt(scope.TempDir);
      Settings.Instance.Subs[1].FilePattern = srt2;
      Settings.Instance.Subs[1].Files = UtilsSubs.getSubsFiles(srt2).ToArray();
      Settings.Instance.Subs[1].Encoding = "utf-8";
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll("one scene"));
      using var _ = fake.Install();

      await new SubsProcessor().StartAsync(new NullProgressReporter());

      Assert.Empty(scope.Msgs.Errors);
      (string system, string user) request = Assert.Single(fake.Requests);
      Assert.DoesNotContain("t2", request.system);
      Assert.DoesNotContain("\"t2\"", request.user);
      foreach (string translation in TestMedia.DialogueTranslations)
      {
        Assert.DoesNotContain(translation, request.user);
        Assert.DoesNotContain(translation, request.system);
      }
      Assert.Contains("Where are you going?", request.user);

      string row = Assert.Single(SnippetE2ETests.TsvLines(scope));
      Assert.Contains("Where are you going?<br>To the station.<br>See you later.<br>Bye.", row);
      Assert.Contains("Wohin gehst du?<br>Zum Bahnhof.<br>Bis später.<br>Tschüss.", row);
    }

    [RequiresFfmpegFact]
    public async Task AiWithoutOnGo_FallsBackToRules_WithoutCallingTheModel()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      ConfigureAi(scope, onGo: false);
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll());
      using var _ = fake.Install();
      var progress = new StepRecorder();

      await new SubsProcessor().StartAsync(progress);

      Assert.Empty(scope.Msgs.Errors);
      Assert.Empty(fake.Requests);
      Assert.DoesNotContain("AI grouping", progress.Steps);
      var lines = SnippetE2ETests.TsvLines(scope);
      Assert.Equal(3, lines.Length); // what the rules produce on this clip
      Assert.Contains("Where are you going?<br>To the station.", lines[0]);
    }

    [RequiresFfmpegFact]
    public async Task AiOnGo_ProviderFailure_FallsBackToRules()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      ConfigureAi(scope, onGo: true);
      var fake = new FakeChatProvider { Failure = new ProviderException("fake", 401, "bad key") };
      using var _ = fake.Install();

      await new SubsProcessor().StartAsync(new NullProgressReporter());

      Assert.Empty(scope.Msgs.Errors);
      Assert.Single(fake.Requests);
      var lines = SnippetE2ETests.TsvLines(scope);
      Assert.Equal(3, lines.Length);
      Assert.Empty(Directory.Exists(Path.Combine(scope.TempDir, "ai-cache")) ? Directory.GetFiles(Path.Combine(scope.TempDir, "ai-cache")) : Array.Empty<string>());
    }

    [RequiresFfmpegFact]
    public async Task PreviewJoins_SkipTheAiStep_EvenWithOnGo()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      ConfigureAi(scope, onGo: true);
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll());
      using var _ = fake.Install();

      var wv = new WorkerVars(null, Path.Combine(scope.TempDir, "preview"), WorkerVars.SubsProcessingType.Preview);
      Directory.CreateDirectory(wv.MediaDir);
      var worker = new WorkerSubs();
      wv.CombinedAll = worker.combineAllSubs(wv, new NullProgressReporter());
      wv.CombinedAll = worker.inactivateLines(wv, new NullProgressReporter());
      bool[] joins = { false, false, true, false };
      var progress = new StepRecorder();

      await new SubsProcessor().StartAsync(progress, wv.CombinedAll, new() { joins });

      Assert.Empty(scope.Msgs.Errors);
      Assert.Empty(fake.Requests);
      Assert.DoesNotContain("AI grouping", progress.Steps);
      Assert.Equal(3, SnippetE2ETests.TsvLines(scope).Length);
    }
  }
}
