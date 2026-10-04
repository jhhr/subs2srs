using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// <see cref="GoChecks"/>, the checks before a run that Go and <c>subs2srs-cli go</c> share,
  /// one at a time. ffmpeg is an empty file in the Tools Directory unless a test needs the real
  /// one: nothing here runs it but the audio-stream check.
  /// </summary>
  public class GoChecksTests
  {
    /// <summary>Settings that pass every check: an existing output dir, a deck name, ffmpeg "found".</summary>
    private static Settings Passing(TestScope scope)
    {
      string tools = Path.Combine(scope.TempDir, "tools");
      Directory.CreateDirectory(tools);
      File.WriteAllText(Path.Combine(tools, "ffmpeg"), "");
      ConstantSettings.ToolsDir = tools;
      Settings s = Settings.Instance;
      s.OutputDir = scope.OutputDir;
      s.DeckName = "Deck";
      return s;
    }

    private static string[] Errors(Settings s, bool aiGroupingRuns = false)
      => GoChecks.Run(s, 0, aiGroupingRuns).Where(p => p.IsError).Select(p => p.Message).ToArray();

    [Fact]
    public void PassingSettings_GiveNoProblem_AndLeaveTheOutputDirEmpty()
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);

      Assert.Empty(GoChecks.Run(s, 0, aiGroupingRuns: true));
      Assert.Empty(Directory.GetFileSystemEntries(scope.OutputDir));
    }

    // ── output dir and deck name ────────────────────────────────────────

    [Fact]
    public void OutputDir_NotThereYet_PassesAndIsRemovedAgain()
    {
      using var scope = new TestScope(" ä 日本");
      Settings s = Passing(scope);
      string parent = Path.Combine(scope.TempDir, "new 日本");
      s.OutputDir = Path.Combine(parent, "deeper [x]");

      Assert.Empty(Errors(s));
      Assert.False(Directory.Exists(parent), "the check left the folders it created");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputDir_ThatIsAFile_OrUnderOne_IsAnError_WithThePath(bool under)
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);
      string file = Path.Combine(scope.TempDir, "a file.txt");
      File.WriteAllText(file, "keep");
      s.OutputDir = under ? Path.Combine(file, "out") : file;

      string error = Assert.Single(Errors(s));
      Assert.StartsWith($"Cannot write to output directory \"{s.OutputDir}\": ", error);
      Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void EmptyOutputDirAndDeckName_GiveTheGuisMessages()
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);
      s.OutputDir = "";
      s.DeckName = "  ";

      Assert.Equal(new[] { "Please provide Output Directory.", "Please provide Deck Name." }, Errors(s));
    }

    // ── tools ───────────────────────────────────────────────────────────

    [Fact]
    public void Ffmpeg_InNeitherTheToolsDirNorPath_IsAnError_AndHidesTheEncoderCheck()
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);
      string empty = Path.Combine(scope.TempDir, "no tools");
      Directory.CreateDirectory(empty);
      ConstantSettings.ToolsDir = empty;
      s.AnimatedSnapshots.Enabled = true;
      string? path = Environment.GetEnvironmentVariable("PATH");
      try
      {
        Environment.SetEnvironmentVariable("PATH", empty);
        Assert.False(ConstantSettings.IsFFmpegAvailable);

        Assert.Equal(new[] { ConstantSettings.FFmpegMissingMessage }, Errors(s));
      }
      finally
      {
        Environment.SetEnvironmentVariable("PATH", path);
      }
    }

    [Theory]
    [InlineData(AnimatedSnapshotFormat.Webp)]
    [InlineData(AnimatedSnapshotFormat.Avif)]
    public void AnimatedSnapshots_WithoutAnEncoder_IsAnError_OnlyWhenTheyAreOn(AnimatedSnapshotFormat format)
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);
      s.AnimatedSnapshots.Format = format;
      try
      {
        UtilsAnimatedSnapshot.OverrideAvailableEncoders(Array.Empty<string>());
        Assert.Empty(Errors(s));

        s.AnimatedSnapshots.Enabled = true;
        Assert.Equal(new[] { UtilsAnimatedSnapshot.MissingEncoderHint(format) }, Errors(s));

        UtilsAnimatedSnapshot.OverrideAvailableEncoders(new[] { "libwebp", "libsvtav1" });
        Assert.Empty(Errors(s));
      }
      finally
      {
        UtilsAnimatedSnapshot.OverrideAvailableEncoders(null!);
      }
    }

    [Fact]
    public void Claude_NotFound_IsAnError_WhenATerminalModelWillBeAsked()
    {
      using var scope = new TestScope();
      Settings s = Passing(scope);
      s.Snippets.Mode = SnippetMode.AI;
      s.Snippets.AiModel = "terminal-claude-sonnet-5";
      try
      {
        ClaudeCliProvider.ExecutableOverride = "";
        string error = Assert.Single(Errors(s, aiGroupingRuns: true));
        Assert.Contains("terminal-claude-sonnet-5", error);
        Assert.EndsWith(ClaudeCliProvider.NoCliMessage, error);

        // Not asked on this run (Go from the Preview's grouping, the preference off), or not through the CLI.
        Assert.Empty(Errors(s, aiGroupingRuns: false));
        s.Snippets.AiModel = "claude-sonnet-5";
        Assert.Empty(Errors(s, aiGroupingRuns: true));

        s.Snippets.AiModel = "terminal-claude-sonnet-5";
        ClaudeCliProvider.ExecutableOverride = Path.Combine(scope.TempDir, "claude");
        Assert.Empty(Errors(s, aiGroupingRuns: true));
      }
      finally
      {
        ClaudeCliProvider.ExecutableOverride = null;
      }
    }

    // ── audio streams ───────────────────────────────────────────────────

    /// <summary>The test video has one audio stream: stream #1 is missing from both copies, #0 is the same in both.</summary>
    [RequiresFfmpegFact]
    public async Task AudioStream_MissingFromTheVideos_IsAWarning_WhenAudioComesFromThem()
    {
      await TestMedia.EnsureAsync();
      using var scope = new TestScope();
      Settings s = Settings.Instance;
      s.OutputDir = scope.OutputDir;
      s.DeckName = "Deck";
      s.VideoClips.Files = new[] { TestMedia.VideoPath, TestMedia.VideoPath };

      GoProblem warning = Assert.Single(GoChecks.Run(s, 1, false));
      Assert.False(warning.IsError);
      Assert.StartsWith("Audio stream #1 issues:", warning.Message);
      Assert.Contains("Stream not found in episodes: 1, 2", warning.Message);

      Assert.Empty(GoChecks.Run(s, 0, false));
      s.AudioClips.Enabled = false; // nor video clips: no audio is cut from the videos
      Assert.Empty(GoChecks.Run(s, 1, false));
    }

    [Fact]
    public void AudioStreamIndex_IsTheStreamsPositionInTheGuisList()
    {
      using var scope = new TestScope();
      Settings s = Settings.Instance;
      s.VideoClips.AudioStream = new InfoStream("0:2", "1", "Japanese", "aac");
      Assert.Equal(1, GoChecks.AudioStreamIndex(s));
      s.VideoClips.AudioStream = new InfoStream("0:a:0", "0", "", "");
      Assert.Equal(0, GoChecks.AudioStreamIndex(s));
      s.VideoClips.AudioStream = new InfoStream("0:1", "", "", "");
      Assert.Equal(0, GoChecks.AudioStreamIndex(s));
    }
  }
}
