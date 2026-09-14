//  Copyright (C) 2026 fkzys and contributors
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  subs2srs is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with subs2srs.  If not, see <http://www.gnu.org/licenses/>.
//
//////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;

namespace subs2srs
{
  /// <summary>
  /// The limits every grouping (rules, model, manual editor) must respect.
  /// </summary>
  public class SnippetLimits
  {
    /// <summary>Maximum trimmed duration of a snippet, in milliseconds.</summary>
    public int MaxSnippetMs { get; set; } = 15_000;

    /// <summary>
    /// An internal gap longer than this is shortened to this length when the
    /// media is cut; the same value is used when computing trimmed durations.
    /// </summary>
    public int GapKeepMs { get; set; } = 500;

    /// <summary>Padding added at both ends of a card (audio pad start + end), counted toward the limit.</summary>
    public int PadMs { get; set; } = 0;

    public SnippetLimits() { }

    public SnippetLimits(int maxSnippetMs, int gapKeepMs, int padMs = 0)
    {
      MaxSnippetMs = maxSnippetMs;
      GapKeepMs = gapKeepMs;
      PadMs = padMs;
    }

    /// <summary>Build from the current project settings.</summary>
    public static SnippetLimits FromSettings()
    {
      var s = Settings.Instance.Snippets;
      int pad = 0;
      if (Settings.Instance.AudioClips.Enabled && Settings.Instance.AudioClips.PadEnabled)
        pad = Settings.Instance.AudioClips.PadStart + Settings.Instance.AudioClips.PadEnd;
      return new SnippetLimits(Math.Max(1, s.MaxSnippetSeconds) * 1000, Math.Max(0, s.GapKeepMs), pad);
    }
  }

  /// <summary>
  /// Options for the deterministic (rule-based) grouper.
  /// </summary>
  public class RuleGrouperOptions
  {
    /// <summary>Two neighbouring lines further apart than this are never joined.</summary>
    public int MaxJoinGapMs { get; set; } = 1500;

    /// <summary>
    /// When true, a join also needs a cue: the earlier line ends with one of
    /// <see cref="CueChars"/>, or (if enabled) the actor changes.
    /// </summary>
    public bool RequireCue { get; set; } = true;

    /// <summary>Characters at the end of a line that suggest the next line belongs with it.</summary>
    public string CueChars { get; set; } = "?？…→、,";

    /// <summary>Treat a change of actor (when both lines have one) as a cue.</summary>
    public bool JoinOnActorChange { get; set; } = true;

    public static RuleGrouperOptions FromSettings()
    {
      var s = Settings.Instance.Snippets;
      return new RuleGrouperOptions
      {
        MaxJoinGapMs = Math.Max(0, s.RulesMaxJoinGapMs),
        RequireCue = s.RulesRequireCue,
        CueChars = s.RulesCueChars ?? "",
        JoinOnActorChange = s.RulesJoinOnActorChange,
      };
    }
  }

  /// <summary>
  /// Everything about turning a list of lines plus a join vector into snippets.
  ///
  /// Vocabulary:
  /// - "lines": one episode after <c>inactivateLines</c>; each is a plain InfoCombined.
  /// - "kept": the indices of the active lines. Neighbours are adjacent kept lines,
  ///   so two kept lines with an omitted line between them are neighbours.
  /// - "join vector": <c>joins[i]</c> means "line i is attached to the next kept
  ///   line". It is indexed by the full line index; entries for inactive lines are
  ///   ignored. Length equals the number of lines (the last entry is always unused).
  /// - "kept joins": the projection of the join vector onto the kept lines
  ///   (length = kept.Length - 1 conceptually; stored with length kept.Length).
  ///
  /// A snippet covering kept lines a..b is built as if the omitted lines between
  /// them did not exist: their text is not merged in, their dialogue ranges are
  /// not part of the card's segments (the media workers cut them away) and they
  /// do not count toward the duration limit. They travel along in
  /// <see cref="InfoCombined.Parts"/> only so that <see cref="Flatten"/> can put
  /// them back in place.
  /// </summary>
  public static class SnippetGrouping
  {
    // ── kept lines and gaps ─────────────────────────────────────────────

    public static int[] KeptIndices(IReadOnlyList<InfoCombined> lines)
    {
      var kept = new List<int>(lines.Count);
      for (int i = 0; i < lines.Count; i++)
        if (lines[i].Active) kept.Add(i);
      return kept.ToArray();
    }

    /// <summary>Gap in milliseconds from the end of <paramref name="a"/> to the start of <paramref name="b"/>; never negative.</summary>
    public static int GapMs(InfoCombined a, InfoCombined b)
    {
      double ms = (b.Subs1.StartTime - a.Subs1.EndTime).TotalMilliseconds;
      return ms > 0 ? (int)Math.Round(ms) : 0;
    }

