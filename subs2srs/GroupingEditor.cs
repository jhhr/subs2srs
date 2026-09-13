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
  public enum JoinAction
  {
    AttachAbove,
    AttachBelow,
    Detach
  }

  public readonly struct JoinResult
  {
    public bool Ok { get; }
    public bool Changed { get; }
    public string Message { get; }
    public int OvershootMs { get; }

    public JoinResult(bool ok, bool changed, string message, int overshootMs = 0)
    {
      Ok = ok;
      Changed = changed;
      Message = message;
      OvershootMs = overshootMs;
    }

    public static JoinResult Applied(string message = "") => new(true, true, message);
    public static JoinResult NoOp(string message) => new(true, false, message);
    public static JoinResult Rejected(string message, int overshootMs = 0) => new(false, false, message, overshootMs);
  }

  /// <summary>
  /// GTK-free editing model for one episode's grouping: a full-index join
  /// vector over the lines, the three per-line actions (attach above, attach
  /// below, detach), limit validation, and undo/redo. The preview dialog's
  /// drag, keyboard and button handlers all end up in <see cref="Apply"/>.
  /// </summary>
  public class GroupingEditor
  {
    private readonly IReadOnlyList<InfoCombined> _lines;
    private bool[] _joins;
    private int[] _kept;
    private readonly Stack<bool[]> _undo = new();
    private readonly Stack<bool[]> _redo = new();

    public SnippetLimits Limits { get; set; }

    /// <summary>Raised after any change to the join vector (including undo/redo).</summary>
    public event Action? Changed;

    public GroupingEditor(IReadOnlyList<InfoCombined> lines, bool[]? joins, SnippetLimits limits)
    {
      _lines = lines;
      Limits = limits;
      _joins = joins != null && joins.Length == lines.Count
        ? (bool[])joins.Clone()
        : SnippetGrouping.NoJoins(lines.Count);
      _kept = SnippetGrouping.KeptIndices(lines);
    }

    public IReadOnlyList<InfoCombined> Lines => _lines;

    /// <summary>The current full-index join vector (a copy).</summary>
    public bool[] Joins => (bool[])_joins.Clone();

    /// <summary>Kept (active) line indices; call <see cref="RefreshKept"/> after changing Active flags.</summary>
    public int[] Kept => _kept;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Recompute the kept set after Active flags changed. Joins are kept as they are.</summary>
    public void RefreshKept()
    {
      _kept = SnippetGrouping.KeptIndices(_lines);
      Changed?.Invoke();
    }

    // ── queries ─────────────────────────────────────────────────────────

    /// <summary>Position of a full line index in the kept set, or -1.</summary>
    public int KeptPosition(int lineIdx) => Array.IndexOf(_kept, lineIdx);

    /// <summary>Full-index range (first, last) of the snippet containing the line, or null if the line is not kept.</summary>
    public (int first, int last)? SnippetRangeOf(int lineIdx)
    {
      int k = KeptPosition(lineIdx);
      if (k < 0) return null;
      var (kf, kl) = keptRangeOf(k, _joins);
      return (_kept[kf], _kept[kl]);
    }

    /// <summary>Ordinal of the snippet the line belongs to (0-based over the episode), or -1.</summary>
    public int SnippetOrdinalOf(int lineIdx)
    {
      int k = KeptPosition(lineIdx);
      if (k < 0) return -1;
      bool[] keptJoins = SnippetGrouping.ProjectToKept(_joins, _kept);
      int ordinal = 0;
      for (int i = 0; i < k; i++)
        if (!keptJoins[i]) ordinal++;
      return ordinal;
    }

    /// <summary>True when the line is joined to the next kept line.</summary>
    public bool IsJoinedBelow(int lineIdx)
    {
      int k = KeptPosition(lineIdx);
      return k >= 0 && k < _kept.Length - 1 && _joins[lineIdx];
    }

    /// <summary>True when the previous kept line is joined to this one.</summary>
    public bool IsJoinedAbove(int lineIdx)
    {
      int k = KeptPosition(lineIdx);
      return k > 0 && _joins[_kept[k - 1]];
    }

    /// <summary>Trimmed duration (ms) of the snippet containing the line, or of the line itself when not kept.</summary>
    public int TrimmedDurationMsOf(int lineIdx)
    {
      var range = SnippetRangeOf(lineIdx);
      if (range == null) return SnippetGrouping.TrimmedDurationMs(_lines, lineIdx, lineIdx, Limits);
      return SnippetGrouping.TrimmedDurationMs(_lines, range.Value.first, range.Value.last, Limits);
    }

    /// <summary>Gap (ms) from the previous kept line to this one, or -1 when there is none.</summary>
    public int GapToPreviousKeptMs(int lineIdx)
    {
      int k = KeptPosition(lineIdx);
      if (k <= 0) return -1;
      return SnippetGrouping.GapMs(_lines[_kept[k - 1]], _lines[lineIdx]);
    }

    // ── actions ─────────────────────────────────────────────────────────

    public JoinResult Apply(int lineIdx, JoinAction action)
    {
      int k = KeptPosition(lineIdx);
      if (k < 0) return JoinResult.Rejected("Inactive lines cannot be grouped.");

      switch (action)
      {
        case JoinAction.AttachAbove:
          if (k == 0) return JoinResult.NoOp("No active line above.");
          return tryJoin(k - 1, "above");

        case JoinAction.AttachBelow:
          if (k >= _kept.Length - 1) return JoinResult.NoOp("No active line below.");
          return tryJoin(k, "below");

        case JoinAction.Detach:
        {
          bool above = k > 0 && _joins[_kept[k - 1]];
          bool below = k < _kept.Length - 1 && _joins[_kept[k]];
          if (!above && !below) return JoinResult.NoOp("Line is already on its own.");
          pushUndo();
          if (above) _joins[_kept[k - 1]] = false;
          if (below) _joins[_kept[k]] = false;
          Changed?.Invoke();
          return JoinResult.Applied("Detached.");
        }
      }

      return JoinResult.Rejected("Unknown action.");
    }

    /// <summary>Replace the whole join vector (e.g. after "Regroup"). Recorded for undo.</summary>
    public void SetJoins(bool[] joins)
    {
      pushUndo();
      _joins = joins.Length == _lines.Count ? (bool[])joins.Clone() : SnippetGrouping.NoJoins(_lines.Count);
      Changed?.Invoke();
    }

    public void UngroupAll() => SetJoins(SnippetGrouping.NoJoins(_lines.Count));

    public bool Undo()
    {
      if (_undo.Count == 0) return false;
      _redo.Push(_joins);
      _joins = _undo.Pop();
      Changed?.Invoke();
      return true;
    }

    public bool Redo()
    {
      if (_redo.Count == 0) return false;
      _undo.Push(_joins);
      _joins = _redo.Pop();
      Changed?.Invoke();
      return true;
    }

    /// <summary>Index of the next kept line (after <paramref name="fromLineIdx"/>) whose join differs from <paramref name="other"/>, or -1.</summary>
    public int NextDisagreement(bool[] other, int fromLineIdx)
    {
      for (int k = 0; k < _kept.Length - 1; k++)
      {
        int i = _kept[k];
        if (i <= fromLineIdx) continue;
        bool mine = _joins[i];
        bool theirs = i < other.Length && other[i];
        if (mine != theirs) return i;
      }
      return -1;
    }

    // ── internals ───────────────────────────────────────────────────────

    /// <summary>Set keptJoins[k] = true if the resulting snippet fits the limit.</summary>
    private JoinResult tryJoin(int k, string direction)
    {
      int slot = _kept[k];
      if (_joins[slot]) return JoinResult.NoOp($"Already attached {direction}.");

      var candidate = (bool[])_joins.Clone();
      candidate[slot] = true;
      var (kf, kl) = keptRangeOf(k, candidate);
      int dur = SnippetGrouping.TrimmedDurationMs(_lines, _kept[kf], _kept[kl], Limits);
      if (dur > Limits.MaxSnippetMs)
      {
        int over = dur - Limits.MaxSnippetMs;
        return JoinResult.Rejected(FormattableString.Invariant(
          $"Would be {dur / 1000.0:0.0} s, {over / 1000.0:0.0} s over the {Limits.MaxSnippetMs / 1000.0:0} s limit."), over);
      }

      pushUndo();
      _joins = candidate;
      Changed?.Invoke();
      return JoinResult.Applied(FormattableString.Invariant($"Attached {direction} ({dur / 1000.0:0.0} s)."));
    }

    /// <summary>Kept-index range of the run containing kept position k under the given full join vector.</summary>
    private (int first, int last) keptRangeOf(int k, bool[] joins)
    {
      int first = k;
      while (first > 0 && joins[_kept[first - 1]]) first--;
      int last = k;
      while (last < _kept.Length - 1 && joins[_kept[last]]) last++;
      return (first, last);
    }

    private void pushUndo()
    {
      _undo.Push((bool[])_joins.Clone());
      _redo.Clear();
    }
  }
}
