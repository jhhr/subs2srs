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
using System.Globalization;
using System.Text;

namespace subs2srs
{
  /// <summary>
  /// ffmpeg argument builders for cutting several time ranges of one input into
  /// one output (dead-space removal inside a snippet).
  /// </summary>
  public static class UtilsGapRemoval
  {
    /// <summary>Seconds with millisecond precision, invariant culture (ffmpeg expression syntax).</summary>
    public static string FormatSeconds(TimeSpan t) =>
      Math.Max(0, t.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Move every range so that <paramref name="origin"/> becomes 0 (for inputs that were seeked/demuxed from origin).</summary>
    public static List<TimeRange> Relative(IReadOnlyList<TimeRange> ranges, TimeSpan origin)
    {
      var result = new List<TimeRange>(ranges.Count);
      foreach (TimeRange r in ranges)
      {
        TimeSpan s = r.Start - origin;
        TimeSpan e = r.End - origin;
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (e < s) e = s;
        result.Add(new TimeRange(s, e));
      }
      return result;
    }

    /// <summary>
    /// <c>between(t,a,b)+between(t,c,d)+...</c> for the select/aselect filters.
    /// </summary>
    public static string BetweenExpression(IReadOnlyList<TimeRange> ranges)
    {
      var sb = new StringBuilder();
      for (int i = 0; i < ranges.Count; i++)
      {
        if (i > 0) sb.Append('+');
        sb.Append("between(t,").Append(FormatSeconds(ranges[i].Start)).Append(',')
          .Append(FormatSeconds(ranges[i].End)).Append(')');
      }
      return sb.ToString();
    }

    /// <summary>
    /// Audio filter that keeps only the given ranges and closes the holes:
    /// <c>aselect='...',asetpts=N/SR/TB</c>. Times are input timestamps.
    /// </summary>
    public static string AudioSelectFilter(IReadOnlyList<TimeRange> ranges) =>
      $"aselect='{BetweenExpression(ranges)}',asetpts=N/SR/TB";

    /// <summary>
    /// Video filter that keeps only the given ranges and closes the holes:
    /// <c>select='...',setpts=N/FRAME_RATE/TB</c>. Times are input timestamps.
    /// </summary>
    public static string VideoSelectFilter(IReadOnlyList<TimeRange> ranges) =>
      $"select='{BetweenExpression(ranges)}',setpts=N/FRAME_RATE/TB";

    /// <summary>
    /// Contents of a list file for ffmpeg's concat demuxer
    /// (<c>-f concat -safe 0 -i list.txt</c>). Paths use forward slashes and
    /// single quotes, with embedded quotes escaped the way the demuxer expects.
    /// </summary>
    public static string ConcatListFile(IEnumerable<string> files)
    {
      var sb = new StringBuilder();
      foreach (string f in files)
      {
        string p = f.Replace('\\', '/').Replace("'", "'\\''");
        sb.Append("file '").Append(p).Append("'\n");
      }
      return sb.ToString();
    }

    /// <summary>Total length of the ranges (the duration of the media produced from them).</summary>
    public static TimeSpan TotalDuration(IReadOnlyList<TimeRange> ranges)
    {
      TimeSpan total = TimeSpan.Zero;
      foreach (TimeRange r in ranges) total += r.Duration;
      return total;
    }
  }
}
