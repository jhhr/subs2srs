using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using subs2srs.Cli;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <c>subs2srs-cli go</c> on a project that groups snippets by AI: the pre-pass that groups
  /// every episode before the run (<see cref="AiPrePass"/>), the table's AI column, the usage
  /// limit, and the dry run's cached column. No network and never <c>claude</c>: the model is
  /// <see cref="FakeChatProvider"/>, or the <c>claude</c> transport with a scripted process
  /// runner (<see cref="ClaudeCliProvider.RunnerOverride"/>). Runs make the TSV only (the
  /// videos are never read), but for the one that checks which video a card's media come from.
  /// </summary>
  public class CliAiPrePassTests : IDisposable
  {
    public CliAiPrePassTests()
    {
      ClaudeCli.ResetProbeCache();
      ClaudeCliProvider.Reset();
    }

    public void Dispose()
    {
      ClaudeCliProvider.RunnerOverride = null;
      ClaudeCliProvider.HelpOverride = null;
      ClaudeCliProvider.ExecutableOverride = null;
      ClaudeCli.ResetProbeCache();
      ClaudeCliProvider.Reset();
    }

    /// <summary>
    /// A project grouping by AI through <paramref name="model"/>, with two chunks per episode of
    /// <see cref="Episode"/>: chunks break at gaps of 3 s or more and hold two lines. Without
    /// <paramref name="media"/>, no audio or snapshots: the run writes the TSV only.
    /// </summary>
    private static string SaveAiProject(TestScope scope, string model = "fake-model", bool media = false)
      => CliTests.SaveRunProject(scope, s =>
      {
        s.Snippets.Mode = SnippetMode.AI;
        s.Snippets.AiModel = model;
        s.Snippets.MaxSnippetSeconds = 3;
        s.Snippets.ChunkTargetLines = 2;
        s.AudioClips.Enabled = media;
        s.Snapshots.Enabled = media;
      });

    private static string Name(int n) => $"[Grp] 進撃 - {n:00}";

    /// <summary>
    /// Episode <paramref name="n"/> in the season folder: its video (an empty file, or the test
    /// video) and the harness's dialogue as its .ja and .en files, each line starting "E<n> ", so
    /// every episode asks the model something else (the cache key is the content) and a card
    /// shows whose text it carries. Lines 3 and 4 come a second later than the harness has them:
    /// the 3.6 s gap before them makes two chunks, lines 1-2 and 3-4.
    /// </summary>
    private static void Episode(string season, int n, bool video = false)
    {
      string mkv = Path.Combine(season, Name(n) + ".mkv");
      if (video) File.Copy(TestMedia.VideoPath, mkv, overwrite: true);
      else File.WriteAllText(mkv, "");
      string subs = Path.Combine(season, EpisodeList.SubsFolder);
      File.WriteAllText(Path.Combine(subs, Name(n) + ".ja.srt"), Srt(n, TestMedia.DialogueLines.Select(l => l.text).ToArray()), new UTF8Encoding(false));
      File.WriteAllText(Path.Combine(subs, Name(n) + ".en.srt"), Srt(n, TestMedia.DialogueTranslations), new UTF8Encoding(false));
    }

    private static string Srt(int n, string[] texts)
    {
      static string Time(double seconds)
      {
        var t = TimeSpan.FromSeconds(seconds);
        return FormattableString.Invariant($"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
      }
      var sb = new StringBuilder();
      for (int i = 0; i < texts.Length; i++)
      {
        (double start, double end, _) = TestMedia.DialogueLines[i];
        double later = i >= 2 ? 1.0 : 0.0;
        sb.Append(i + 1).Append("\r\n")
          .Append(Time(start + later)).Append(" --> ").Append(Time(end + later)).Append("\r\n")
          .Append($"E{n} {texts[i]}").Append("\r\n\r\n");
      }
      return sb.ToString();
    }

    private static string[] TsvLines(string tsv)
      => File.ReadAllLines(tsv, Encoding.UTF8).Where(l => l.Length > 0).ToArray();

    private static string[][] Rows(CliTests.Result r) => r.Lines.Select(EpisodeListTests.Cells).ToArray();

    /// <summary>The cells of one column of a table's rows, the header left out.</summary>
    private static string[] Column(CliTests.Result r, int column, int rows)
      => Rows(r).Skip(1).Take(rows).Select(cells => cells[column]).ToArray();

    // ── the fake model ──────────────────────────────────────────────────

    /// <summary>
    /// Every episode grouped by the model before the run, which then starts from those lines
    /// and groupings (no Combine subs, no AI step of its own, though the On Go preference is
    /// on); the cards follow the model. A second go and dry run find everything cached.
    /// </summary>
    [Fact]
    public async Task Go_GroupsEveryEpisodeFirst_TheCardsFollowTheModel_AndACachedEpisodeIsNotAskedAgain()
    {
      using var scope = new TestScope(" ä 日本");
      CliTests.FakeFfmpeg(scope);
      string project = SaveAiProject(scope);
      string season = CliTests.MakeSeason(scope);
      for (int n = 1; n <= 3; n++) Episode(season, n);
      ConstantSettings.AiGroupingOnGo = true; // kept by --no-prefs
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll("scene"));
      using var _ = fake.Install();
      string[] go = { "go", "--project", project, "--season", season, "--no-prefs" };
      string[] dryRun = go.Append("--dry-run").ToArray();

      CliTests.Result dry = await CliTests.Run(dryRun);
      Assert.True(dry.Code == CliOptions.ExitOk, dry.ToString());
      Assert.Equal(new[] { "#", "Video", "Subs1", "Subs2", "AI", "Status" }, Rows(dry)[0]);
      Assert.Equal(new[] { "not cached", "not cached", "not cached" }, Column(dry, 4, 3));
      Assert.Empty(fake.Requests);

      CliTests.Result r = await CliTests.Run(go);

      Assert.True(r.Code == CliOptions.ExitOk, r.ToString());
      Assert.Equal(6, fake.Requests.Count); // two chunks per episode
      string[][] rows = Rows(r);
      Assert.Equal(new[] { "#", "Episode", "AI", "Status", "Cards" }, rows[0]);
      for (int n = 1; n <= 3; n++)
        Assert.Equal(new[] { $"{n}", Name(n), "grouped", "done", "2" }, rows[n]);
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");
      Assert.Equal($"season TSV: {tsv} (3 of 3 episodes); exit 0", r.Lines[4]);
      string[] cards = TsvLines(tsv);
      Assert.Equal(new[] { "Show_1", "Show_1", "Show_2", "Show_2", "Show_3", "Show_3" }, CliTests.Tags(tsv));
      Assert.Contains("E2 Where are you going?<br>E2 To the station.", cards[2]);
      Assert.Contains("E2 See you later.<br>E2 Bye.", cards[3]);
      Assert.Contains("E2 Bis später.<br>E2 Tschüss.", cards[3]);
      string[] err = r.Stderr.Split(Environment.NewLine);
      Assert.Contains("AI grouping: episode 2 (2 of 3)", err);
      Assert.Contains("Step 1 of 3: Group into snippets", err); // 5 + 1 with its own first steps and AI step
      Assert.DoesNotContain(err, l => l.StartsWith("warning: ", StringComparison.Ordinal));

      CliTests.Result again = await CliTests.Run(go);
      Assert.True(again.Code == CliOptions.ExitOk, again.ToString());
      Assert.Equal(6, fake.Requests.Count);
      Assert.Equal(new[] { "cached", "cached", "cached" }, Column(again, 2, 3));
      Assert.Equal(cards, TsvLines(tsv));

      CliTests.Result dryAgain = await CliTests.Run(dryRun);
      Assert.Equal(new[] { "cached", "cached", "cached" }, Column(dryAgain, 4, 3));
      Assert.Equal(6, fake.Requests.Count);
    }

    /// <summary>
    /// A chunk whose request fails for another reason than the usage limit: the rules group
    /// it, the episode keeps its cards with a warning, and, not being cached, it is asked
    /// again by the next go (the others come from the cache).
    /// </summary>
    [Fact]
    public async Task Go_WhenAChunkFails_KeepsTheEpisodeWithThatChunkByTheRules_AndWarns()
    {
      using var scope = new TestScope();
      CliTests.FakeFfmpeg(scope);
      string project = SaveAiProject(scope);
      string season = CliTests.MakeSeason(scope);
      for (int n = 1; n <= 3; n++) Episode(season, n);
      Func<string, string, string> joinAll = FakeChatProvider.JoinAll();
      var fake = new FakeChatProvider(answer: (system, user) => user.Contains("E2 See you later.")
        ? throw new ProviderException("fake", 500, "the server broke")
        : joinAll(system, user));
      using var _ = fake.Install();
      string[] go = { "go", "--project", project, "--season", season, "--no-prefs" };

      CliTests.Result r = await CliTests.Run(go);

      Assert.True(r.Code == CliOptions.ExitOk, r.ToString());
      string[][] rows = Rows(r);
      Assert.Equal(new[] { "1", Name(1), "grouped", "done", "2" }, rows[1]);
      // Lines 1-2 joined by the model; the rules leave 3 and 4 apart (3 ends a sentence).
      Assert.Equal(new[] { "2", Name(2), "1/2 by rules", "done", "3" }, rows[2]);
      Assert.Equal(new[] { "3", Name(3), "grouped", "done", "2" }, rows[3]);
      Assert.Contains(r.Stderr.Split(Environment.NewLine),
        l => l.StartsWith("warning: episode 2: 1 of 2 chunks grouped by the rules, their requests failed", StringComparison.Ordinal));
      Assert.Equal(new[] { "Show_1", "Show_1", "Show_2", "Show_2", "Show_2", "Show_3", "Show_3" },
        CliTests.Tags(Path.Combine(scope.OutputDir, "Show.tsv")));

      fake.Answer = joinAll;
      fake.Requests.Clear();
      CliTests.Result again = await CliTests.Run(go);

      Assert.True(again.Code == CliOptions.ExitOk, again.ToString());
      Assert.Equal(2, fake.Requests.Count);
      Assert.All(fake.Requests, q => Assert.Contains("E2 ", q.user));
      Assert.Equal(new[] { "cached", "grouped", "cached" }, Column(again, 2, 3));
    }

    /// <summary>The pre-pass skips every episode: the table, exit 3, and the run never starts.</summary>
    [Fact]
    public async Task Go_WhenThePrePassSkipsEveryEpisode_PrintsTheTable_Exits3_AndMakesNothing()
    {
      using var scope = new TestScope();
      CliTests.FakeFfmpeg(scope);
      string project = SaveAiProject(scope);
      string season = CliTests.MakeSeason(scope);
      Episode(season, 1);
      Episode(season, 2);
      var fake = new FakeChatProvider { Failure = new ProviderException("fake", 401, "bad key") };
      using var _ = fake.Install();

      CliTests.Result r = await CliTests.Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      string[][] rows = Rows(r);
      Assert.Equal("failed", rows[1][2]);
      Assert.StartsWith("skipped: AI grouping failed: ", rows[1][3]);
      Assert.Contains("bad key", rows[2][3]);
      Assert.Equal("season TSV: not written (0 of 2 episodes); exit 3", r.Lines[3]);
      Assert.DoesNotContain("Step 1 of", r.Stderr);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    /// <summary>
    /// Subtitles without a line: go stops with exit 1 at the pre-pass's first step, before
    /// asking or making anything; a dry run cannot tell what is cached, says so, and keeps its
    /// exit code (a dry run does not read the subtitles in the other modes either).
    /// </summary>
    [Fact]
    public async Task Go_WithSubtitlesWithoutALine_Exits1_BeforeAnyWork_AndADryRunCannotTellWhatIsCached()
    {
      using var scope = new TestScope();
      CliTests.FakeFfmpeg(scope);
      string project = SaveAiProject(scope);
      string season = CliTests.MakeSeason(scope, "a.mkv", "s2s/a.ja.srt", "s2s/a.en.srt");
      var fake = new FakeChatProvider(answer: FakeChatProvider.JoinAll());
      using var _ = fake.Install();

      CliTests.Result r = await CliTests.Run("go", "--project", project, "--season", season, "--no-prefs");

      Assert.True(r.Code == CliOptions.ExitError, r.ToString());
      Assert.Contains("subs2srs-cli: Combine subs failed: no lines of dialog could be parsed from the subtitle files", r.Stderr);
      Assert.Equal("", r.Stdout);
      Assert.Empty(fake.Requests);
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));

      CliTests.Result dry = await CliTests.Run("go", "--project", project, "--season", season, "--no-prefs", "--dry-run");

      Assert.True(dry.Code == CliOptions.ExitOk, dry.ToString());
      Assert.Equal("unknown", Rows(dry)[1][4]);
      Assert.Contains("subs2srs-cli: cannot tell which AI groupings are cached: Combine subs failed: ", dry.Stderr);
    }

    // ── the claude transport and the usage limit ────────────────────────

    private const string Help = "-p --model <m> --output-format <f> --json-schema <s> --system-prompt <p> --safe-mode";

    /// <summary>Which chunk a request is: "E2a" for lines 1-2 of episode 2, "E2b" for lines 3-4.</summary>
    private static string ChunkOf(string prompt)
    {
      for (int n = 1; n <= 9; n++)
      {
        if (prompt.Contains($"E{n} Where are you going?")) return $"E{n}a";
        if (prompt.Contains($"E{n} See you later.")) return $"E{n}b";
      }
      return "?";
    }

    /// <summary>A <c>claude</c> result joining the chunk's lines into one snippet.</summary>
    private static CliProcessResult JoinAll(string prompt)
    {
      List<int> ids = FakeChatProvider.LineIds(prompt);
      return new CliProcessResult
      {
        ExitCode = 0,
        Stdout = FormattableString.Invariant(
          $"{{\"is_error\":false,\"result\":\"ok\",\"structured_output\":{{\"snippets\":[{{\"first\":{ids[0]},\"last\":{ids[ids.Count - 1]}}}]}},")
          + "\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}",
      };
    }

    private static readonly CliProcessResult LimitReached = new CliProcessResult
    {
      ExitCode = 1,
      Stdout = "{\"is_error\":true,\"subtype\":\"error\",\"result\":\"You've hit your session limit, resets 3pm\"}",
    };

    /// <summary>
    /// The usage limit is reached during episode 2: by its second chunk while its first one is
    /// answered (the grouper returns with one failed chunk, which the rules would fill in), or
    /// by both (the grouper throws). Episode 2 is skipped either way, and episode 3 without a
    /// request. The TSV holds episode 1. The next go (a new process: the limit has reset) asks
    /// for 2 and 3 only, 1 coming from the cache, and rewrites the TSV with all three.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Go_AtTheUsageLimit_SkipsThatEpisodeAndTheRestWithoutAsking_AndTheNextRunDoesOnlyThose(bool bothChunks)
    {
      using var scope = new TestScope();
      CliTests.FakeFfmpeg(scope);
      // Both chunks of an episode in flight at once (the defaults allow more).
      ConstantSettings.AiMaxConcurrentRequests = 2;
      ConstantSettings.ClaudeCliMaxConcurrentProcesses = 2;
      string project = SaveAiProject(scope, "terminal-claude-sonnet-5");
      string season = CliTests.MakeSeason(scope);
      for (int n = 1; n <= 3; n++) Episode(season, n);
      var asked = new List<string>();
      bool limitOnEpisode2 = true;
      var e2aRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      ClaudeCliProvider.ExecutableOverride = "/no/such/claude";
      ClaudeCliProvider.HelpOverride = _ => Help;
      ClaudeCliProvider.RunnerOverride = async (request, ct) =>
      {
        string chunk = ChunkOf(request.Prompt);
        lock (asked) asked.Add(chunk);
        if (limitOnEpisode2 && bothChunks && chunk.StartsWith("E2", StringComparison.Ordinal))
          return LimitReached;
        if (limitOnEpisode2 && chunk == "E2a") e2aRunning.TrySetResult();
        if (limitOnEpisode2 && chunk == "E2b")
        {
          // E2a has passed its limit check: it is answered, this one hits the limit.
          await e2aRunning.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
          return LimitReached;
        }
        return JoinAll(request.Prompt);
      };
      string[] go = { "go", "--project", project, "--season", season, "--no-prefs" };
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");

      CliTests.Result r = await CliTests.Run(go);

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      if (bothChunks)
      {
        // The first to hit the limit fails the other at once, whether it was waiting or not yet sent.
        Assert.Equal(new[] { "E1a", "E1b" }, asked.Where(c => c.StartsWith("E1", StringComparison.Ordinal)).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Contains(asked, c => c.StartsWith("E2", StringComparison.Ordinal));
        Assert.DoesNotContain(asked, c => c.StartsWith("E3", StringComparison.Ordinal));
      }
      else
        Assert.Equal(new[] { "E1a", "E1b", "E2a", "E2b" }, asked.OrderBy(c => c, StringComparer.Ordinal));
      string[][] rows = Rows(r);
      Assert.Equal(new[] { "1", Name(1), "grouped", "done", "2" }, rows[1]);
      Assert.Equal(new[] { "2", Name(2), "usage limit", "skipped: the Claude usage limit was reached", "-" }, rows[2]);
      Assert.Equal(new[] { "3", Name(3), "usage limit", "skipped: the Claude usage limit was reached", "-" }, rows[3]);
      Assert.Equal($"season TSV: {tsv} (1 of 3 episodes); exit 3", r.Lines[4]);
      Assert.Contains("subs2srs-cli: the Claude usage limit was reached (You've hit your session limit, resets 3pm); 2 episode(s) skipped.", r.Stderr);
      Assert.Equal(new[] { "Show_1", "Show_1" }, CliTests.Tags(tsv));

      ClaudeCliProvider.Reset();
      limitOnEpisode2 = false;
      asked.Clear();
      CliTests.Result again = await CliTests.Run(go);

      Assert.True(again.Code == CliOptions.ExitOk, again.ToString());
      Assert.Equal(new[] { "E2a", "E2b", "E3a", "E3b" }, asked.OrderBy(c => c, StringComparer.Ordinal));
      Assert.Equal(new[] { "cached", "grouped", "grouped" }, Column(again, 2, 3));
      Assert.Equal($"season TSV: {tsv} (3 of 3 episodes); exit 0", again.Lines[4]);
      Assert.Equal(new[] { "Show_1", "Show_1", "Show_2", "Show_2", "Show_3", "Show_3" }, CliTests.Tags(tsv));
    }

    // ── dropping an episode ─────────────────────────────────────────────

    /// <summary>
    /// Episode 2 has no AI grouping and is dropped from the run: episode 3's cards carry its
    /// text, its number and media cut from its own video. Episode 2's "video" is not a video,
    /// so media cut for a card from the wrong one fails the run.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Go_WithEpisode2Dropped_Episode3sCardsCarryItsTextAndItsOwnMedia()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope(" ä 日本");
      string project = SaveAiProject(scope, media: true);
      string season = CliTests.MakeSeason(scope);
      Episode(season, 1, video: true);
      Episode(season, 2);
      File.WriteAllText(Path.Combine(season, Name(2) + ".mkv"), "This is not a video.");
      Episode(season, 3, video: true);
      Func<string, string, string> joinAll = FakeChatProvider.JoinAll();
      var fake = new FakeChatProvider(answer: (system, user) => user.Contains("E2 ")
        ? throw new ProviderException("fake", 401, "bad key")
        : joinAll(system, user));
      using var _ = fake.Install();

      // --yes: the checks warn that episode 2's "video" has no audio stream.
      CliTests.Result r = await CliTests.Run("go", "--project", project, "--season", season, "--no-prefs", "--yes");

      Assert.True(r.Code == CliOptions.ExitSkipped, r.ToString());
      string[][] rows = Rows(r);
      Assert.Equal(new[] { "1", Name(1), "grouped", "done", "2" }, rows[1]);
      Assert.Equal("failed", rows[2][2]);
      Assert.Equal(new[] { "3", Name(3), "grouped", "done", "2" }, rows[3]);
      string tsv = Path.Combine(scope.OutputDir, "Show.tsv");
      Assert.Equal($"season TSV: {tsv} (2 of 3 episodes); exit 3", r.Lines[4]);

      string mediaDir = Path.Combine(scope.OutputDir, "Show.media");
      string[] media = Directory.GetFiles(mediaDir).Select(f => Path.GetFileName(f)).ToArray();
      Assert.Equal(8, media.Length); // an mp3 and a jpg per card
      Assert.All(media, m => Assert.True(m.StartsWith("Show_1_", StringComparison.Ordinal) || m.StartsWith("Show_3_", StringComparison.Ordinal), m));
      string[] episode3 = TsvLines(tsv).Where(l => l.StartsWith("Show_3\t", StringComparison.Ordinal)).ToArray();
      Assert.Equal(2, episode3.Length);
      Assert.Contains("E3 Where are you going?<br>E3 To the station.", episode3[0]);
      Assert.Contains("E3 Wohin gehst du?<br>E3 Zum Bahnhof.", episode3[0]);
      Assert.Contains("E3 See you later.<br>E3 Bye.", episode3[1]);
      foreach (string card in episode3)
      {
        string[] named = media.Where(m => card.Contains(m, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, named.Length);
        Assert.All(named, m => Assert.StartsWith("Show_3_", m));
        Assert.All(named, m => Assert.True(new FileInfo(Path.Combine(mediaDir, m)).Length > 0, m));
      }
    }

    /// <summary>
    /// Dropping keeps every per-episode list in step, also in pattern mode, where the numbers
    /// come from the start number: they are made explicit, the padding stays that of every
    /// episode, and a list of another length (a pattern the run does not use) is left alone.
    /// </summary>
    [Fact]
    public void KeepEpisodes_DropsTheEpisodeFromEveryPerEpisodeList_AndKeepsTheOthersNumbers()
    {
      using var scope = new TestScope();
      Settings s = Settings.Instance;
      s.EpisodeStartNumber = 5;
      s.Subs[0].Files = new[] { "a.ja", "b.ja", "c.ja" };
      s.Subs[1].Files = new[] { "a.en", "b.en", "c.en" };
      s.VideoClips.Files = new[] { "a.mkv", "b.mkv" };
      s.AudioClips.Files = new[] { "a.mp3", "b.mp3", "c.mp3" };

      AiPrePass.KeepEpisodes(s, new[] { true, false, true });

      Assert.Equal(new[] { "a.ja", "c.ja" }, s.Subs[0].Files);
      Assert.Equal(new[] { "a.en", "c.en" }, s.Subs[1].Files);
      Assert.Equal(new[] { "a.mp3", "c.mp3" }, s.AudioClips.Files);
      Assert.Equal(new[] { "a.mkv", "b.mkv" }, s.VideoClips.Files);
      Assert.Equal(new[] { 5, 7 }, s.EpisodeNumbers);
      Assert.Equal(3, s.EpisodeCountForNames);
      Assert.Equal(7, s.EpisodeNumber(1));
    }
  }
}
