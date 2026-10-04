//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  public enum MkvExtractStatus
  {
    /// <summary>mkvextract wrote the file.</summary>
    Extracted,
    /// <summary>The file was there already and was left as it was (season plan, design 7).</summary>
    Kept,
    /// <summary>Nothing extracted; <see cref="MkvExtractResult.Reason"/> says why, and no partial file is left.</summary>
    Failed,
  }

  /// <summary>The outcome of one <see cref="MkvExtract.ExtractAsync"/>.</summary>
  public sealed class MkvExtractResult
  {
    public MkvExtractStatus Status { get; init; }
    /// <summary>The output file, full path.</summary>
    public string OutputPath { get; init; } = "";
    /// <summary>Null unless <see cref="MkvExtractStatus.Failed"/>; otherwise one line for the season table.</summary>
    public string? Reason { get; init; }
  }

  /// <summary>
  /// Extracting a season's EN subtitle track with <c>mkvextract</c> (season plan B3): the output
  /// name (pure) and one track of one file to one path. No GTK here: the command line and the
  /// GUI share it. mkvextract 82 writes ASS, SSA and SRT tracks as UTF-8 with a BOM.
  /// </summary>
  public static class MkvExtract
  {
    /// <summary>The season folder's subfolder the EN extracts go to (also the retimed JP files).</summary>
    public const string SubsFolder = "s2s";

    /// <summary>The tag between the video's name and the extension of an EN extract.</summary>
    public const string EnTag = ".en";

    /// <summary>What to tell the user when mkvextract cannot be found.</summary>
    public const string NotFoundMessage =
      "mkvextract not found: install MKVToolNix, or put its folder on PATH or in Tools Directory (Preferences)";

    /// <summary>
    /// The file extension for a text subtitle codec, without the dot: <c>ass</c>, <c>ssa</c> or
    /// <c>srt</c>; null for any other codec (<see cref="MkvTracks.TextCodecIds"/>).
    /// </summary>
    public static string? Extension(string codecId) => codecId switch
    {
      "S_TEXT/ASS" => "ass",
      "S_TEXT/SSA" => "ssa",
      "S_TEXT/UTF8" => "srt",
      _ => null,
    };

    /// <summary>
    /// Where the EN track of <paramref name="videoPath"/> is extracted to:
    /// <c>&lt;season&gt;/s2s/&lt;video name&gt;.en.&lt;ext&gt;</c>, the name <c>go --season</c>
    /// looks for. Throws <see cref="ArgumentException"/> for a track that is not ASS, SSA or SRT.
    /// </summary>
    public static string OutputPath(string seasonDir, string videoPath, MkvTrackInfo track)
    {
      string ext = Extension(track.CodecId)
        ?? throw new ArgumentException("track " + track.Id.ToString(CultureInfo.InvariantCulture) + " ("
          + track.CodecId + ") is not an ASS, SSA or SRT subtitle track", nameof(track));
      return Path.Combine(seasonDir, SubsFolder, Path.GetFileNameWithoutExtension(videoPath) + EnTag + "." + ext);
    }

    /// <summary>
    /// Test hook: runs the process instead of starting it (the start info is the one
    /// <see cref="ExtractAsync"/> built). Null in normal runs; reset it in the test's Dispose.
    /// </summary>
    internal static Func<ProcessStartInfo, CancellationToken, Task<CliProcessResult>>? RunnerOverride { get; set; }

    /// <summary>
    /// <c>mkvextract &lt;mkv&gt; tracks &lt;id&gt;:&lt;output&gt;</c>, creating the output's folder.
    /// An output that exists and is not empty is <see cref="MkvExtractStatus.Kept"/>, not extracted
    /// again (an empty one, which no extraction leaves, is overwritten). Exit code 0 or 1 (warnings) with a non-empty output is
    /// <see cref="MkvExtractStatus.Extracted"/>; every failure (mkvextract not found or not
    /// starting, exit code 2 or more, no output) is <see cref="MkvExtractStatus.Failed"/> with
    /// mkvextract's message and deletes the partial file. Only a cancel throws, after deleting
    /// the partial file too.
    /// </summary>
    public static async Task<MkvExtractResult> ExtractAsync(string mkvFile, int trackId, string outputPath,
      CancellationToken ct = default)
    {
      ct.ThrowIfCancellationRequested();
      // Full paths, so a name starting with '@' or '-' is not read as an option.
      string output = Path.GetFullPath(outputPath);
      if (new FileInfo(output) is { Exists: true, Length: > 0 })
        return new MkvExtractResult { Status = MkvExtractStatus.Kept, OutputPath = output };

      MkvExtractResult Failed(string reason) =>
        new MkvExtractResult { Status = MkvExtractStatus.Failed, OutputPath = output, Reason = reason };

      string? exe = ConstantSettings.ResolveTool(ConstantSettings.ExeMkvExtract);
      if (exe == null)
        return Failed(NotFoundMessage);
      string mkv = Path.GetFullPath(mkvFile);
      if (!File.Exists(mkv))
        // mkvextract itself would say "Unknown mode '<path>'".
        return Failed("no such file: " + mkv);
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        return Failed("cannot create the folder for " + output + ": " + ex.Message);
      }

      CliProcessResult result;
      try
      {
        ProcessStartInfo psi = MkvTracks.StartInfo(exe, new[]
        {
          "--output-charset", "UTF-8", mkv, "tracks", trackId.ToString(CultureInfo.InvariantCulture) + ":" + output,
        });
        result = await (RunnerOverride ?? MkvTracks.RunAsync)(psi, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        TryDelete(output);
        throw;
      }
      catch (Exception ex)
      {
        return Failed(WithCleanup("could not start mkvextract: " + ex.Message, output));
      }

      if (result.ExitCode == null)
        return Failed(WithCleanup("mkvextract did not finish", output));
      int code = result.ExitCode.Value;
      string? message = Message(result);
      string exit = "mkvextract exited with code " + code.ToString(CultureInfo.InvariantCulture);
      if (code >= 2)
        return Failed(WithCleanup(exit + ": "
          + (message ?? LastLine(result.Stderr) ?? LastLine(result.Stdout) ?? "(no output)"), output));
      if (new FileInfo(output) is not { Exists: true, Length: > 0 })
        return Failed(WithCleanup(exit + " but wrote nothing" + (message != null ? ": " + message : ""), output));
      return new MkvExtractResult { Status = MkvExtractStatus.Extracted, OutputPath = output };
    }

    /// <summary>
    /// mkvextract's own message: its first <c>Error:</c> line, else its first <c>Warning:</c>
    /// line, without that word (both go to stdout, between progress lines ended by '\r').
    /// </summary>
    private static string? Message(CliProcessResult result)
    {
      string[] lines = (result.Stdout + "\n" + result.Stderr)
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim()).ToArray();
      foreach (string prefix in new[] { "Error:", "Warning:" })
      {
        string? line = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line != null && line.Length > prefix.Length)
          return line.Substring(prefix.Length).Trim();
      }
      return null;
    }

    private static string? LastLine(string text)
      => text.Split(new[] { '\r', '\n' }).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>The reason, plus a note when the partial file is there and cannot be deleted.</summary>
    private static string WithCleanup(string reason, string output)
    {
      string? error = TryDelete(output);
      return error == null ? reason : reason + " (and the partial file could not be deleted: " + error + ")";
    }

    /// <summary>Deletes the file if it is there; the error's message when that fails, else null.</summary>
    private static string? TryDelete(string path)
    {
      try
      {
        if (File.Exists(path)) File.Delete(path);
        return null;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        return ex.Message;
      }
    }
  }
}
