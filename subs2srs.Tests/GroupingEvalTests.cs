using System;
using System.IO;
using System.Linq;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
  public class GroupingEvalTests
  {
    private static bool[] J(string pattern) => pattern.Select(c => c == '1').ToArray();

    // ── boundary metrics ───────────────────────────────────────────────

    [Fact]
    public void Score_IdenticalVectors_IsPerfect()
    {
      GroupingScore s = GroupingScorer.Score(J("1010"), J("1010"), 5);
      Assert.Equal(4, s.Boundaries);
      Assert.Equal(2, s.TruePositives);
      Assert.Equal(0, s.FalsePositives);
      Assert.Equal(0, s.FalseNegatives);
      Assert.Equal(2, s.TrueNegatives);
      Assert.Equal(1.0, s.Precision);
      Assert.Equal(1.0, s.Recall);
      Assert.Equal(1.0, s.F1);
      Assert.Equal(3, s.TruthSnippets);          // (0-1)(2-3)(4)
      Assert.Equal(3, s.ExactSnippets);
      Assert.Equal(2, s.TruthMultiLineSnippets);
      Assert.Equal(2, s.ExactMultiLineSnippets);
      Assert.Equal(1.0, s.ExactRate);
      Assert.Equal(1, s.Files);
      Assert.Equal(5, s.Lines);
    }

    [Fact]
    public void Score_NothingPredicted_HasZeroRecall_AndPrecisionOne()
    {
      GroupingScore s = GroupingScorer.Score(J("110"), J("000"), 4);
      Assert.Equal(0, s.TruePositives);
      Assert.Equal(2, s.FalseNegatives);
      Assert.Equal(0.0, s.Recall);
      Assert.Equal(0.0, s.Precision); // nothing predicted while joins were expected
      Assert.Equal(0.0, s.F1);
      Assert.Equal(2, s.TruthSnippets);        // (0-2)(3)
      Assert.Equal(1, s.ExactSnippets);        // the single line 3
      Assert.Equal(1, s.UnderMergedSnippets);  // 0-2 was split
      Assert.Equal(0, s.OverMergedSnippets);
      Assert.Equal(4, s.PredictedSnippets);
    }

    [Fact]
    public void Score_NoJoinsAnywhere_CountsAsAgreement()
    {
      GroupingScore s = GroupingScorer.Score(J("000"), J("000"), 4);
      Assert.Equal(1.0, s.Precision);
      Assert.Equal(1.0, s.Recall);
      Assert.Equal(1.0, s.F1);
      Assert.Equal(4, s.ExactSnippets);
      Assert.Equal(1.0, s.MultiLineExactRate); // vacuous
    }

    [Fact]
    public void Score_MixedErrors_ClassifiesOverAndUnderMerges()
    {
      // truth: (0-1)(2)(3-4-5)(6)   predicted: (0-1-2)(3-4)(5)(6)
      GroupingScore s = GroupingScorer.Score(J("100110"), J("110100"), 7);
      Assert.Equal(2, s.TruePositives);   // 0-1 and 3-4
      Assert.Equal(1, s.FalsePositives);  // 1-2 over-merged
      Assert.Equal(1, s.FalseNegatives);  // 4-5 under-merged
      Assert.Equal(2, s.TrueNegatives);
      Assert.Equal(2.0 / 3, s.Precision, 9);
      Assert.Equal(2.0 / 3, s.Recall, 9);
      Assert.Equal(2.0 / 3, s.F1, 9);
      Assert.Equal(4, s.TruthSnippets);
      Assert.Equal(1, s.ExactSnippets);        // (6)
      Assert.Equal(2, s.OverMergedSnippets);   // (0-1) and (2) were swallowed into 0-1-2
      Assert.Equal(1, s.UnderMergedSnippets);  // (3-4-5) was split
      Assert.Equal(2, s.TruthMultiLineSnippets);
      Assert.Equal(0, s.ExactMultiLineSnippets);
      Assert.Equal(0.25, s.ExactRate);
    }

    [Fact]
    public void Score_ToleratesShortOrLongVectors()
    {
      // A cache result carries lineCount entries (last unused); a validation file carries lineCount - 1.
      GroupingScore a = GroupingScorer.Score(J("10"), J("100"), 3);
      GroupingScore b = GroupingScorer.Score(J("10"), J("1"), 3);
      Assert.Equal(1.0, a.F1);
      Assert.Equal(1.0, b.F1);
      Assert.Equal(2, a.Boundaries);
      GroupingScore empty = GroupingScorer.Score(Array.Empty<bool>(), Array.Empty<bool>(), 0);
      Assert.Equal(0, empty.Boundaries);
      Assert.Equal(0, empty.TruthSnippets);
      Assert.Equal(1.0, empty.F1);
    }

    // ── aggregate ──────────────────────────────────────────────────────

    [Fact]
    public void Sum_IsMicroAverage_AndSumsUsage()
    {
      GroupingScore a = GroupingScorer.Score(J("1111"), J("1111"), 5); // 4 TP
      GroupingScore b = GroupingScorer.Score(J("0000"), J("1100"), 5); // 2 FP
      a.InputTokens = 100; a.OutputTokens = 10; a.Usd = 0.5;
      b.InputTokens = 200; b.OutputTokens = 20; b.Usd = null;
      GroupingScore sum = GroupingScore.Sum(new[] { a, b });
      Assert.Equal(2, sum.Files);
      Assert.Equal(10, sum.Lines);
      Assert.Equal(8, sum.Boundaries);
      Assert.Equal(4, sum.TruePositives);
      Assert.Equal(2, sum.FalsePositives);
      Assert.Equal(4.0 / 6, sum.Precision, 9);
      Assert.Equal(1.0, sum.Recall);
      Assert.Equal(300, sum.InputTokens);
      Assert.Equal(30, sum.OutputTokens);
      Assert.Equal(0.5, sum.Usd);
      Assert.Null(GroupingScore.Sum(new[] { b }).Usd);
      Assert.Null(GroupingScore.Sum(Array.Empty<GroupingScore>()).Usd);
    }

    // ── diff ───────────────────────────────────────────────────────────

    [Fact]
    public void Diff_ListsDisagreeingBoundaries_IgnoringTheUnusedLastSlot()
    {
      Assert.Equal(new[] { 0 }, GroupingScorer.Diff(J("1010"), J("0011"), 4)); // slot 3 is past the last boundary
      Assert.Equal(1, GroupingScorer.Flips(J("1010"), J("0011"), 4));
      Assert.Equal(new[] { 0, 2 }, GroupingScorer.Diff(J("1010"), J("0001"), 4));
      Assert.Empty(GroupingScorer.Diff(J("10"), J("100"), 3));
      Assert.Equal(new[] { 1 }, GroupingScorer.Diff(null, J("01"), 3));
    }

    // ── set discovery ──────────────────────────────────────────────────

    [Fact]
    public void Set_FindsFilesRecursively_AndTreatsHoldoutFolderAsHoldout()
    {
      using var scope = new TestScope();
      string set = Path.Combine(scope.TempDir, "validation");
      WriteFile(Path.Combine(set, "show_1.grouping.json"), 3);
      WriteFile(Path.Combine(set, "other", "show_2.grouping.json"), 3);
      WriteFile(Path.Combine(set, "holdout", "show_3.grouping.json"), 3);
      WriteFile(Path.Combine(set, "holdout", "deep", "show_4.grouping.json"), 3);
      File.WriteAllText(Path.Combine(set, "notes.json"), "{}");
      File.WriteAllText(Path.Combine(set, "broken.grouping.json"), "{ not json");

      GroupingEvalSet loaded = GroupingEvalSet.Load(set);
      Assert.Equal(4, loaded.Files.Count);
      Assert.Equal(new[] { "other/show_2.grouping.json", "show_1.grouping.json" }, loaded.Tuning.Select(f => f.RelativePath).ToArray());
      Assert.Equal(new[] { "holdout/deep/show_4.grouping.json", "holdout/show_3.grouping.json" }, loaded.Holdout.Select(f => f.RelativePath).ToArray());
      Assert.Single(loaded.Errors);
      Assert.EndsWith("broken.grouping.json", loaded.Errors[0].path);
      Assert.Equal(3, loaded.Files[0].File.Lines.Count);
    }

    [Fact]
    public void Set_ExplicitHoldoutDirectory_InsideOrOutsideTheSet()
    {
      using var scope = new TestScope();
      string set = Path.Combine(scope.TempDir, "validation");
      string outside = Path.Combine(scope.TempDir, "secret");
      WriteFile(Path.Combine(set, "a.grouping.json"), 2);
      WriteFile(Path.Combine(set, "later", "b.grouping.json"), 2);
      WriteFile(Path.Combine(outside, "c.grouping.json"), 2);

      GroupingEvalSet inside = GroupingEvalSet.Load(set, Path.Combine(set, "later"));
      Assert.Equal(new[] { "a.grouping.json" }, inside.Tuning.Select(f => f.RelativePath).ToArray());
      Assert.Equal(new[] { "later/b.grouping.json" }, inside.Holdout.Select(f => f.RelativePath).ToArray());

      GroupingEvalSet external = GroupingEvalSet.Load(set, outside);
      Assert.Equal(2, external.Tuning.Count());
      Assert.Equal(new[] { "holdout/c.grouping.json" }, external.Holdout.Select(f => f.RelativePath).ToArray());
      Assert.StartsWith(outside, external.Holdout.First().Path, StringComparison.OrdinalIgnoreCase);

      Assert.Empty(GroupingEvalSet.Load(Path.Combine(scope.TempDir, "missing")).Files);
    }

    [Fact]
    public void Set_RejectsAFileWithTooFewJoins()
    {
      using var scope = new TestScope();
      string set = Path.Combine(scope.TempDir, "validation");
      GroupingValidationFile file = MakeFile(3);
      file.Joins = new[] { true }; // needs 2
      file.Write(Path.Combine(set, "short.grouping.json"));
      GroupingEvalSet loaded = GroupingEvalSet.Load(set);
      Assert.Empty(loaded.Files);
      Assert.Contains("1 joins for 3 lines", loaded.Errors[0].error);
    }

    [Fact]
    public void Limits_ConvertBackToSnippetLimits()
    {
      var limits = new GroupingValidationFile.LimitsInfo { MaxSnippetSeconds = 12.5, GapKeepMs = 400, PadMs = 50 };
      SnippetLimits s = limits.ToSnippetLimits();
      Assert.Equal(12_500, s.MaxSnippetMs);
      Assert.Equal(400, s.GapKeepMs);
      Assert.Equal(50, s.PadMs);
    }

    internal static GroupingValidationFile MakeFile(int lines, bool[]? joins = null)
    {
      var file = new GroupingValidationFile();
      file.Limits.MaxSnippetSeconds = 15;
      file.Limits.GapKeepMs = 500;
      for (int i = 0; i < lines; i++)
        file.Lines.Add(new GroupingValidationFile.LineInfo
        {
          I = i,
          S = GroupingValidationFile.FormatTime(TimeSpan.FromSeconds(i * 1.5)),
          E = GroupingValidationFile.FormatTime(TimeSpan.FromSeconds(i * 1.5 + 1)),
          T = "line " + i,
        });
      file.Joins = joins ?? new bool[Math.Max(0, lines - 1)];
      return file;
    }

    private static void WriteFile(string path, int lines) => MakeFile(lines).Write(path);
  }
}
