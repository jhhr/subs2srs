using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// The pure helpers behind <c>terminal-</c> models (plan <c>plans/claude-cli-plan.md</c> §7):
  /// model mapping, finding the executable, probing the flags, building the argument list,
  /// classifying a finished process and picking the model's last answer out of its text.
  /// No process is ever started.
  /// </summary>
  public class ClaudeCliTests
  {
    private static JsonElement Schema(string json = "{\"type\":\"object\"}")
      => JsonDocument.Parse(json).RootElement.Clone();

    private static ClaudeCliFeatures AllFlags() => ClaudeCliFeatures.FromHelp(
      "-p, --print  --model <m>  --output-format <f>  --json-schema <s>  --system-prompt <p>  "
      + "--system-prompt-file <f>  --tools <t>  --no-session-persistence  --safe-mode  --effort <e>");

    // ── model names ───────────────────────────────────────────────────

    [Theory]
    [InlineData("terminal-claude-sonnet-5", true)]
    [InlineData("TERMINAL-claude-sonnet-5", true)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("gpt-5", false)]
    [InlineData("", false)]
    public void IsTerminalModel_LooksAtThePrefix(string model, bool expected)
      => Assert.Equal(expected, ClaudeCli.IsTerminalModel(model));

    [Theory]
    [InlineData("terminal-claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("terminal-haiku", "haiku")]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    public void CliModel_StripsThePrefix(string model, string expected)
      => Assert.Equal(expected, ClaudeCli.CliModel(model));

    [Theory]
    [InlineData("terminal-claude-sonnet-5", true)]
    [InlineData("terminal-claude-haiku-4-5-20251001", true)]
    [InlineData("terminal-sonnet", true)]
    [InlineData("terminal-opus", true)]
    [InlineData("terminal-gpt-5", false)]      // the CLI serves Claude only
    [InlineData("terminal-", false)]
    [InlineData("claude-sonnet-5", false)]     // not a terminal model at all
    public void IsSupportedModel_AcceptsClaudeIdsAndAliases(string model, bool expected)
      => Assert.Equal(expected, ClaudeCli.IsSupportedModel(model));

    // ── environment ───────────────────────────────────────────────────

    [Fact]
    public void ApplyEnvironment_HidesTheApiKeyAndTurnsOffThinking()
    {
      var env = new Dictionary<string, string?>
      {
        ["ANTHROPIC_API_KEY"] = "sk-secret",
        ["ANTHROPIC_AUTH_TOKEN"] = "tok",
        ["ANTHROPIC_BASE_URL"] = "https://proxy",
        ["PATH"] = "/usr/bin",
      };
      ClaudeCli.ApplyEnvironment(env);

      // A key would make the CLI bill the API instead of the subscription.
      Assert.Null(env["ANTHROPIC_API_KEY"]);
      Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
      Assert.Null(env["ANTHROPIC_BASE_URL"]);
      Assert.Equal("0", env["MAX_THINKING_TOKENS"]);
      Assert.Equal("1", env["DISABLE_TELEMETRY"]);
      Assert.Equal("1", env["DISABLE_ERROR_REPORTING"]);
      Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
      Assert.Equal("/usr/bin", env["PATH"]); // everything else is inherited untouched
    }

    // ── finding the executable ────────────────────────────────────────

    [Fact]
    public void Find_ConfiguredPathWins()
    {
      Assert.Equal(@"C:\tools\claude.exe",
        ClaudeCli.Find(@"  C:\tools\claude.exe  ", _ => @"C:\other\claude.exe"));
    }

    [Fact]
    public void Find_ExeOnPathIsUsedAsIs()
    {
      Assert.Equal(@"C:\bin\claude.exe", ClaudeCli.Find("", _ => @"C:\bin\claude.exe"));
    }

    [Fact]
    public void Find_NothingOnPath_IsNull()
    {
      Assert.Null(ClaudeCli.Find(null, _ => null));
      Assert.Null(ClaudeCli.Find("   ", _ => ""));
    }

    [Fact]
    public void Find_NpmShim_ResolvesToTheNativeBinaryBesideIt()
    {
      string root = TempDir();
      try
      {
        string shim = Path.Combine(root, "claude.cmd");
        File.WriteAllText(shim, "@echo off");
        string bin = Path.Combine(root, "node_modules", "@anthropic-ai", "claude-code", "bin");
        Directory.CreateDirectory(bin);
        string native = Path.Combine(bin, "claude.exe");
        File.WriteAllText(native, "");

        Assert.Equal(native, ClaudeCli.Find(null, _ => shim));
      }
      finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_ShimWithNoNativeBinary_IsNull()
    {
      string root = TempDir();
      try
      {
        string shim = Path.Combine(root, "claude.ps1");
        File.WriteAllText(shim, "# shim");
        // Running a shim goes through a shell that mangles the schema quoting: worse than nothing.
        Assert.Null(ClaudeCli.Find(null, _ => shim));
      }
      finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_ExtensionlessBinary_IsUsed()
    {
      string root = TempDir();
      try
      {
        string exe = Path.Combine(root, "claude");
        File.WriteAllText(exe, "#!/bin/sh");
        Assert.Equal(exe, ClaudeCli.Find(null, _ => exe));
      }
      finally { Directory.Delete(root, true); }
    }

    private static string TempDir()
    {
      string dir = Path.Combine(Path.GetTempPath(), "subs2srs-cli-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      return dir;
    }

    // ── probing the flags ─────────────────────────────────────────────

    [Fact]
    public void FromHelp_ReadsTheFlagsThatAreThere()
    {
      ClaudeCliFeatures f = ClaudeCliFeatures.FromHelp(
        "  -p, --print                 Print response and exit\n"
        + "  --model <model>             Model for the session\n"
        + "  --output-format <format>    Output format\n"
        + "  --json-schema <schema>      JSON Schema for structured output\n"
        + "  --system-prompt <prompt>    System prompt\n"
        + "  --tools <tools>             Allowed tools\n"
        + "  --no-session-persistence    Do not save the session\n"
        + "  --safe-mode                 Disable CLAUDE.md, hooks, MCP\n"
        + "  --effort <level>            Thinking effort\n");

      Assert.True(f.Print);
      Assert.True(f.Model);
      Assert.True(f.JsonSchema);
      Assert.True(f.SystemPrompt);
      Assert.True(f.Tools);
      Assert.True(f.NoSessionPersistence);
      Assert.True(f.SafeMode);
      Assert.True(f.Effort);
      // Documented online but absent from the installed build: never passed.
      Assert.False(f.SystemPromptFile);
    }

    [Fact]
    public void FromHelp_SystemPromptDoesNotImplySystemPromptFile()
    {
      ClaudeCliFeatures f = ClaudeCliFeatures.FromHelp("--system-prompt <p>");
      Assert.True(f.SystemPrompt);
      Assert.False(f.SystemPromptFile);

      ClaudeCliFeatures g = ClaudeCliFeatures.FromHelp("--system-prompt-file <f>");
      Assert.True(g.SystemPromptFile);
    }

    [Fact]
    public void FromHelp_EmptyHelp_HasNoOptionalFlags()
    {
      ClaudeCliFeatures f = ClaudeCliFeatures.FromHelp("");
      Assert.False(f.Tools);
      Assert.False(f.SafeMode);
      Assert.False(f.Effort);
      Assert.False(f.SystemPromptFile);
    }

    [Fact]
    public void Probe_RunsHelpOncePerExecutable()
    {
      ClaudeCli.ResetProbeCache();
      string exe = "claude-probe-" + Guid.NewGuid().ToString("N");
      int calls = 0;
      string? help(string path) { calls++; return "--safe-mode --tools <t>"; }

      ClaudeCliFeatures first = ClaudeCli.Probe(exe, help);
      ClaudeCliFeatures again = ClaudeCli.Probe(exe, help);

      Assert.Same(first, again);
      Assert.Equal(1, calls);
      Assert.True(first.SafeMode);
    }

    [Fact]
    public void Probe_HelpFails_AssumesOnlyTheEssentials()
    {
      ClaudeCli.ResetProbeCache();
      ClaudeCliFeatures f = ClaudeCli.Probe("claude-broken-" + Guid.NewGuid().ToString("N"),
        _ => throw new InvalidOperationException("no such file"));

      Assert.True(f.Print);
      Assert.True(f.JsonSchema);
      Assert.False(f.SafeMode);   // passing a flag this build lacks would fail every request
      Assert.False(f.Tools);
    }

    // ── the argument list ─────────────────────────────────────────────

    [Fact]
    public void BuildArguments_HasTheFlagsOfTheProbedBuild()
    {
      List<string> args = ClaudeCli.BuildArguments(AllFlags(), "terminal-claude-sonnet-5",
        "be brief", Schema(), null, out string prefix);

      Assert.Equal("", prefix);
      Assert.Equal("-p", args[0]);
      Assert.Equal(new[] { "--model", "claude-sonnet-5" }, Pair(args, "--model"));
      Assert.Equal(new[] { "--output-format", "json" }, Pair(args, "--output-format"));
      Assert.Equal(new[] { "--tools", "" }, Pair(args, "--tools"));
      Assert.Contains("--no-session-persistence", args);
      Assert.Contains("--safe-mode", args);
      Assert.Equal(new[] { "--effort", "low" }, Pair(args, "--effort"));
      Assert.Equal(new[] { "--system-prompt", "be brief" }, Pair(args, "--system-prompt"));
      // --bare would make the CLI read only ANTHROPIC_API_KEY and bill the API.
      Assert.DoesNotContain("--bare", args);
    }

    [Fact]
    public void BuildArguments_OmitsFlagsTheBuildDoesNotHave()
    {
      ClaudeCliFeatures old = ClaudeCliFeatures.FromHelp("-p --model <m> --output-format <f> --system-prompt <p> --json-schema <s>");
      List<string> args = ClaudeCli.BuildArguments(old, "terminal-claude-sonnet-5", "hi", Schema(), null, out _);

      Assert.DoesNotContain("--safe-mode", args);
      Assert.DoesNotContain("--tools", args);
      Assert.DoesNotContain("--effort", args);
      Assert.DoesNotContain("--no-session-persistence", args);
      Assert.Contains("--json-schema", args);
    }

    [Fact]
    public void BuildArguments_SchemaRoundTrips()
    {
      JsonElement schema = Schema("{\"type\":\"object\",\"properties\":{\"snippets\":{\"type\":\"array\"}},\"required\":[\"snippets\"]}");
      List<string> args = ClaudeCli.BuildArguments(AllFlags(), "terminal-claude-sonnet-5", "s", schema, null, out _);

      string json = args[args.IndexOf("--json-schema") + 1];
      using JsonDocument doc = JsonDocument.Parse(json);
      Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
      Assert.Equal("snippets", doc.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void BuildArguments_NoSchema_NoFlag()
    {
      List<string> args = ClaudeCli.BuildArguments(AllFlags(), "terminal-claude-sonnet-5", "s", default, null, out _);
      Assert.DoesNotContain("--json-schema", args);
    }

    [Fact]
    public void BuildArguments_LongSystemPrompt_MovesIntoThePrompt()
    {
      string system = new string('x', ClaudeCli.MaxInlineSystemPrompt + 1);
      // This build has no --system-prompt-file, so the command line cannot carry it.
      ClaudeCliFeatures noFile = ClaudeCliFeatures.FromHelp("-p --model <m> --output-format <f> --system-prompt <p>");
      List<string> args = ClaudeCli.BuildArguments(noFile, "terminal-claude-sonnet-5", system, default, null, out string prefix);

      Assert.DoesNotContain("--system-prompt", args);
      Assert.StartsWith(ClaudeCli.SystemPromptHeading, prefix);
      Assert.Contains(system, prefix);
      Assert.False(ClaudeCli.WantsSystemPromptFile(noFile, system));
    }

    [Fact]
    public void BuildArguments_LongSystemPrompt_UsesTheFileWhenTheBuildHasIt()
    {
      string system = new string('x', ClaudeCli.MaxInlineSystemPrompt + 1);
      ClaudeCliFeatures f = AllFlags();
      Assert.True(ClaudeCli.WantsSystemPromptFile(f, system));

      List<string> args = ClaudeCli.BuildArguments(f, "terminal-claude-sonnet-5", system, default, @"C:\tmp\p.md", out string prefix);

      Assert.Equal("", prefix);
      Assert.Equal(new[] { "--system-prompt-file", @"C:\tmp\p.md" }, Pair(args, "--system-prompt-file"));
      Assert.DoesNotContain("--system-prompt", args);
    }

    [Fact]
    public void WantsSystemPromptFile_ShortPromptStaysInline()
    {
      Assert.False(ClaudeCli.WantsSystemPromptFile(AllFlags(), "short"));
    }

    private static string[] Pair(List<string> args, string flag)
    {
      int i = args.IndexOf(flag);
      return i < 0 ? Array.Empty<string>() : new[] { args[i], args[i + 1] };
    }

    // ── classifying a finished process ────────────────────────────────

    [Fact]
    public void Classify_StructuredOutput_IsTheAnswer()
    {
      CliOutcome o = ClaudeCli.Classify(0,
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\","
        + "\"structured_output\":{\"snippets\":[{\"first\":0,\"last\":1}]},"
        + "\"usage\":{\"input_tokens\":1200,\"output_tokens\":40},\"model\":\"claude-sonnet-5\"}", "");

      Assert.Equal(CliAction.Ok, o.Action);
      Assert.Contains("\"snippets\"", o.Json);
      Assert.Equal(1200, o.InputTokens);
      Assert.Equal(40, o.OutputTokens);
      Assert.Equal("claude-sonnet-5", o.Model);
    }

    /// <summary>
    /// A result object exactly as the CLI printed one on 2026-09-16 (claude 2.1.268,
    /// terminal-claude-sonnet-5, ids scrubbed). It pins the two things a hand-written sample got
    /// wrong: nearly the whole prompt comes back as cache tokens rather than <c>input_tokens</c>,
    /// and there is no top-level <c>model</c> field at all.
    /// </summary>
    [Fact]
    public void Classify_ARecordedRealAnswer()
    {
      CliOutcome o = ClaudeCli.Classify(0, AiProviderTests.Fixture("claude-cli-ok.json"), "");

      Assert.Equal(CliAction.Ok, o.Action);
      Assert.Equal("success", o.Subtype);
      Assert.Null(o.Status); // api_error_status is present but JSON null
      Assert.Contains("\"first\": 0", o.Json);

      // 4 + 1611 cache-read + 1761 cache-creation: input_tokens alone would report 4.
      Assert.Equal(3376, o.InputTokens);
      Assert.Equal(157, o.OutputTokens);
      // The ID comes from the per-model usage breakdown, not a "model" field.
      Assert.Equal("claude-sonnet-5", o.Model);
    }

    [Fact]
    public void Classify_TextOnlyAnswer_IsOkWithNoJson()
    {
      CliOutcome o = ClaudeCli.Classify(0, "{\"is_error\":false,\"result\":\"{\\\"snippets\\\":[]}\"}", "");

      Assert.Equal(CliAction.Ok, o.Action);
      Assert.Null(o.Json);
      Assert.Equal("{\"snippets\":[]}", o.Text);
    }

    [Fact]
    public void Classify_MissingUsage_IsTolerated()
    {
      CliOutcome o = ClaudeCli.Classify(0, "{\"is_error\":false,\"result\":\"{}\"}", "");
      Assert.Equal(CliAction.Ok, o.Action);
      Assert.Equal(0, o.InputTokens);
      Assert.Equal(0, o.OutputTokens);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    [InlineData(500)]
    [InlineData(503)]
    public void Classify_UpstreamStatus_IsRetried(int status)
    {
      CliOutcome o = ClaudeCli.Classify(1,
        "{\"is_error\":true,\"result\":\"upstream said no\",\"api_error_status\":" + status + "}", "");

      Assert.Equal(CliAction.Retry, o.Action);
      Assert.Equal(status, o.Status);
    }

    [Fact]
    public void Classify_UnknownModel_Fails()
    {
      CliOutcome o = ClaudeCli.Classify(1,
        "{\"is_error\":true,\"result\":\"Invalid model name: claude-nope\",\"api_error_status\":404}", "");

      Assert.Equal(CliAction.Fail, o.Action);
      Assert.Contains("Invalid model name", o.Text);
    }

    [Fact]
    public void Classify_MaxStructuredOutputRetries_Fails()
    {
      // The CLI has already retried the schema itself; another process would fail the same way.
      CliOutcome o = ClaudeCli.Classify(1,
        "{\"is_error\":true,\"subtype\":\"error_max_structured_output_retries\",\"result\":\"schema never validated\"}", "");

      Assert.Equal(CliAction.Fail, o.Action);
      Assert.Equal(ClaudeCli.StructuredOutputRetriesSubtype, o.Subtype);
    }

    [Theory]
    [InlineData("You've hit your 5-hour limit. It will reset at 3pm.")]
    [InlineData("Usage limit reached for this account")]
    [InlineData("Your limit will reset at midnight")]
    public void Classify_UsageLimitWording_IsExhausted(string message)
    {
      CliOutcome o = ClaudeCli.Classify(1, "{\"is_error\":true,\"result\":" + JsonSerializer.Serialize(message) + "}", "");
      Assert.Equal(CliAction.Exhausted, o.Action);
      Assert.Equal(message, o.Text);
    }

    [Fact]
    public void Classify_NonJsonOutput_IsRetried()
    {
      CliOutcome o = ClaudeCli.Classify(1, "Segmentation fault", "node: symbol lookup error");

      Assert.Equal(CliAction.Retry, o.Action);
      Assert.Contains("symbol lookup error", o.Text);
    }

    [Fact]
    public void Classify_KilledProcess_NoOutput_IsRetried()
    {
      CliOutcome o = ClaudeCli.Classify(null, "", "");
      Assert.Equal(CliAction.Retry, o.Action);
      Assert.Contains("no JSON output", o.Text);
    }

    [Fact]
    public void Classify_NonZeroExitWithoutIsError_IsNotOk()
    {
      CliOutcome o = ClaudeCli.Classify(1, "{\"result\":\"partial\"}", "");
      Assert.NotEqual(CliAction.Ok, o.Action);
    }

    // ── the last JSON object ──────────────────────────────────────────

    [Fact]
    public void LastJsonObject_TwoObjects_TakesTheLater()
    {
      string text = "{\"snippets\":[1]}\nWait, let me reconsider...\n{\"snippets\":[2]}";
      Assert.Equal("{\"snippets\":[2]}", ClaudeCli.LastJsonObject(text));
    }

    [Fact]
    public void LastJsonObject_NestedObject_IsOneObject()
    {
      string text = "{\"a\":{\"b\":1},\"c\":2}";
      Assert.Equal(text, ClaudeCli.LastJsonObject(text));
    }

    [Fact]
    public void LastJsonObject_FencedBlock_IsFound()
    {
      Assert.Equal("{\"snippets\":[]}", ClaudeCli.LastJsonObject("Here you go:\n```json\n{\"snippets\":[]}\n```\n"));
    }

    [Fact]
    public void LastJsonObject_BraceInsideAString_DoesNotConfuseIt()
    {
      string text = "{\"note\":\"a } brace\"}";
      Assert.Equal(text, ClaudeCli.LastJsonObject(text));
    }

    [Fact]
    public void LastJsonObject_UnbalancedThenValid_TakesTheValid()
    {
      Assert.Equal("{\"ok\":1}", ClaudeCli.LastJsonObject("{ oops \n{\"ok\":1}"));
    }

    [Fact]
    public void LastJsonObject_NoJson_IsNull()
    {
      Assert.Null(ClaudeCli.LastJsonObject("no braces here"));
      Assert.Null(ClaudeCli.LastJsonObject(""));
      Assert.Null(ClaudeCli.LastJsonObject(null));
    }
  }
}
