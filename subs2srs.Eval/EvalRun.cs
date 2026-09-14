using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace subs2srs.Eval
{
  /// <summary>One validation file's outcome in a run.</summary>
  public sealed class EvalFileRecord
  {
    public string RelativePath { get; set; } = "";
    public bool IsHoldout { get; set; }
    public int Lines { get; set; }
    /// <summary>Human joins (kept projection, Lines - 1 entries).</summary>
    public bool[] Truth { get; set; } = Array.Empty<bool>();
    /// <summary>What the producer proposed, null when the file failed.</summary>
    public bool[]? Predicted { get; set; }
    public GroupingScore? Score { get; set; }
    public bool Cached { get; set; }
    public int Chunks { get; set; }
    public int FailedChunks { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double? Usd { get; set; }
    /// <summary>The proposal the human corrected when labelling, if the file has one.</summary>
    public string? ProposalProducer { get; set; }
    public string? ProposalModel { get; set; }
    public int? ProposalPromptVersion { get; set; }
    /// <summary>Boundaries the human flipped from that proposal.</summary>
    public int? ProposalFlips { get; set; }
    public List<string> Repairs { get; set; } = new();
    public string? Error { get; set; }

    [JsonIgnore] public string Name => Path.GetFileName(RelativePath);
  }


  /// <summary>A whole run: configuration, per-file records and the three aggregates. Written as &lt;name&gt;.run.json.</summary>
  public sealed class EvalRun
  {
    public const string Extension = ".run.json";

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>"rules" or "ai".</summary>
    public string Producer { get; set; } = "rules";
    public string? Model { get; set; }
    public int? PromptVersion { get; set; }
    public int? ChunkTargetLines { get; set; }
    public string? ExtraInstructions { get; set; }
    public RuleGrouperOptions? RuleOptions { get; set; }
    public string SetDir { get; set; } = "";
    public string? HoldoutDir { get; set; }
    public string? CacheDir { get; set; }
    public List<EvalFileRecord> Files { get; set; } = new();
    public GroupingScore Tuning { get; set; } = new();
    public GroupingScore Holdout { get; set; } = new();
    public GroupingScore All { get; set; } = new();
    /// <summary>Tokens and cost of the files that were actually requested in this run (not served from the cache).</summary>
    public int SpentInputTokens { get; set; }
    public int SpentOutputTokens { get; set; }
    public double? SpentUsd { get; set; }

    public void Aggregate()
    {
      Tuning = GroupingScore.Sum(Files.Where(f => !f.IsHoldout && f.Score != null).Select(f => f.Score!));
      Holdout = GroupingScore.Sum(Files.Where(f => f.IsHoldout && f.Score != null).Select(f => f.Score!));
      All = GroupingScore.Sum(Files.Where(f => f.Score != null).Select(f => f.Score!));
      SpentInputTokens = Files.Where(f => !f.Cached).Sum(f => f.InputTokens);
      SpentOutputTokens = Files.Where(f => !f.Cached).Sum(f => f.OutputTokens);
      SpentUsd = Model == null ? null : AiPricing.Usd(Model, SpentInputTokens, SpentOutputTokens);
    }

    public string Describe()
    {
      if (Producer == "rules")
      {
        RuleGrouperOptions r = RuleOptions ?? new RuleGrouperOptions();
        return FormattableString.Invariant($"rules (gap {r.MaxJoinGapMs} ms, {(r.RequireCue ? "cues \"" + r.CueChars + "\"" + (r.JoinOnActorChange ? " or actor change" : "") : "no cue needed")})");
      }
      string s = FormattableString.Invariant($"{Model}, prompt v{PromptVersion}, {ChunkTargetLines} lines per request");
      if (!string.IsNullOrWhiteSpace(ExtraInstructions)) s += ", with extra instructions";
      return s;
    }

    // ── IO ──────────────────────────────────────────────────────────────

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EvalRun FromJson(string json) =>
      JsonSerializer.Deserialize<EvalRun>(json, Json) ?? throw new InvalidDataException("empty run file");

    public static EvalRun Read(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));

    public string JsonPath(string outDir) => Path.Combine(outDir, Name + Extension);
    public string CsvPath(string outDir) => Path.Combine(outDir, Name + ".csv");
    public string MarkdownPath(string outDir) => Path.Combine(outDir, Name + ".md");

    /// <summary>Write .run.json, .csv and .md into <paramref name="outDir"/>; the markdown gets the optional comparison appended.</summary>
    public void WriteReports(string outDir, string? comparison = null)
    {
      Directory.CreateDirectory(outDir);
      var utf8 = new UTF8Encoding(false);
      File.WriteAllText(JsonPath(outDir), ToJson(), utf8);
      File.WriteAllText(CsvPath(outDir), ToCsv(), utf8);
      string md = ToMarkdown();
      if (!string.IsNullOrEmpty(comparison)) md += "\n" + comparison;
      File.WriteAllText(MarkdownPath(outDir), md, utf8);
    }

    // ── text ────────────────────────────────────────────────────────────

    private static readonly string[] Columns =
      { "file", "set", "lines", "boundaries", "tp", "fp", "fn", "precision", "recall", "f1", "truthSnippets", "exact", "exactRate", "overMerged", "underMerged", "multiLineSnippets", "multiLineExact", "inputTokens", "outputTokens", "usd", "cached", "failedChunks", "proposalFlips", "error" };

    public string ToCsv()
    {
      var sb = new StringBuilder();
      sb.Append(string.Join(",", Columns)).Append('\n');
      foreach (EvalFileRecord f in Files)
        sb.Append(CsvRow(f.RelativePath, f.IsHoldout ? "holdout" : "tuning", f.Score, f.InputTokens, f.OutputTokens, f.Usd,
          f.Cached ? "yes" : "no", f.FailedChunks.ToString(CultureInfo.InvariantCulture), f.ProposalFlips?.ToString(CultureInfo.InvariantCulture) ?? "", f.Error ?? "")).Append('\n');
      foreach ((string label, GroupingScore s) in Aggregates())
        sb.Append(CsvRow(label, "", s, s.InputTokens, s.OutputTokens, s.Usd, "", "", "", "")).Append('\n');
      return sb.ToString();
    }

    private static string CsvRow(string file, string set, GroupingScore? s, int inTok, int outTok, double? usd, string cached, string failed, string flips, string error)
    {
      s ??= new GroupingScore();
      string[] cells =
      {
        Csv(file), set, N(s.Lines), N(s.Boundaries), N(s.TruePositives), N(s.FalsePositives), N(s.FalseNegatives),
        R(s.Precision), R(s.Recall), R(s.F1), N(s.TruthSnippets), N(s.ExactSnippets), R(s.ExactRate), N(s.OverMergedSnippets), N(s.UnderMergedSnippets),
        N(s.TruthMultiLineSnippets), N(s.ExactMultiLineSnippets), N(inTok), N(outTok), usd.HasValue ? usd.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "",
        cached, failed, flips, Csv(error),
      };
      return string.Join(",", cells);
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string R(double d) => d.ToString("0.0000", CultureInfo.InvariantCulture);

    public IEnumerable<(string label, GroupingScore score)> Aggregates()
    {
      bool split = Files.Any(f => f.IsHoldout) && Files.Any(f => !f.IsHoldout);
      if (split)
      {
        yield return ("TUNING", Tuning);
        yield return ("HOLD-OUT", Holdout);
      }
      else if (Files.Any(f => f.IsHoldout)) yield return ("HOLD-OUT", Holdout);
      yield return ("ALL", All);
    }

    /// <summary>The table printed on stdout: one row per file plus the aggregates.</summary>
    public string ToTable()
    {
      var rows = new List<string[]>();
      rows.Add(new[] { "file", "set", "lines", "P", "R", "F1", "exact", "over", "under", "tokens", "usd", "note" });
      foreach (EvalFileRecord f in Files)
        rows.Add(new[] { f.RelativePath, f.IsHoldout ? "holdout" : "tuning", N(f.Lines) }.Concat(Metrics(f.Score, f.InputTokens + f.OutputTokens, f.Usd)).Append(Note(f)).ToArray());
      foreach ((string label, GroupingScore s) in Aggregates())
        rows.Add(new[] { label, "", N(s.Lines) }.Concat(Metrics(s, s.InputTokens + s.OutputTokens, s.Usd)).Append(FormattableString.Invariant($"{s.Files} file(s)")).ToArray());

      int[] widths = new int[rows[0].Length];
      foreach (string[] row in rows)
        for (int c = 0; c < row.Length; c++) widths[c] = Math.Max(widths[c], row[c].Length);
      var sb = new StringBuilder();
      for (int r = 0; r < rows.Count; r++)
      {
        if (r == rows.Count - Aggregates().Count()) sb.Append(new string('-', widths.Sum() + 2 * (widths.Length - 1))).Append('\n');
        for (int c = 0; c < rows[r].Length; c++)
        {
          bool numeric = c >= 2 && c <= 10;
          sb.Append(numeric ? rows[r][c].PadLeft(widths[c]) : rows[r][c].PadRight(widths[c]));
          if (c < rows[r].Length - 1) sb.Append("  ");
        }
        sb.Append('\n');
      }
      return sb.ToString();
    }

    private static string[] Metrics(GroupingScore? s, int tokens, double? usd)
    {
      if (s == null || s.Files == 0) return new[] { "-", "-", "-", "-", "-", "-", "-", "-" };
      return new[]
      {
        Pct(s.Precision), Pct(s.Recall), Pct(s.F1),
        FormattableString.Invariant($"{s.ExactSnippets}/{s.TruthSnippets}"),
        N(s.OverMergedSnippets), N(s.UnderMergedSnippets),
        tokens > 0 ? tokens.ToString("N0", CultureInfo.InvariantCulture) : "-",
        usd.HasValue ? usd.Value.ToString("0.000", CultureInfo.InvariantCulture) : "-",
      };
    }

    private static string Pct(double d) => (d * 100).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Note(EvalFileRecord f)
    {
      if (f.Error != null) return "error: " + f.Error;
      var parts = new List<string>();
      if (f.Cached) parts.Add("cached");
      if (f.FailedChunks > 0) parts.Add(FormattableString.Invariant($"{f.FailedChunks}/{f.Chunks} chunks fell back to the rules"));
      if (f.Repairs.Count > 0) parts.Add(FormattableString.Invariant($"{f.Repairs.Count} repair(s)"));
      if (f.ProposalFlips.HasValue) parts.Add(FormattableString.Invariant($"labelled from {f.ProposalProducer}, {f.ProposalFlips} flip(s)"));
      return string.Join(", ", parts);
    }

    public string ToMarkdown()
    {
      var sb = new StringBuilder();
      sb.Append("# Grouping eval: ").Append(Name).Append('\n').Append('\n');
      sb.Append("- Producer: ").Append(Describe()).Append('\n');
      sb.Append("- Set: `").Append(SetDir).Append('`');
      if (HoldoutDir != null) sb.Append(", hold-out `").Append(HoldoutDir).Append('`');
      sb.Append('\n');
      sb.Append("- Run at ").Append(CreatedUtc.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
      if (Producer == "ai")
      {
        sb.Append(FormattableString.Invariant($"- Spent this run: {SpentInputTokens:N0} input + {SpentOutputTokens:N0} output tokens"));
        if (SpentUsd.HasValue) sb.Append(FormattableString.Invariant($", about ${SpentUsd.Value:0.000}"));
        sb.Append(FormattableString.Invariant($" ({Files.Count(f => f.Cached)} of {Files.Count} file(s) from the cache)\n"));
      }
      sb.Append('\n');
      sb.Append("| file | set | lines | P | R | F1 | exact | over | under | tokens | usd | note |\n");
      sb.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |\n");
      foreach (EvalFileRecord f in Files)
        sb.Append("| ").Append(string.Join(" | ", new[] { f.RelativePath, f.IsHoldout ? "holdout" : "tuning", N(f.Lines) }.Concat(Metrics(f.Score, f.InputTokens + f.OutputTokens, f.Usd)).Append(Note(f)))).Append(" |\n");
      foreach ((string label, GroupingScore s) in Aggregates())
        sb.Append("| **").Append(label).Append("** | | ").Append(string.Join(" | ", new[] { N(s.Lines) }.Concat(Metrics(s, s.InputTokens + s.OutputTokens, s.Usd)).Append(FormattableString.Invariant($"{s.Files} file(s)")))).Append(" |\n");
      sb.Append('\n');
      sb.Append("P/R/F1 are per boundary (join == true); exact = truth snippets reproduced exactly; over/under = truth snippets the prediction extended / split.\n");
      return sb.ToString();
    }
  }
}
