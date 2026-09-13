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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace subs2srs
{
  /// <summary>
  /// One episode's human-labelled grouping, written by "Save as validation" in
  /// the preview. It carries the kept lines exactly as the grouper sees them,
  /// so an evaluation needs neither subtitle files nor ffmpeg.
  /// </summary>
  public class GroupingValidationFile
  {
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Version { get; set; } = CurrentVersion;
    public SourceInfo Source { get; set; } = new();
    public LimitsInfo Limits { get; set; } = new();
    public List<LineInfo> Lines { get; set; } = new();

    /// <summary>Human truth: joins[i] = kept line i is attached to kept line i+1. Length = Lines.Count - 1.</summary>
    public bool[] Joins { get; set; } = Array.Empty<bool>();

    /// <summary>What the editor started from, if anything.</summary>
    public ProposalInfo? Proposal { get; set; }

    public class SourceInfo
    {
      public string Subs1 { get; set; } = "";
      public string? Subs2 { get; set; }
      public string? Subs1Sha256 { get; set; }
      public int Episode { get; set; }
      public OmissionInfo Omission { get; set; } = new();
    }

    /// <summary>The settings that decide which lines are kept, and therefore who is whose neighbour.</summary>
    public class OmissionInfo
    {
      public SubOmission Subs1 { get; set; } = new();
      public SubOmission Subs2 { get; set; } = new();
      public bool SpanEnabled { get; set; }
      public string? SpanStart { get; set; }
      public string? SpanEnd { get; set; }
      public List<string> ActorList { get; set; } = new();
      public bool KanjiLinesOnly { get; set; }
    }

    public class SubOmission
    {
      public string[] IncludedWords { get; set; } = Array.Empty<string>();
      public string[] ExcludedWords { get; set; } = Array.Empty<string>();
      public bool ExcludeDuplicateLines { get; set; }
      public int? ExcludeFewerChars { get; set; }
      public int? ExcludeShorterThanMs { get; set; }
      public int? ExcludeLongerThanMs { get; set; }
      public bool JoinSentences { get; set; }
      public string? JoinSentencesChars { get; set; }
      public bool RemoveNoCounterpart { get; set; }
    }

    public class LimitsInfo
    {
      public double MaxSnippetSeconds { get; set; }
      public int GapKeepMs { get; set; }
      public int PadMs { get; set; }
    }

    public class LineInfo
    {
      /// <summary>Index into the episode's full line list (before omitted lines were dropped).</summary>
      public int I { get; set; }
      public string S { get; set; } = "";
      public string E { get; set; } = "";
      public string? Actor { get; set; }
      public string T { get; set; } = "";
      public string? T2 { get; set; }
    }

    public class ProposalInfo
    {
      public string Producer { get; set; } = "rules";
      public string? Model { get; set; }
      public int? PromptVersion { get; set; }
      public bool[] Joins { get; set; } = Array.Empty<bool>();
    }

    // ── build / IO ──────────────────────────────────────────────────────

    /// <summary>
    /// Build the file for one episode from the flat lines and full-index join vectors.
    /// </summary>
    public static GroupingValidationFile Build(IReadOnlyList<InfoCombined> lines, bool[] joins,
      bool[]? proposalJoins, string proposalProducer, SnippetLimits limits, int episodeNumber,
      string subs1Path, string? subs2Path)
    {
      int[] kept = SnippetGrouping.KeptIndices(lines);
      var file = new GroupingValidationFile();

      file.Source.Subs1 = Path.GetFileName(subs1Path);
      file.Source.Subs2 = string.IsNullOrEmpty(subs2Path) ? null : Path.GetFileName(subs2Path);
      file.Source.Subs1Sha256 = TrySha256(subs1Path);
      file.Source.Episode = episodeNumber;
      file.Source.Omission = OmissionFromSettings();

      file.Limits.MaxSnippetSeconds = limits.MaxSnippetMs / 1000.0;
      file.Limits.GapKeepMs = limits.GapKeepMs;
      file.Limits.PadMs = limits.PadMs;

      bool hasSubs2 = !string.IsNullOrEmpty(subs2Path);
      foreach (int i in kept)
      {
        InfoCombined c = lines[i];
        file.Lines.Add(new LineInfo
        {
          I = i,
          S = FormatTime(c.Subs1.StartTime),
          E = FormatTime(c.Subs1.EndTime),
          Actor = string.IsNullOrEmpty(c.Subs1.Actor) ? null : c.Subs1.Actor,
          T = c.Subs1.Text ?? "",
          T2 = hasSubs2 ? c.Subs2.Text : null,
        });
      }

      file.Joins = ToKeptJoins(joins, kept);
      if (proposalJoins != null)
      {
        file.Proposal = new ProposalInfo
        {
          Producer = proposalProducer,
          Joins = ToKeptJoins(proposalJoins, kept),
        };
      }

      return file;
    }

    /// <summary>Kept-projection join vector with the conventional length (kept.Length - 1).</summary>
    private static bool[] ToKeptJoins(bool[] fullJoins, int[] kept)
    {
      bool[] projected = SnippetGrouping.ProjectToKept(fullJoins, kept);
      int n = Math.Max(0, kept.Length - 1);
      var result = new bool[n];
      Array.Copy(projected, result, n);
      return result;
    }

    public static OmissionInfo OmissionFromSettings()
    {
      var s = Settings.Instance;
      var o = new OmissionInfo
      {
        SpanEnabled = s.SpanEnabled,
        SpanStart = s.SpanEnabled ? FormatTime(s.SpanStart) : null,
        SpanEnd = s.SpanEnabled ? FormatTime(s.SpanEnd) : null,
        ActorList = new List<string>(s.ActorList ?? new List<string>()),
        KanjiLinesOnly = s.LanguageSpecific.KanjiLinesOnly,
      };
      o.Subs1 = FromSub(s.Subs[0]);
      o.Subs2 = FromSub(s.Subs[1]);
      return o;
    }

    private static SubOmission FromSub(SubSettings sub)
    {
      return new SubOmission
      {
        IncludedWords = sub.IncludedWords ?? Array.Empty<string>(),
        ExcludedWords = sub.ExcludedWords ?? Array.Empty<string>(),
        ExcludeDuplicateLines = sub.ExcludeDuplicateLinesEnabled,
        ExcludeFewerChars = sub.ExcludeFewerEnabled ? sub.ExcludeFewerCount : null,
        ExcludeShorterThanMs = sub.ExcludeShorterThanTimeEnabled ? sub.ExcludeShorterThanTime : null,
        ExcludeLongerThanMs = sub.ExcludeLongerThanTimeEnabled ? sub.ExcludeLongerThanTime : null,
        JoinSentences = sub.JoinSentencesEnabled,
        JoinSentencesChars = sub.JoinSentencesEnabled ? sub.JoinSentencesCharList : null,
        RemoveNoCounterpart = sub.RemoveNoCounterpart,
      };
    }

    public static string FormatTime(TimeSpan t) =>
      $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    public static TimeSpan ParseTime(string s)
    {
      return TimeSpan.ParseExact(s, @"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? TrySha256(string path)
    {
      try
      {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
      }
      catch
      {
        return null;
      }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static GroupingValidationFile FromJson(string json) =>
      JsonSerializer.Deserialize<GroupingValidationFile>(json, JsonOpts)
      ?? throw new InvalidDataException("Empty validation file.");

    public void Write(string path)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
      File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
    }

    public static GroupingValidationFile Read(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Default file name: &lt;deck&gt;_&lt;episode&gt;.grouping.json</summary>
    public static string FileName(string deckName, int episodeNumber) => $"{deckName}_{episodeNumber}.grouping.json";

    /// <summary>Rebuild plain InfoCombined lines from the file (all active) for scoring or replay.</summary>
    public List<InfoCombined> ToLines()
    {
      var lines = new List<InfoCombined>(Lines.Count);
      foreach (LineInfo l in Lines)
      {
        var s1 = new InfoLine(ParseTime(l.S), ParseTime(l.E), l.T, l.Actor ?? "");
        var s2 = new InfoLine(ParseTime(l.S), ParseTime(l.E), l.T2 ?? "", l.Actor ?? "");
        lines.Add(new InfoCombined(s1, s2, true));
      }
      return lines;
    }
  }
}
