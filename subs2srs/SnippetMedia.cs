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
  /// How a grouped (multi-line) card is cut out of the media.
  /// </summary>
  public class CardCutOptions
  {
    /// <summary>Shorten silences between kept parts longer than <see cref="GapKeepMs"/> to that length.</summary>
    public bool GapRemoval { get; set; } = true;

    public int GapKeepMs { get; set; } = 500;

    /// <summary>Padding before the first kept part, in milliseconds.</summary>
    public int PadStartMs { get; set; }

    /// <summary>Padding after the last kept part, in milliseconds.</summary>
    public int PadEndMs { get; set; }

    public CardCutOptions() { }

    public CardCutOptions(bool gapRemoval, int gapKeepMs, int padStartMs = 0, int padEndMs = 0)
    {
      GapRemoval = gapRemoval;
      GapKeepMs = gapKeepMs;
      PadStartMs = padStartMs;
      PadEndMs = padEndMs;
    }

    /// <summary>
    /// From the project settings: the Snippets gap-removal options and the
    /// audio-clip pad (the pad the duration limit is measured with).
    /// </summary>
    public static CardCutOptions FromSettings()
    {
      var (padStart, padEnd) = SnippetMedia.GroupedCardPadMs();
      return new CardCutOptions(Settings.Instance.Snippets.GapRemovalEnabled,
        Math.Max(0, Settings.Instance.Snippets.GapKeepMs), padStart, padEnd);
    }
  }

  /// <summary>
  /// The one list of media ranges a grouped card is cut from. Audio clips,
  /// video clips and animated snapshots of a multi-line card all use the same
  /// list, so they cover the same dialogue (the video only approximately, since
  /// it is stream-copied on keyframes).
  ///
  /// The list is the kept parts with the silences between them shortened to
  /// GapKeepMs (or kept whole when gap removal is off) and the outer edges
  /// padded, minus the dialogue of the omitted parts: an omitted line inside a
  /// snippet is never heard or seen on the card, whatever the gap setting.
  /// Where an omitted line overlaps a kept one, the kept dialogue wins.
  /// </summary>
  public static class SnippetMedia
  {
    /// <summary>
    /// The pad applied to grouped cards: the audio-clip pad when audio clips are
    /// enabled with padding, otherwise none. Single-line cards keep each
    /// worker's own pad.
    /// </summary>
    public static (int start, int end) GroupedCardPadMs()
    {
      var a = Settings.Instance.AudioClips;
      if (a.Enabled && a.PadEnabled) return (Math.Max(0, a.PadStart), Math.Max(0, a.PadEnd));
      return (0, 0);
    }

    /// <summary>
    /// The ranges to cut for a card made of <paramref name="keptSegments"/>
    /// while never including <paramref name="omittedSegments"/>. Sorted, non-empty,
    /// non-overlapping; empty only when there are no kept segments.
    /// </summary>
    public static List<TimeRange> Ranges(IReadOnlyList<TimeRange> keptSegments, IReadOnlyList<TimeRange> omittedSegments,
      CardCutOptions options)
    {
      int gapKeep = options.GapRemoval ? Math.Max(0, options.GapKeepMs) : int.MaxValue;
      List<TimeRange> ranges = SnippetGrouping.TrimmedRanges(keptSegments, gapKeep,
        Math.Max(0, options.PadStartMs), Math.Max(0, options.PadEndMs));

      if (omittedSegments == null || omittedSegments.Count == 0) return ranges;

      // Only the part of an omitted line that lies outside every kept line is cut away.
      List<TimeRange> holes = Subtract(omittedSegments, keptSegments);
      return Subtract(ranges, holes);
    }

    /// <summary>The range list of a grouped card, or null for a plain single line (which each worker cuts its own way).</summary>
    public static List<TimeRange>? RangesFor(InfoCombined card, CardCutOptions options)
    {
      if (card == null || !card.IsSnippet) return null;
      return Ranges(card.Segments(), card.OmittedSegments(), options);
    }

    /// <summary>
    /// What a media worker cuts for any card: the shared list for a grouped
    /// card; for a plain line with several recorded dialogue ranges (the
    /// sentence-join feature) and gap removal on, those ranges trimmed with the
    /// worker's own pad; otherwise null (the worker cuts the padded line itself).
    /// </summary>
    public static List<TimeRange>? RangesFor(InfoCombined card, CardCutOptions options, int plainPadStartMs, int plainPadEndMs)
    {
      if (card == null) return null;
      if (card.IsSnippet) return Ranges(card.Segments(), card.OmittedSegments(), options);
      if (!options.GapRemoval) return null;
      List<TimeRange> segments = card.Segments();
      if (segments.Count < 2) return null;
      return SnippetGrouping.TrimmedRanges(segments, Math.Max(0, options.GapKeepMs),
        Math.Max(0, plainPadStartMs), Math.Max(0, plainPadEndMs));
    }

    /// <summary>
    /// The range list of the card that lines[first..last] (full indices) would
    /// become; the same as <see cref="RangesFor(InfoCombined, CardCutOptions)"/>
    /// on the materialised snippet. Used by the preview, which keeps flat lines.
    /// </summary>
    public static List<TimeRange> RangesFor(IReadOnlyList<InfoCombined> lines, int first, int last, CardCutOptions options)
    {
      return Ranges(SnippetGrouping.SegmentsOf(lines, first, last),
        SnippetGrouping.OmittedSegmentsOf(lines, first, last), options);
    }

    /// <summary>
    /// <paramref name="ranges"/> with <paramref name="holes"/> removed. Both
    /// inputs are sorted and non-overlapping; empty pieces are dropped.
    /// </summary>
    public static List<TimeRange> Subtract(IReadOnlyList<TimeRange> ranges, IReadOnlyList<TimeRange> holes)
    {
      var result = new List<TimeRange>();
      foreach (TimeRange r in ranges)
      {
        TimeSpan cursor = r.Start;
        foreach (TimeRange h in holes)
        {
          if (h.End <= cursor) continue;
          if (h.Start >= r.End) break;
          if (h.Start > cursor) result.Add(new TimeRange(cursor, h.Start));
          if (h.End > cursor) cursor = h.End;
        }
        if (cursor < r.End) result.Add(new TimeRange(cursor, r.End));
      }
      return result;
    }
  }
}
