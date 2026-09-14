using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace subs2srs
{
  /// <summary>
  /// Scores of one predicted grouping against the human truth (plan §9.3), or the sum over
  /// several files. Every decision is one boundary between two consecutive kept lines, so the
  /// primary numbers are precision, recall and F1 of <c>join == true</c>. The snippet-level
  /// counts classify each truth snippet as exact, under-merged (the prediction splits it) or
  /// over-merged (the prediction extends it), which is the actionable signal for prompt changes.
  /// </summary>
  public sealed class GroupingScore
  {
    public int Files { get; set; }
    public int Lines { get; set; }

    /// <summary>Boundaries between consecutive kept lines (lines - 1 per file).</summary>
    public int Boundaries { get; set; }
    /// <summary>Both joined.</summary>
    public int TruePositives { get; set; }
    /// <summary>Predicted joined, truth not: an over-merged boundary.</summary>
    public int FalsePositives { get; set; }
    /// <summary>Truth joined, predicted not: an under-merged boundary.</summary>
    public int FalseNegatives { get; set; }
    public int TrueNegatives { get; set; }

    public int TruthSnippets { get; set; }
    public int PredictedSnippets { get; set; }
    /// <summary>Truth snippets the prediction reproduces exactly (single-line ones included).</summary>
    public int ExactSnippets { get; set; }
    /// <summary>Truth snippets with at least one internal boundary not joined by the prediction.</summary>
    public int UnderMergedSnippets { get; set; }
    /// <summary>Truth snippets kept whole by the prediction but extended over a neighbour.</summary>
    public int OverMergedSnippets { get; set; }
    public int TruthMultiLineSnippets { get; set; }
    public int ExactMultiLineSnippets { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    /// <summary>USD, or null when no file had a known price.</summary>
    public double? Usd { get; set; }

    /// <summary>1 when nothing was predicted and nothing was expected; 0 when nothing was predicted but something was expected.</summary>
    public double Precision => TruePositives + FalsePositives > 0
      ? (double)TruePositives / (TruePositives + FalsePositives)
      : (FalseNegatives == 0 ? 1.0 : 0.0);

    public double Recall => TruePositives + FalseNegatives > 0
      ? (double)TruePositives / (TruePositives + FalseNegatives)
      : (FalsePositives == 0 ? 1.0 : 0.0);

    public double F1 => Precision + Recall > 0 ? 2 * Precision * Recall / (Precision + Recall) : 0.0;

    public double ExactRate => TruthSnippets > 0 ? (double)ExactSnippets / TruthSnippets : 1.0;

    public double MultiLineExactRate => TruthMultiLineSnippets > 0 ? (double)ExactMultiLineSnippets / TruthMultiLineSnippets : 1.0;

    /// <summary>Micro-average: the counts are summed and the rates recomputed from the sums.</summary>
    public static GroupingScore Sum(IEnumerable<GroupingScore> scores)
    {
      var sum = new GroupingScore();
      bool anyUsd = false;
      double usd = 0;
      foreach (GroupingScore s in scores)
      {
        sum.Files += s.Files;
        sum.Lines += s.Lines;
        sum.Boundaries += s.Boundaries;
        sum.TruePositives += s.TruePositives;
        sum.FalsePositives += s.FalsePositives;
        sum.FalseNegatives += s.FalseNegatives;
        sum.TrueNegatives += s.TrueNegatives;
        sum.TruthSnippets += s.TruthSnippets;
        sum.PredictedSnippets += s.PredictedSnippets;
        sum.ExactSnippets += s.ExactSnippets;
        sum.UnderMergedSnippets += s.UnderMergedSnippets;
        sum.OverMergedSnippets += s.OverMergedSnippets;
        sum.TruthMultiLineSnippets += s.TruthMultiLineSnippets;
        sum.ExactMultiLineSnippets += s.ExactMultiLineSnippets;
        sum.InputTokens += s.InputTokens;
        sum.OutputTokens += s.OutputTokens;
        if (s.Usd.HasValue) { anyUsd = true; usd += s.Usd.Value; }
      }
      sum.Usd = anyUsd ? usd : null;
      return sum;
    }
  }


  /// <summary>Compares two kept-projection join vectors over the same lines.</summary>
  public static class GroupingScorer
  {
    /// <summary>joins[k] with anything past the end (or a missing last slot) read as false.</summary>
    public static bool JoinAt(bool[]? joins, int k) => joins != null && k >= 0 && k < joins.Length && joins[k];

    /// <summary>Score <paramref name="predicted"/> against <paramref name="truth"/> for an episode of <paramref name="lineCount"/> kept lines.</summary>
    public static GroupingScore Score(bool[] truth, bool[] predicted, int lineCount)
    {
      var score = new GroupingScore { Files = 1, Lines = Math.Max(0, lineCount) };
      int boundaries = Math.Max(0, lineCount - 1);
      score.Boundaries = boundaries;
      for (int k = 0; k < boundaries; k++)
      {
        bool t = JoinAt(truth, k), p = JoinAt(predicted, k);
        if (t && p) score.TruePositives++;
        else if (!t && p) score.FalsePositives++;
        else if (t && !p) score.FalseNegatives++;
        else score.TrueNegatives++;
      }

      List<(int first, int last)> truthRanges = SnippetGrouping.JoinsToRanges(Normalize(truth, lineCount), lineCount);
      List<(int first, int last)> predictedRanges = SnippetGrouping.JoinsToRanges(Normalize(predicted, lineCount), lineCount);
      var predictedSet = new HashSet<(int, int)>(predictedRanges);
      score.TruthSnippets = truthRanges.Count;
      score.PredictedSnippets = predictedRanges.Count;
      foreach ((int first, int last) r in truthRanges)
      {
        bool multi = r.last > r.first;
        if (multi) score.TruthMultiLineSnippets++;
        if (predictedSet.Contains(r))
        {
          score.ExactSnippets++;
          if (multi) score.ExactMultiLineSnippets++;
          continue;
        }
        bool split = false;
        for (int k = r.first; k < r.last; k++)
          if (!JoinAt(predicted, k)) { split = true; break; }
        if (split) score.UnderMergedSnippets++;
        else score.OverMergedSnippets++;
      }
      return score;
    }

    /// <summary>Boundaries (kept positions k, meaning line k to k+1) where the two vectors disagree.</summary>
    public static List<int> Diff(bool[]? a, bool[]? b, int lineCount)
    {
      var diff = new List<int>();
      for (int k = 0; k < lineCount - 1; k++)
        if (JoinAt(a, k) != JoinAt(b, k)) diff.Add(k);
      return diff;
    }

    /// <summary>Number of boundaries that differ, e.g. how many joins a human flipped from a proposal.</summary>
    public static int Flips(bool[]? a, bool[]? b, int lineCount) => Diff(a, b, lineCount).Count;

    private static bool[] Normalize(bool[]? joins, int lineCount)
    {
      var result = new bool[Math.Max(0, lineCount)];
      for (int k = 0; k < result.Length - 1; k++) result[k] = JoinAt(joins, k);
      return result;
    }
  }


  /// <summary>One validation file of an evaluation set.</summary>
  public sealed class GroupingEvalFile
  {
    public string Path { get; init; } = "";
    /// <summary>Path relative to the set directory (forward slashes), the file's name in reports.</summary>
    public string RelativePath { get; init; } = "";
    public bool IsHoldout { get; init; }
    public GroupingValidationFile File { get; init; } = new();
    public string Name => System.IO.Path.GetFileName(Path);
  }


  /// <summary>
  /// A directory of validation files (plan §9.3). Every <c>*.grouping.json</c> below the set
  /// directory belongs to it; files under a folder named <c>holdout</c> (at any depth) form the
  /// hold-out set that prompts are never tuned on, and a separate hold-out directory can be
  /// given instead. Unreadable files are listed in <see cref="Errors"/>, not thrown.
  /// </summary>
  public sealed class GroupingEvalSet
  {
    public const string HoldoutDirName = "holdout";
    public const string FilePattern = "*.grouping.json";

    public string Dir { get; init; } = "";
    public string? HoldoutDir { get; init; }
    public List<GroupingEvalFile> Files { get; } = new();
    public List<(string path, string error)> Errors { get; } = new();

    public IEnumerable<GroupingEvalFile> Tuning => Files.Where(f => !f.IsHoldout);
    public IEnumerable<GroupingEvalFile> Holdout => Files.Where(f => f.IsHoldout);

    public static GroupingEvalSet Load(string dir, string? holdoutDir = null)
    {
      var set = new GroupingEvalSet { Dir = Path.GetFullPath(dir), HoldoutDir = holdoutDir == null ? null : Path.GetFullPath(holdoutDir) };
      if (Directory.Exists(set.Dir))
        foreach (string path in Directory.EnumerateFiles(set.Dir, FilePattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
          string relative = Path.GetRelativePath(set.Dir, path).Replace('\\', '/');
          bool holdout = relative.Split('/').Take(relative.Split('/').Length - 1).Any(seg => string.Equals(seg, HoldoutDirName, StringComparison.OrdinalIgnoreCase))
            || (set.HoldoutDir != null && IsUnder(path, set.HoldoutDir));
          set.Add(path, relative, holdout);
        }
      if (set.HoldoutDir != null && Directory.Exists(set.HoldoutDir) && !IsUnder(set.HoldoutDir, set.Dir))
        foreach (string path in Directory.EnumerateFiles(set.HoldoutDir, FilePattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
          set.Add(path, HoldoutDirName + "/" + Path.GetRelativePath(set.HoldoutDir, path).Replace('\\', '/'), true);
      return set;
    }

    private void Add(string path, string relative, bool holdout)
    {
      try
      {
        GroupingValidationFile file = GroupingValidationFile.Read(path);
        if (file.Lines.Count > 0 && file.Joins.Length < file.Lines.Count - 1)
          throw new InvalidDataException(FormattableString.Invariant($"{file.Joins.Length} joins for {file.Lines.Count} lines"));
        Files.Add(new GroupingEvalFile { Path = path, RelativePath = relative, IsHoldout = holdout, File = file });
      }
      catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidDataException || ex is FormatException)
      {
        Errors.Add((path, ex.Message));
      }
    }

    private static bool IsUnder(string path, string dir)
    {
      string full = Path.GetFullPath(path);
      string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
  }
}
