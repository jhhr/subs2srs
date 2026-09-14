using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace subs2srs
{
  /// <summary>What the AI pass produced for one episode; also the cache file's content.</summary>
  public sealed class AiGroupingResult
  {
    public int Version { get; set; } = 1;
    public string Model { get; set; } = "";
    public int PromptVersion { get; set; } = AiGroupingPrompt.PromptVersion;
    /// <summary>Kept-projection join vector (length = kept lines, last entry unused).</summary>
    public bool[] KeptJoins { get; set; } = Array.Empty<bool>();
    /// <summary>Model notes keyed by the kept position of a snippet's first line.</summary>
    public Dictionary<int, string> Notes { get; set; } = new();
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int Chunks { get; set; }
    /// <summary>Chunks whose request failed and were grouped by the rules instead.</summary>
    public int FailedChunks { get; set; }
    /// <summary>Repairs made to the model's answers (malformed ranges), for the log.</summary>
    public List<string> Repairs { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public bool FromCache { get; set; }
  }


  /// <summary>
  /// Cache of AI grouping results (plan §A.5), one JSON file per key under the app cache dir
  /// (or the <c>AiCacheDir</c> preference). The key is a SHA-256 over everything the answer
  /// depends on: the exact lines the model sees (which already reflect the omission settings),
  /// the limits, the model, chunking and prompt version, and the extra instructions.
  /// </summary>
  public sealed class AiGroupingCache
  {
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Dir { get; }

    public AiGroupingCache(string? dir = null)
    {
      Dir = string.IsNullOrWhiteSpace(dir) ? ConstantSettings.AiCacheDirFull : dir;
    }

    public static string KeyFor(IReadOnlyList<InfoCombined> lines, int[] kept, string model, SnippetLimits limits,
      int chunkTargetLines, string? extraInstructions)
    {
      var whole = new AiChunk { KeptStart = 0, KeptEnd = kept.Length - 1 };
      string content = kept.Length == 0 ? "[]" : AiGroupingPrompt.BuildUser(lines, kept, whole);
      var sb = new StringBuilder();
      sb.Append("v=").Append(AiGroupingPrompt.PromptVersion).Append('\n');
      sb.Append("model=").Append((model ?? "").Trim()).Append('\n');
      sb.Append("max=").Append(limits.MaxSnippetMs).Append('\n');
      sb.Append("gap=").Append(limits.GapKeepMs).Append('\n');
      sb.Append("chunk=").Append(chunkTargetLines).Append('\n');
      sb.Append("extra=").Append((extraInstructions ?? "").Trim()).Append('\n');
      sb.Append(content);
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
      return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string PathFor(string key) => Path.Combine(Dir, key + ".json");

    public AiGroupingResult? TryGet(string key)
    {
      string path = PathFor(key);
      try
      {
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<AiGroupingResult>(File.ReadAllText(path, Encoding.UTF8), Json);
        if (result == null || result.KeptJoins == null) return null;
        result.FromCache = true;
        return result;
      }
      catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
      {
        Logger.Instance.info("AI grouping cache: could not read " + path + ": " + ex.Message);
        return null;
      }
    }

    public void Put(string key, AiGroupingResult result)
    {
      string path = PathFor(key);
      try
      {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(path, JsonSerializer.Serialize(result, Json), new UTF8Encoding(false));
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
      {
        Logger.Instance.info("AI grouping cache: could not write " + path + ": " + ex.Message);
      }
    }
  }
}
