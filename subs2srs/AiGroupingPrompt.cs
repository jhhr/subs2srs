using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace subs2srs
{
  /// <summary>A contiguous run of kept lines sent to the model as one request (plan §A.1).</summary>
  public sealed class AiChunk
  {
    /// <summary>Position in the kept list of the first line (inclusive).</summary>
    public int KeptStart { get; init; }
    /// <summary>Position in the kept list of the last line (inclusive).</summary>
    public int KeptEnd { get; init; }
    public int Count => KeptEnd - KeptStart + 1;
  }


  /// <summary>
  /// Gap-split chunker: a gap of at least <c>MaxSnippetMs</c> between two kept lines can never be
  /// inside a snippet, so cutting there is exact and needs no overlap. Among those gaps the one
  /// that brings the chunk closest to the target size is chosen; dense stretches with no such gap
  /// produce a bigger chunk. Target 0 = whole episode in one chunk.
  /// </summary>
  public static class AiChunker
  {
    public const int DefaultTargetLines = 200;

    public static List<AiChunk> Split(IReadOnlyList<InfoCombined> lines, int[] kept, int maxSnippetMs, int targetLines)
    {
      var chunks = new List<AiChunk>();
      if (kept.Length == 0) return chunks;
      if (targetLines <= 0)
      {
        chunks.Add(new AiChunk { KeptStart = 0, KeptEnd = kept.Length - 1 });
        return chunks;
      }

      // Candidate boundaries: k such that a chunk may end at kept position k-1
      var boundaries = new List<int>();
      for (int k = 1; k < kept.Length; k++)
        if (SnippetGrouping.GapMs(lines[kept[k - 1]], lines[kept[k]]) >= maxSnippetMs)
          boundaries.Add(k);

      int start = 0;
      int b = 0;
      while (start < kept.Length)
      {
        int remaining = kept.Length - start;
        if (remaining <= targetLines) break;

        // last boundary with size <= target, and first boundary with size > target
        int? below = null, above = null;
        for (int j = b; j < boundaries.Count; j++)
        {
          int size = boundaries[j] - start;
          if (size <= 0) continue;
          if (size <= targetLines) below = j;
          else { above = j; break; }
        }
        int? pick = null;
        if (below.HasValue && above.HasValue)
          pick = (targetLines - (boundaries[below.Value] - start)) <= ((boundaries[above.Value] - start) - targetLines) ? below : above;
        else pick = below ?? above;
        if (!pick.HasValue) break; // no boundary left: the rest is one chunk

        int end = boundaries[pick.Value];
        chunks.Add(new AiChunk { KeptStart = start, KeptEnd = end - 1 });
        start = end;
        b = pick.Value + 1;
      }
      chunks.Add(new AiChunk { KeptStart = start, KeptEnd = kept.Length - 1 });
      return chunks;
    }
  }


  /// <summary>
  /// The prompt (plan §A.2): a stable system prompt, one JSON array of lines per chunk, and the
  /// output schema shared by all providers. <see cref="PromptVersion"/> is part of the cache key
  /// and of the validation file's proposal block; bump it whenever the wording or format changes.
  /// </summary>
  public static class AiGroupingPrompt
  {
    public const int PromptVersion = 2; // v2: subs1 only, the translation track is not sent

    /// <summary>JSON schema of the answer: {"snippets":[{"first":int,"last":int,"note":string}]}.</summary>
    public static JsonElement Schema { get; } = JsonDocument.Parse(
      "{\"type\":\"object\",\"properties\":{\"snippets\":{\"type\":\"array\",\"items\":{\"type\":\"object\","
      + "\"properties\":{\"first\":{\"type\":\"integer\"},\"last\":{\"type\":\"integer\"},\"note\":{\"type\":\"string\"}},"
      + "\"required\":[\"first\",\"last\",\"note\"],\"additionalProperties\":false}}},"
      + "\"required\":[\"snippets\"],\"additionalProperties\":false}").RootElement.Clone();

    private static readonly JsonSerializerOptions LineJson = new JsonSerializerOptions
    {
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The system prompt. Only the first subtitle track is described: the grouping is decided on subs1,
    /// and the second track (already synced to subs1 by subs2srs) follows it when the snippet is built.
    /// </summary>
    public static string BuildSystem(int maxSnippetSeconds, string? extraInstructions)
    {
      var sb = new StringBuilder();
      sb.Append("You group consecutive subtitle lines of a TV episode into short dialogue snippets for language-learning flashcards. ");
      sb.Append("Each snippet becomes one card with its audio, so a snippet must be understandable on its own.\n\n");
      sb.Append("Input: a JSON array of lines in order. Each line has i (index), s and e (start and end time), actor (may be empty) and t (the dialogue text).\n\n");
      sb.Append("Rules:\n");
      sb.Append("- A snippet is a range of consecutive lines, first..last inclusive. Ranges must not overlap. Lines you leave out become single-line cards.\n");
      sb.Append("- Group lines only when a line is not self-contained: an answer without its question, a reply that depends on the previous line, deixis (this, that, there) that the previous line resolves, a sentence split across lines, a joke or callback that needs its setup.\n");
      sb.Append("- Prefer the smallest group that is self-contained. Most lines stay single. Do not group across scene changes or long pauses.\n");
      sb.Append(FormattableString.Invariant($"- Never let a snippet span more than {maxSnippetSeconds} seconds from the start of its first line to the end of its last line.\n"));
      sb.Append("- note: at most a few words saying why the lines belong together, or an empty string.\n\n");
      sb.Append("Answer with JSON only: {\"snippets\":[{\"first\":12,\"last\":14,\"note\":\"answer to 12\"}]}. Only list multi-line snippets.");
      if (!string.IsNullOrWhiteSpace(extraInstructions))
      {
        sb.Append("\n\nAdditional instructions from the user:\n");
        sb.Append(extraInstructions.Trim());
      }
      return sb.ToString();
    }

    /// <summary>
    /// The user content for one chunk: a JSON array of the chunk's kept lines with i = kept position.
    /// Subs1 only; <see cref="InfoCombined.Subs2"/> is never sent.
    /// </summary>
    public static string BuildUser(IReadOnlyList<InfoCombined> lines, int[] kept, AiChunk chunk)
    {
      var items = new List<Dictionary<string, object?>>(chunk.Count);
      for (int k = chunk.KeptStart; k <= chunk.KeptEnd; k++)
      {
        InfoCombined line = lines[kept[k]];
        items.Add(new Dictionary<string, object?>
        {
          ["i"] = k,
          ["s"] = GroupingValidationFile.FormatTime(line.Subs1.StartTime),
          ["e"] = GroupingValidationFile.FormatTime(line.Subs1.EndTime),
          ["actor"] = line.Subs1.Actor ?? "",
          ["t"] = line.Subs1.Text ?? "",
        });
      }
      return JsonSerializer.Serialize(items, LineJson);
    }

    /// <summary>
    /// Rough token estimate for a prompt string: about one token per 2.5 characters (CJK text is
    /// denser than English, so this overestimates English a little).
    /// </summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling((text?.Length ?? 0) / 2.5);

    /// <summary>Rough output tokens for a chunk: the answer lists only multi-line snippets, ~12 tokens each, a third of the lines at most.</summary>
    public static int EstimateOutputTokens(int chunkLines) => 20 + chunkLines * 4;
  }


  /// <summary>Snippets the model proposed for one chunk, already clipped to the chunk and made consistent.</summary>
  public sealed class AiChunkAnswer
  {
    public List<(int first, int last, string note)> Snippets { get; } = new();
    public List<string> Repairs { get; } = new();
  }


  /// <summary>
  /// Turns the model's <c>{"snippets":[{first,last,note}]}</c> into ranges over kept positions.
  /// Anything malformed is repaired and described, never thrown: ranges outside the chunk are
  /// clipped or dropped, overlaps are cut at the earlier snippet's end, single-line ranges are
  /// ignored. Duration limits are enforced later by <see cref="SnippetGrouping.Repair"/>.
  /// </summary>
  public static class AiAnswerParser
  {
    public static AiChunkAnswer Parse(string json, AiChunk chunk)
    {
      var answer = new AiChunkAnswer();
      JsonElement snippets;
      try
      {
        using JsonDocument doc = JsonDocument.Parse(ExtractJsonObject(json));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("snippets", out snippets) || snippets.ValueKind != JsonValueKind.Array)
        {
          answer.Repairs.Add("answer has no \"snippets\" array; treated as no groups");
          return answer;
        }
        snippets = snippets.Clone();
      }
      catch (JsonException ex)
      {
        answer.Repairs.Add("answer is not valid JSON (" + ex.Message + "); treated as no groups");
        return answer;
      }

      var raw = new List<(int first, int last, string note)>();
      foreach (JsonElement s in snippets.EnumerateArray())
      {
        if (s.ValueKind != JsonValueKind.Object) { answer.Repairs.Add("ignored a non-object snippet entry"); continue; }
        if (!TryInt(s, "first", out int first) || !TryInt(s, "last", out int last))
        {
          answer.Repairs.Add("ignored a snippet without integer first/last");
          continue;
        }
        string note = s.TryGetProperty("note", out JsonElement n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "").Trim() : "";
        if (first > last) { (first, last) = (last, first); answer.Repairs.Add(FormattableString.Invariant($"swapped first/last of {last}..{first}")); }
        if (last < chunk.KeptStart || first > chunk.KeptEnd)
        {
          answer.Repairs.Add(FormattableString.Invariant($"dropped {first}..{last}: outside lines {chunk.KeptStart}..{chunk.KeptEnd}"));
          continue;
        }
        if (first < chunk.KeptStart || last > chunk.KeptEnd)
        {
          int cf = Math.Max(first, chunk.KeptStart), cl = Math.Min(last, chunk.KeptEnd);
          answer.Repairs.Add(FormattableString.Invariant($"clipped {first}..{last} to {cf}..{cl}"));
          first = cf; last = cl;
        }
        if (first == last) continue; // a single line is the default anyway
        raw.Add((first, last, note));
      }

      raw.Sort((a, b) => a.first != b.first ? a.first.CompareTo(b.first) : b.last.CompareTo(a.last));
      int lastEnd = -1;
      foreach ((int first, int last, string note) r in raw)
      {
        int first = r.first;
        if (first <= lastEnd)
        {
          first = lastEnd + 1;
          if (first >= r.last)
          {
            answer.Repairs.Add(FormattableString.Invariant($"dropped {r.first}..{r.last}: overlaps an earlier snippet"));
            continue;
          }
          answer.Repairs.Add(FormattableString.Invariant($"cut {r.first}..{r.last} to {first}..{r.last}: overlaps an earlier snippet"));
        }
        answer.Snippets.Add((first, r.last, r.note));
        lastEnd = r.last;
      }
      return answer;
    }

    /// <summary>Apply a chunk answer to a kept-projection join vector.</summary>
    public static void ApplyToKeptJoins(AiChunkAnswer answer, bool[] keptJoins, Dictionary<int, string> notesByKeptFirst)
    {
      foreach ((int first, int last, string note) s in answer.Snippets)
      {
        for (int k = s.first; k < s.last && k < keptJoins.Length; k++) keptJoins[k] = true;
        if (!string.IsNullOrEmpty(s.note)) notesByKeptFirst[s.first] = s.note;
      }
    }

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
      value = 0;
      if (!obj.TryGetProperty(name, out JsonElement v)) return false;
      if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt32(out value);
      if (v.ValueKind == JsonValueKind.String) return int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
      return false;
    }

    /// <summary>Tolerate prose or code fences around the object, like <c>extract_json_string</c> in the Python reference.</summary>
    internal static string ExtractJsonObject(string text)
    {
      if (text == null) return "";
      int start = text.IndexOf('{');
      int end = text.LastIndexOf('}');
      return start >= 0 && end > start ? text.Substring(start, end - start + 1) : text;
    }
  }
}