    // ── segments and trimmed durations ─────────────────────────────────

    /// <summary>
    /// The dialogue segments of the kept lines among lines[first..last] (full
    /// indices), sorted and merged. A single-line range (first == last) yields
    /// that line's segments whether or not it is kept.
    /// </summary>
    public static List<TimeRange> SegmentsOf(IReadOnlyList<InfoCombined> lines, int first, int last)
    {
      var segs = new List<TimeRange>();
      for (int i = first; i <= last; i++)
        if (lines[i].Active || first == last) segs.AddRange(lines[i].Segments());
      return MergeOverlaps(segs);
    }

    /// <summary>The dialogue segments of the omitted lines among lines[first..last], sorted and merged.</summary>
    public static List<TimeRange> OmittedSegmentsOf(IReadOnlyList<InfoCombined> lines, int first, int last)
    {
      var segs = new List<TimeRange>();
      for (int i = first; i <= last; i++)
        if (!lines[i].Active && first != last) segs.AddRange(lines[i].Segments());
      return MergeOverlaps(segs);
    }

    public static List<TimeRange> MergeOverlaps(List<TimeRange> ranges)
    {
      var sorted = ranges.OrderBy(r => r.Start).Select(r => new TimeRange(r.Start, r.End)).ToList();
      var merged = new List<TimeRange>();
      foreach (TimeRange r in sorted)
      {
        if (merged.Count > 0 && r.Start <= merged[^1].End)
        {
          if (r.End > merged[^1].End) merged[^1].End = r.End;
        }
        else
        {
          merged.Add(r);
        }
      }
      return merged;
    }

    /// <summary>
    /// Duration of the media that would be produced for these segments:
    /// the segments themselves plus min(gap, gapKeepMs) for each gap between them.
    /// </summary>
    public static int TrimmedDurationMs(IReadOnlyList<TimeRange> segments, int gapKeepMs)
    {
      double total = 0;
      for (int i = 0; i < segments.Count; i++)
      {
        total += segments[i].Duration.TotalMilliseconds;
        if (i > 0)
        {
          double gap = (segments[i].Start - segments[i - 1].End).TotalMilliseconds;
          if (gap > 0) total += Math.Min(gap, gapKeepMs);
        }
      }
      return (int)Math.Round(total);
    }

    /// <summary>Trimmed duration of lines[first..last] including the pad from <paramref name="limits"/>.</summary>
    public static int TrimmedDurationMs(IReadOnlyList<InfoCombined> lines, int first, int last, SnippetLimits limits)
    {
      return TrimmedDurationMs(SegmentsOf(lines, first, last), limits.GapKeepMs) + limits.PadMs;
    }

    /// <summary>
    /// The ranges of media to keep for a card: every gap longer than
    /// <paramref name="gapKeepMs"/> is shortened to that length, half kept on
    /// each side of the cut; shorter gaps are untouched. Pads extend the outer
    /// edges. Adjacent ranges that touch after trimming are merged.
    /// </summary>
    public static List<TimeRange> TrimmedRanges(IReadOnlyList<TimeRange> segments, int gapKeepMs, int padStartMs, int padEndMs)
    {
      var result = new List<TimeRange>();
      if (segments.Count == 0) return result;

      TimeSpan curStart = UtilsSubs.applyTimePad(segments[0].Start, -padStartMs);
      TimeSpan curEnd = segments[0].End;

      for (int i = 1; i < segments.Count; i++)
      {
        double gap = (segments[i].Start - curEnd).TotalMilliseconds;
        if (gap > gapKeepMs)
        {
          double half = gapKeepMs / 2.0;
          result.Add(new TimeRange(curStart, curEnd + TimeSpan.FromMilliseconds(Math.Floor(half))));
          curStart = segments[i].Start - TimeSpan.FromMilliseconds(Math.Ceiling(half));
        }
        curEnd = segments[i].End;
      }

      result.Add(new TimeRange(curStart, UtilsSubs.applyTimePad(curEnd, padEndMs)));
      return result;
    }

    // ── join vectors ────────────────────────────────────────────────────

    /// <summary>Maximal runs of joined elements as inclusive (first, last) index pairs over an n-element list.</summary>
    public static List<(int first, int last)> JoinsToRanges(bool[] joins, int n)
    {
      var ranges = new List<(int, int)>();
      int start = 0;
      for (int i = 0; i < n; i++)
      {
        bool joinedToNext = i < n - 1 && i < joins.Length && joins[i];
        if (!joinedToNext)
        {
          ranges.Add((start, i));
          start = i + 1;
        }
      }
      return ranges;
    }

