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

namespace subs2srs
{
  /// <summary>
  /// Represents a single subtitle line.
  /// </summary>
  public class InfoLine : IComparable<InfoLine>
  {
    /// <summary>
    /// The start time of the line (offset from beginning of media).
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// The end time of the line (offset from beginning of media).
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// The actual subtitle text. For Vobsubs, it's the file name of the extracted image file for this line.
    /// </summary>
    public string Text { get; set; }

    /// <summary>
    /// Actor is a field unique to .ass subtitles.
    /// </summary>
    public string Actor { get; set; }

    /// <summary>
    /// When the sentence-join feature combined several subtitle lines into this
    /// one, the time ranges of the original lines (so the gaps between them can
    /// be removed from the media). Null for a line that was not joined.
    /// </summary>
    public List<TimeRange>? Segments { get; set; }

    public InfoLine()
    {
      StartTime = TimeSpan.Zero;
      EndTime = TimeSpan.Zero;
      Text = "";
      Actor = "";
    }

    public InfoLine(TimeSpan startTime, TimeSpan endTime, string text)
    {
      StartTime = startTime;
      EndTime = endTime;
      Text = text;
      Actor = "";
    }

    public InfoLine(TimeSpan startTime, TimeSpan endTime, string text, string actor)
    {
      StartTime = startTime;
      EndTime = endTime;
      Text = text;
      Actor = actor;
    }

    /// <summary>
    /// Shift StartTime, EndTime and any recorded segments by the given number of milliseconds.
    /// </summary>
    public void Shift(int shiftMs)
    {
      StartTime = UtilsSubs.shiftTiming(StartTime, shiftMs);
      EndTime = UtilsSubs.shiftTiming(EndTime, shiftMs);
      if (Segments != null)
      {
        foreach (TimeRange r in Segments)
        {
          r.Start = UtilsSubs.shiftTiming(r.Start, shiftMs);
          r.End = UtilsSubs.shiftTiming(r.End, shiftMs);
        }
      }
    }

    /// <summary>
    /// Compare lines based on their Start Times.
    /// </summary>
    public int CompareTo(InfoLine other)
    {
      return StartTime.CompareTo(other.StartTime);
    }

    public override string ToString()
    {
      return $"{Text} {UtilsSubs.timeToString(StartTime)}, {UtilsSubs.timeToString(EndTime)}";
    }
  }
}
