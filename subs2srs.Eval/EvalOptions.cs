using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace subs2srs.Eval
{
  /// <summary>A usage or environment error; <see cref="ExitCode"/> becomes the process exit code.</summary>
  public sealed class EvalException : Exception
  {
    public int ExitCode { get; }
    public EvalException(string message, int exitCode = 1) : base(message) { ExitCode = exitCode; }
  }


  /// <summary>Command line of one evaluation run (see <see cref="Usage"/>).</summary>
  public sealed class EvalOptions
  {
    public const int ExitUsage = 1;
    public const int ExitNoFiles = 2;
    public const int ExitCostCap = 3;
    public const int ExitAllFailed = 4;

    public string SetDir { get; set; } = "";
    public string? HoldoutDir { get; set; }
    /// <summary>Null = the rule-based grouper.</summary>
    public string? Model { get; set; }
    public bool Rules => Model == null;
    public string? ExtraInstructions { get; set; }
    public int ChunkTargetLines { get; set; } = AiChunker.DefaultTargetLines;
    public string? CacheDir { get; set; }
    public string? OutDir { get; set; }
    public string? Name { get; set; }
    public string? Compare { get; set; }
    public bool Refresh { get; set; }
    /// <summary>Requests in flight at once; null = the preference, 0 = auto.</summary>
    public int? Concurrency { get; set; }
    public double? MaxCostUsd { get; set; }
    public bool EstimateOnly { get; set; }
    public bool Verbose { get; set; }
    public bool NoPrefs { get; set; }
    public string? PrefsPath { get; set; }
    public bool Help { get; set; }
    public RuleGrouperOptions RuleOptions { get; set; } = new RuleGrouperOptions();

    public string EffectiveOutDir => OutDir ?? Path.Combine(SetDir, "eval-reports");

    /// <summary>Run name: <c>rules</c>, or model + prompt version (+ a hash of the extra instructions, + the chunk size when not default).</summary>
    public string EffectiveName
    {
      get
      {
        if (!string.IsNullOrWhiteSpace(Name)) return Sanitize(Name);
        if (Rules) return "rules";
        var sb = new StringBuilder(Model).Append("_v").Append(AiGroupingPrompt.PromptVersion);
        if (!string.IsNullOrWhiteSpace(ExtraInstructions))
          sb.Append("_p").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ExtraInstructions.Trim()))).Substring(0, 8).ToLowerInvariant());
        if (ChunkTargetLines != AiChunker.DefaultTargetLines) sb.Append("_c").Append(ChunkTargetLines);
        return Sanitize(sb.ToString());
      }
    }

    public static string Sanitize(string name)
    {
      var sb = new StringBuilder(name.Length);
      foreach (char c in name.Trim())
        sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '-' : c);
      return sb.Length == 0 ? "run" : sb.ToString();
    }

    public const string Usage = @"subs2srs.Eval - score a snippet grouper against human-labelled validation files (plan 9.3)