    /// <summary>Inverse of <see cref="JoinsToRanges"/>; indices outside 0..n-1 are ignored.</summary>
    public static bool[] RangesToJoins(IEnumerable<(int first, int last)> ranges, int n)
    {
      var joins = new bool[Math.Max(0, n)];
      foreach (var (first, last) in ranges)
      {
        for (int i = Math.Max(0, first); i < Math.Min(last, n - 1); i++)
          joins[i] = true;
      }
      return joins;
    }

    /// <summary>Project a full-index join vector onto the kept lines.</summary>
    public static bool[] ProjectToKept(bool[] fullJoins, int[] kept)
    {
      var keptJoins = new bool[kept.Length];
      for (int k = 0; k < kept.Length - 1; k++)
        keptJoins[k] = kept[k] < fullJoins.Length && fullJoins[kept[k]];
      return keptJoins;
    }

    /// <summary>Write a kept-projection join vector back into a full-index vector (other entries untouched).</summary>
    public static void ApplyKeptJoins(bool[] fullJoins, int[] kept, bool[] keptJoins)
    {
      for (int k = 0; k < kept.Length; k++)
      {
        if (kept[k] >= fullJoins.Length) continue;
        fullJoins[kept[k]] = k < kept.Length - 1 && k < keptJoins.Length && keptJoins[k];
      }
    }

    /// <summary>A full-index join vector with nothing joined.</summary>
    public static bool[] NoJoins(int n) => new bool[Math.Max(0, n)];

    // ── validation / repair ─────────────────────────────────────────────

    /// <summary>
    /// Enforce the limits on a kept-projection join vector: every run whose
    /// trimmed duration exceeds the limit is split at its largest internal gap
    /// (between kept lines) until every piece fits. Returns a new vector.
    /// Messages describing each split are appended to <paramref name="log"/>.
    /// </summary>
    public static bool[] Repair(IReadOnlyList<InfoCombined> lines, int[] kept, bool[] keptJoins,
      SnippetLimits limits, List<string>? log = null)
    {
      var joins = (bool[])keptJoins.Clone();
      if (joins.Length < kept.Length) Array.Resize(ref joins, kept.Length);
      if (kept.Length > 0) joins[kept.Length - 1] = false;

      var work = new Stack<(int first, int last)>();
      foreach (var r in JoinsToRanges(joins, kept.Length)) work.Push(r);

      while (work.Count > 0)
      {
        var (first, last) = work.Pop();
        if (first == last) continue;

        int dur = TrimmedDurationMs(lines, kept[first], kept[last], limits);
        if (dur <= limits.MaxSnippetMs) continue;

        // Split at the largest gap between consecutive kept lines inside the run.
        int bestK = first;
        int bestGap = -1;
        for (int k = first; k < last; k++)
        {
          int gap = GapMs(lines[kept[k]], lines[kept[k + 1]]);
          if (gap > bestGap) { bestGap = gap; bestK = k; }
        }

        joins[bestK] = false;
        log?.Add(FormattableString.Invariant(
          $"Snippet of kept lines {first}-{last} is {dur / 1000.0:0.0} s (limit {limits.MaxSnippetMs / 1000.0:0.0} s); split after kept line {bestK} (gap {bestGap} ms)"));

        work.Push((first, bestK));
        work.Push((bestK + 1, last));
      }

      return joins;
    }

    // ── materialisation ─────────────────────────────────────────────────

    /// <summary>
    /// Replace runs of joined kept lines with snippet InfoCombineds. Lines not
    /// covered by any snippet (including inactive ones) are passed through
    /// unchanged, in order. Omitted lines between a snippet's first and last
    /// kept line are carried in its Parts (for <see cref="Flatten"/>) but
    /// contribute no text, media or duration to it.
    /// </summary>
    public static List<InfoCombined> Materialize(IReadOnlyList<InfoCombined> lines, bool[] fullJoins,
      string separator, Func<IReadOnlyList<InfoCombined>, int, int, string?>? noteFor = null)
    {
      int[] kept = KeptIndices(lines);
      bool[] keptJoins = ProjectToKept(fullJoins, kept);
      var output = new List<InfoCombined>(lines.Count);

      int next = 0;
      foreach (var (kFirst, kLast) in JoinsToRanges(keptJoins, kept.Length))
      {
        int first = kept[kFirst];
        int last = kept[kLast];

        for (; next < first; next++) output.Add(lines[next]);

        if (first == last)
        {
          output.Add(lines[first]);
        }
        else
        {
          var parts = new List<InfoCombined>(last - first + 1);
          for (int i = first; i <= last; i++) parts.Add(lines[i]);
          output.Add(InfoCombined.CreateSnippet(parts, separator, noteFor?.Invoke(lines, first, last)));
        }
        next = last + 1;
      }

      for (; next < lines.Count; next++) output.Add(lines[next]);
      return output;
    }

