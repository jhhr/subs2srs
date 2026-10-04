//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace subs2srs
{
  /// <summary>
  /// What the season table needs from subsretimer's <c>--report</c> file (version 1, camelCase
  /// JSON in UTF-8), which an <c>--auto</c> run writes when it exits 0 or 2. Only the fields
  /// read here; the file has more (each segment's lines and offset, the encodings, the
  /// mismatch averages).
  /// </summary>
  /// <param name="ExitCode">0 saved, 2 nothing saved (see <see cref="Reason"/>).</param>
  /// <param name="Saved">The full path of the saved file, or null.</param>
  /// <param name="Segments">The number of runs of target lines moved together, each by its own offset.</param>
  /// <param name="ReferenceCoverage">The share (0 to 1) of the reference's lines the retimed target covers, the number <c>--min-match</c> compares; null when nothing was aligned.</param>
  /// <param name="Reason">Why nothing was saved: <see cref="BelowMinMatch"/>, <see cref="NoTimedLines"/>, or null.</param>
  public sealed record RetimeReport(int ExitCode, string? Saved, int Segments, double? ReferenceCoverage, string? Reason)
  {
    /// <summary>The report version this reads; the tool raises it when a field changes meaning.</summary>
    public const int SupportedVersion = 1;

    /// <summary><see cref="Reason"/> when the coverage was below <c>--min-match</c>.</summary>
    public const string BelowMinMatch = "below min-match";

    /// <summary><see cref="Reason"/> when either file had no timed lines, so nothing was aligned.</summary>
    public const string NoTimedLines = "no timed lines";

    /// <summary>
    /// The report at <paramref name="path"/>, or null when it is missing, unreadable, not the
    /// tool's JSON, or of another version. Never throws.
    /// </summary>
    public static RetimeReport? Read(string path)
    {
      try
      {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out JsonElement version)
            || version.GetInt32() != SupportedVersion
            || !root.TryGetProperty("exitCode", out JsonElement exitCode))
          return null;

        int segments = root.TryGetProperty("segments", out JsonElement list) && list.ValueKind == JsonValueKind.Array
          ? list.GetArrayLength() : 0;
        double? coverage = root.TryGetProperty("referenceCoverage", out JsonElement cov) && cov.ValueKind == JsonValueKind.Object
          ? cov.GetProperty("share").GetDouble() : null;
        return new RetimeReport(exitCode.GetInt32(), StringOrNull(root, "saved"), segments, coverage, StringOrNull(root, "reason"));
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or FormatException or KeyNotFoundException
        or ArgumentException or NotSupportedException)
      {
        return null;
      }
    }

    private static string? StringOrNull(JsonElement root, string name) =>
      root.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
  }
}