usage: subs2srs.Eval --set <dir> [--model <model> | --rules] [options]

  --set <dir>          directory of *.grouping.json files (searched recursively); files under a
                       folder named 'holdout' are the hold-out set and are reported separately
  --holdout <dir>      use this directory as the hold-out set instead (inside or outside --set)
  --model <model>      ask this model (claude-..., gpt-..., gemini-...) through the app's own
                       chunker, prompt, provider layer and cache; needs the provider's API key in
                       preferences.json or the environment (ANTHROPIC_API_KEY, OPENAI_API_KEY,
                       GEMINI_API_KEY)
  --rules              score the rule-based grouper (the default when --model is absent)
  --prompt <text|@file> extra instructions appended to the system prompt (a path is read as a file)
  --chunk <n>          target lines per request (default 200, 0 = whole episode)
  --refresh            ignore cached answers and ask the model again
  --cache <dir>        answer cache (default: the app's AI cache directory / preference)
  --concurrency <n>    requests in flight at once (default: the AI Max Concurrent Requests
                       preference; 0 = auto); pacing itself follows the provider's rate-limit answers
  --max-cost <usd>     stop before the first request when the estimate exceeds this amount
  --estimate           print the token/cost estimate and exit without calling the model
  --out <dir>          where reports go (default <set>/eval-reports)
  --name <run>         run name (default 'rules' or <model>_v<promptVersion>[_p<hash>][_c<chunk>])
  --compare <run>      diff this run against an earlier run boundary by boundary
                       (a .run.json path or a run name in --out)
  --rules-gap <ms>     rules: maximum gap between joined lines (1500)
  --rules-cues <chars> rules: cue characters at the end of a line (?？…→、,)
  --rules-no-cue       rules: join on the gap alone
  --rules-no-actor     rules: an actor change is not a cue
  --prefs <file>       read this preferences.json instead of the user's
  --no-prefs           do not read preferences.json (keys from the environment, retry defaults)
  --verbose            echo the application log to stderr
  --help               this text

Output: a table on stdout and <out>/<run>.run.json, .csv and .md. Every metric is per boundary
(join == true precision/recall/F1) or per truth snippet (exact, over-merged, under-merged),
per file and summed over the tuning set, the hold-out set and all files.

Exit codes: 0 ok, 1 usage, 2 no validation files, 3 cost cap exceeded, 4 every model call failed.";

    public static EvalOptions Parse(string[] args)
    {
      var o = new EvalOptions();
      for (int i = 0; i < args.Length; i++)
      {
        string a = args[i];
        string Next()
        {
          if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new EvalException($"{a} needs a value", ExitUsage);
          return args[++i];
        }
        int NextInt()
        {
          string v = Next();
          return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0
            ? n : throw new EvalException($"{a} needs a non-negative integer, got '{v}'", ExitUsage);
        }
        switch (a)
        {
          case "--set": o.SetDir = Next(); break;
          case "--holdout": o.HoldoutDir = Next(); break;
          case "--model": o.Model = Next().Trim(); break;
          case "--rules": o.Model = null; break;
          case "--prompt": o.ExtraInstructions = ReadPrompt(Next()); break;
          case "--chunk": o.ChunkTargetLines = NextInt(); break;
          case "--refresh": o.Refresh = true; break;
          case "--cache": o.CacheDir = Next(); break;
          case "--concurrency": o.Concurrency = NextInt(); break;
          case "--max-cost":
          {
            string v = Next();
            o.MaxCostUsd = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0
              ? d : throw new EvalException($"--max-cost needs a non-negative number, got '{v}'", ExitUsage);
            break;
          }
          case "--estimate": o.EstimateOnly = true; break;
          case "--out": o.OutDir = Next(); break;
          case "--name": o.Name = Next(); break;
          case "--compare": o.Compare = Next(); break;
          case "--rules-gap": o.RuleOptions.MaxJoinGapMs = NextInt(); break;
          case "--rules-cues": o.RuleOptions.CueChars = Next(); break;
          case "--rules-no-cue": o.RuleOptions.RequireCue = false; break;
          case "--rules-no-actor": o.RuleOptions.JoinOnActorChange = false; break;
          case "--prefs": o.PrefsPath = Next(); break;
          case "--no-prefs": o.NoPrefs = true; break;
          case "--verbose": o.Verbose = true; break;
          case "--help": case "-h": case "-?": o.Help = true; return o;
          default: throw new EvalException($"unknown argument '{a}'", ExitUsage);
        }
      }
      if (string.IsNullOrWhiteSpace(o.SetDir)) throw new EvalException("--set <dir> is required", ExitUsage);
      if (o.Model == "") throw new EvalException("--model needs a model name", ExitUsage);
      if (o.Rules && (o.EstimateOnly || o.Refresh || o.MaxCostUsd.HasValue))
        throw new EvalException("--estimate, --refresh and --max-cost only apply with --model", ExitUsage);
      return o;
    }

    private static string ReadPrompt(string value)
    {
      string path = value.StartsWith("@", StringComparison.Ordinal) ? value.Substring(1) : value;
      if (value.StartsWith("@", StringComparison.Ordinal) || File.Exists(path))
      {
        if (!File.Exists(path)) throw new EvalException($"prompt file not found: {path}", ExitUsage);
        return File.ReadAllText(path, Encoding.UTF8).Trim();
      }
      return value.Trim();
    }
  }
}