    /// <summary>
    /// Inverse of <see cref="Materialize"/>: expand snippets back into their
    /// parts and return the flat lines plus the full-index join vector.
    /// </summary>
    public static (List<InfoCombined> lines, bool[] joins) Flatten(IReadOnlyList<InfoCombined> cards)
    {
      var lines = new List<InfoCombined>();
      var joinAt = new List<int>();
      foreach (InfoCombined card in cards)
      {
        if (card.Parts != null && card.Parts.Count > 0)
        {
          int first = lines.Count;
          lines.AddRange(card.Parts);
          // join every kept part to the next kept part inside the snippet
          int lastKept = -1;
          for (int i = first; i < lines.Count; i++)
          {
            if (!lines[i].Active) continue;
            if (lastKept >= 0) joinAt.Add(lastKept);
            lastKept = i;
          }
        }
        else
        {
          lines.Add(card);
        }
      }
      var joins = NoJoins(lines.Count);
      foreach (int i in joinAt) joins[i] = true;
      return (lines, joins);
    }
  }

  /// <summary>
  /// Deterministic grouper: merge a kept line into the running snippet when the
  /// gap to the previous kept line is small, the trimmed duration stays within
  /// the limit, and (optionally) there is a cue: the previous line ends with a
  /// question/continuation mark or the speaker changes.
  /// Zero cost, and the fallback when no model is configured.
  /// </summary>
  public static class RuleBasedGrouper
  {
    /// <summary>Returns a kept-projection join vector (length = kept.Length).</summary>
    public static bool[] Group(IReadOnlyList<InfoCombined> lines, int[] kept, SnippetLimits limits, RuleGrouperOptions options)
    {
      var joins = new bool[kept.Length];
      int runStart = 0;

      for (int k = 0; k + 1 < kept.Length; k++)
      {
        InfoCombined a = lines[kept[k]];
        InfoCombined b = lines[kept[k + 1]];

        bool join = SnippetGrouping.GapMs(a, b) <= options.MaxJoinGapMs
                    && (!options.RequireCue || HasCue(a, b, options))
                    && SnippetGrouping.TrimmedDurationMs(lines, kept[runStart], kept[k + 1], limits) <= limits.MaxSnippetMs;

        joins[k] = join;
        if (!join) runStart = k + 1;
      }

      return joins;
    }

    /// <summary>Full-index join vector for one episode, already limit-checked.</summary>
    public static bool[] GroupEpisode(IReadOnlyList<InfoCombined> lines, SnippetLimits limits, RuleGrouperOptions options)
    {
      int[] kept = SnippetGrouping.KeptIndices(lines);
      bool[] keptJoins = Group(lines, kept, limits, options);
      var full = SnippetGrouping.NoJoins(lines.Count);
      SnippetGrouping.ApplyKeptJoins(full, kept, keptJoins);
      return full;
    }

    /// <summary>Short human-readable reason why lines first..last were grouped.</summary>
    public static string Note(IReadOnlyList<InfoCombined> lines, int first, int last, RuleGrouperOptions options)
    {
      var reasons = new List<string>();
      int[] kept = SnippetGrouping.KeptIndices(lines).Where(i => i >= first && i <= last).ToArray();
      for (int k = 0; k + 1 < kept.Length; k++)
      {
        InfoCombined a = lines[kept[k]];
        InfoCombined b = lines[kept[k + 1]];
        string why = EndsWithCue(a, options) ? "cue" : ActorChanges(a, b) ? "speaker change" : "gap";
        reasons.Add($"{why} {SnippetGrouping.GapMs(a, b)} ms");
      }
      return "rules: " + string.Join(", ", reasons);
    }

    public static bool HasCue(InfoCombined a, InfoCombined b, RuleGrouperOptions options)
    {
      return EndsWithCue(a, options) || (options.JoinOnActorChange && ActorChanges(a, b));
    }

    public static bool EndsWithCue(InfoCombined a, RuleGrouperOptions options)
    {
      string text = (a.Subs1.Text ?? "").TrimEnd();
      // ignore closing quotes/brackets after the cue character
      text = text.TrimEnd('」', '』', '）', ')', '"', '\'', '”', '’');
      if (text.Length == 0 || string.IsNullOrEmpty(options.CueChars)) return false;
      return options.CueChars.Contains(text[^1]);
    }

    public static bool ActorChanges(InfoCombined a, InfoCombined b)
    {
      string x = (a.Subs1.Actor ?? "").Trim();
      string y = (b.Subs1.Actor ?? "").Trim();
      return x.Length > 0 && y.Length > 0 && !string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }
  }
}
