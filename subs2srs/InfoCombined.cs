//  Copyright (C) 2009-2016 Christopher Brochtrup
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
using System.Text.Json.Serialization;

namespace subs2srs
{
  /// <summary>
  /// A contiguous time range inside the media. Used for the segments of a
  /// multi-line snippet (one per part) so that the gaps between the parts can
  /// be removed from the generated media.
  /// </summary>
  public class TimeRange
  {
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }

    public TimeRange() { }

    public TimeRange(TimeSpan start, TimeSpan end)
    {
      Start = start;
      End = end;
    }

    [JsonIgnore]
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    public override string ToString() => $"{UtilsSubs.timeToString(Start)}-{UtilsSubs.timeToString(End)}";
  }

  /// <summary>
  /// Represents paired lines: Subs1 and its corresponding Subs2.
  ///
  /// A card is one InfoCombined. Normally it is one subtitle line; when
  /// <see cref="Parts"/> is set, it is a snippet built from several consecutive
  /// lines and Subs1/Subs2 hold the merged view (joined text, first start time,
  /// last end time), so that every existing consumer keeps working unchanged.
  /// Only the media workers look at <see cref="Segments"/> to remove dead space.
  /// </summary>
  public class InfoCombined
  {
    public InfoLine Subs1 { get; set; }
    public InfoLine Subs2 { get; set; }

    /// <summary>
    /// Is the line active? (That is, will it be processed?)
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// Is the line only needed for context information?
    /// If true, Active is false for this line.
    /// </summary>
    public bool OnlyNeededForContext { get; set; }

    /// <summary>
    /// The lines this snippet was built from, in order; null for a plain single line.
    /// Each part is a plain (non-snippet) InfoCombined.
    /// </summary>
    public List<InfoCombined>? Parts { get; set; }

    /// <summary>
    /// Short reason for the grouping (from the rules or, later, the model). Shown in the preview.
    /// </summary>
    public string? GroupNote { get; set; }

    public InfoCombined()
    {
      Subs1 = new InfoLine();
      Subs2 = new InfoLine();
      Active = true;
      OnlyNeededForContext = false;
    }

    public InfoCombined(InfoLine subs1, InfoLine subs2)
    {
      Subs1 = subs1;
      Subs2 = subs2;
      Active = true;
      OnlyNeededForContext = false;
    }

    public InfoCombined(InfoLine subs1, InfoLine subs2, bool active)
    {
      Subs1 = subs1;
      Subs2 = subs2;
      Active = active;
      OnlyNeededForContext = false;
    }

    /// <summary>True when this card was built from more than one subtitle line.</summary>
    [JsonIgnore]
    public bool IsSnippet => Parts != null && Parts.Count > 1;

    /// <summary>Number of subtitle lines this card is built from (1 for a plain line).</summary>
    [JsonIgnore]
    public int PartCount => Parts == null ? 1 : Parts.Count;

    /// <summary>
    /// The time ranges of spoken dialogue in this card, in order, using Subs1 timings.
    /// A plain line yields its own range (or the ranges recorded by the
    /// sentence-join feature, when they are consistent with the line's timing).
    /// A snippet yields the ranges of its parts. Ranges never overlap and are sorted.
    /// </summary>
    public List<TimeRange> Segments()
    {
      var result = new List<TimeRange>();

      if (Parts != null && Parts.Count > 0)
      {
        foreach (InfoCombined part in Parts)
          result.AddRange(part.Segments());
      }
      else if (Subs1.Segments != null && Subs1.Segments.Count > 0 && segmentsAreConsistent(Subs1))
      {
        foreach (TimeRange r in Subs1.Segments)
          result.Add(new TimeRange(r.Start, r.End));
      }
      else
      {
        result.Add(new TimeRange(Subs1.StartTime, Subs1.EndTime));
      }

      return mergeOverlaps(result);
    }

    /// <summary>
    /// Build a snippet from consecutive lines. Subs1/Subs2 become the merged view:
    /// text joined with <paramref name="separator"/> (empty texts skipped),
    /// StartTime of the first part, EndTime of the last part, actor of the first part.
    /// A single part yields a clone-free wrapper that behaves like the part itself.
    /// </summary>
    public static InfoCombined CreateSnippet(IList<InfoCombined> parts, string separator, string? note = null)
    {
      if (parts == null || parts.Count == 0)
        throw new ArgumentException("A snippet needs at least one part.", nameof(parts));

      if (parts.Count == 1)
      {
        InfoCombined only = parts[0];
        only.GroupNote = note ?? only.GroupNote;
        return only;
      }

      var snippet = new InfoCombined
      {
        Subs1 = mergeLines(parts, true, separator),
        Subs2 = mergeLines(parts, false, separator),
        Active = true,
        OnlyNeededForContext = false,
        Parts = new List<InfoCombined>(parts),
        GroupNote = note
      };

      return snippet;
    }

    private static InfoLine mergeLines(IList<InfoCombined> parts, bool subs1, string separator)
    {
      var texts = new List<string>(parts.Count);
      foreach (InfoCombined part in parts)
      {
        string t = (subs1 ? part.Subs1 : part.Subs2).Text ?? "";
        if (t.Trim().Length > 0) texts.Add(t);
      }

      InfoLine first = subs1 ? parts[0].Subs1 : parts[0].Subs2;
      InfoLine last = subs1 ? parts[parts.Count - 1].Subs1 : parts[parts.Count - 1].Subs2;

      TimeSpan start = first.StartTime;
      TimeSpan end = last.EndTime;
      foreach (InfoCombined part in parts)
      {
        InfoLine l = subs1 ? part.Subs1 : part.Subs2;
        if (l.StartTime < start) start = l.StartTime;
        if (l.EndTime > end) end = l.EndTime;
      }

      return new InfoLine(start, end, string.Join(separator, texts), first.Actor ?? "");
    }

    private static bool segmentsAreConsistent(InfoLine line)
    {
      TimeSpan prevEnd = line.StartTime;
      foreach (TimeRange r in line.Segments!)
      {
        if (r.Start < line.StartTime || r.End > line.EndTime || r.End <= r.Start || r.Start < prevEnd)
          return false;
        prevEnd = r.End;
      }
      return true;
    }

    private static List<TimeRange> mergeOverlaps(List<TimeRange> ranges)
    {
      if (ranges.Count <= 1) return ranges;
      ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
      var merged = new List<TimeRange> { ranges[0] };
      for (int i = 1; i < ranges.Count; i++)
      {
        TimeRange last = merged[merged.Count - 1];
        TimeRange cur = ranges[i];
        if (cur.Start <= last.End)
        {
          if (cur.End > last.End) last.End = cur.End;
        }
        else
        {
          merged.Add(cur);
        }
      }
      return merged;
    }

    public override string ToString()
    {
      return $"{Active}, {OnlyNeededForContext}, {Subs1.StartTime}, {Subs1.EndTime}";
    }
  }
}
