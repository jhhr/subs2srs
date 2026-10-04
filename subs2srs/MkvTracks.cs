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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>One track as <c>mkvmerge -J</c> describes it.</summary>
  public sealed class MkvTrackInfo
  {
    /// <summary>The track id mkvmerge and mkvextract take (0-based, file order).</summary>
    public int Id { get; init; }
    /// <summary><c>video</c>, <c>audio</c> or <c>subtitles</c>.</summary>
    public string Type { get; init; } = "";
    /// <summary>mkvmerge's name for the codec, e.g. <c>SubRip/SRT</c>.</summary>
    public string Codec { get; init; } = "";
    /// <summary>The Matroska codec id, e.g. <c>S_TEXT/UTF8</c>.</summary>
    public string CodecId { get; init; } = "";
    /// <summary>ISO 639-2 code, e.g. <c>eng</c>; <c>und</c> when the file does not say.</summary>
    public string Language { get; init; } = "";
    /// <summary>IETF BCP 47 tag, e.g. <c>en-US</c>.</summary>
    public string LanguageIetf { get; init; } = "";
    /// <summary>The track name, "" when it has none.</summary>
    public string Name { get; init; } = "";
    public bool Forced { get; init; }
    public bool Default { get; init; }
    /// <summary>
    /// <c>num_index_entries</c>: the subtitle events of a subtitle track, as mkvmerge counts
    /// them from the index. Null when not reported; 0 in a file written without an index.
    /// </summary>
    public int? Events { get; init; }

    public bool IsSubtitles => Type == "subtitles";

    /// <summary>A subtitle track in a codec subs2srs and subsretimer read: ASS, SSA or SRT.</summary>
    public bool IsText => IsSubtitles && MkvTracks.TextCodecIds.Contains(CodecId);

    /// <summary>A bitmap subtitle track (PGS, VobSub, DVB): nothing to read without OCR.</summary>
    public bool IsImage => IsSubtitles
      && (MkvTracks.ImageCodecIds.Contains(CodecId) || CodecId.StartsWith("S_IMAGE/", StringComparison.Ordinal));

    /// <summary>
    /// English by either tag: <c>eng</c>, or an IETF tag that is <c>en</c> or starts with
    /// <c>en-</c> (<c>en-US</c>, <c>en-GB</c>). Not <c>enm</c> (Middle English).
    /// </summary>
    public bool IsEnglish =>
      string.Equals(Language, "eng", StringComparison.OrdinalIgnoreCase)
      || string.Equals(LanguageIetf, "en", StringComparison.OrdinalIgnoreCase)
      || LanguageIetf.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    /// <summary>For a table: <c>3 "English" 312 ev</c> (no name, no count: left out).</summary>
    public string Label
    {
      get
      {
        string label = Id.ToString(CultureInfo.InvariantCulture);
        if (Name.Length > 0) label += " \"" + Name + "\"";
        if (Events != null) label += " " + Events.Value.ToString(CultureInfo.InvariantCulture) + " ev";
        return label;
      }
    }
  }

  /// <summary>The tracks of one file, or why there are none (<see cref="Error"/> set).</summary>
  public sealed class MkvTrackList
  {
    public IReadOnlyList<MkvTrackInfo> Tracks { get; init; } = Array.Empty<MkvTrackInfo>();
    /// <summary>Null when mkvmerge listed the file; otherwise one line for the user.</summary>
    public string? Error { get; init; }

    public static MkvTrackList Failed(string error) => new MkvTrackList { Error = error };
  }

  /// <summary>The EN track to extract, or why there is none (<see cref="Reason"/> set).</summary>
  public sealed class MkvTrackPick
  {
    public MkvTrackInfo? Track { get; init; }
    /// <summary>Null when a track was picked; otherwise one line for the season table.</summary>
    public string? Reason { get; init; }

    public static MkvTrackPick None(string reason) => new MkvTrackPick { Reason = reason };
  }

  /// <summary>
  /// The subtitle tracks of an mkv from <c>mkvmerge -J</c>, and the choice of the English track
  /// a season's retime uses as its reference (season plan B1, B2). Parsing and the pick are pure;
  /// <see cref="ListAsync"/> runs mkvmerge. No GTK here: the command line and the GUI share it.
  /// </summary>
  public static class MkvTracks
  {
    /// <summary>
    /// The text codecs a track can be picked in. WebVTT (<c>S_TEXT/WEBVTT</c>) is text too, but
    /// neither subs2srs nor subsretimer reads it, so such a track is passed over.
    /// </summary>
    public static readonly IReadOnlySet<string> TextCodecIds =
      new HashSet<string>(StringComparer.Ordinal) { "S_TEXT/ASS", "S_TEXT/SSA", "S_TEXT/UTF8" };

    /// <summary>Bitmap codecs (plus every <c>S_IMAGE/*</c>).</summary>
    public static readonly IReadOnlySet<string> ImageCodecIds =
      new HashSet<string>(StringComparer.Ordinal) { "S_HDMV/PGS", "S_VOBSUB", "S_DVBSUB" };

    public const string NoSubtitleTrack = "no subtitle track";
    public const string OnlyImageTracks = "only image subtitle tracks (PGS/VobSub), which need OCR";
    public const string NoEnglishTextTrack = "no English text subtitle track";

    // ── Parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// The tracks of <c>mkvmerge -J</c>'s JSON, in its order. An error instead when the text is
    /// not that JSON, when mkvmerge reported errors, or when it could not read the container.
    /// Never throws.
    /// </summary>
    public static MkvTrackList Parse(string json)
    {
      try
      {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
          return MkvTrackList.Failed(NotJson);

        string? reported = FirstError(root);
        if (reported != null)
          return MkvTrackList.Failed("mkvmerge: " + reported);

        if (root.TryGetProperty("container", out JsonElement container)
          && (Bool(container, "recognized") == false || Bool(container, "supported") == false))
          return MkvTrackList.Failed("mkvmerge cannot read this file (format not recognised or not supported)");

        var tracks = new List<MkvTrackInfo>();
        if (root.TryGetProperty("tracks", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
          foreach (JsonElement t in list.EnumerateArray())
          {
            if (t.ValueKind != JsonValueKind.Object
              || !t.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int trackId))
              continue;
            JsonElement p = t.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object
              ? props : default;
            tracks.Add(new MkvTrackInfo
            {
              Id = trackId,
              Type = Str(t, "type"),
              Codec = Str(t, "codec"),
              CodecId = Str(p, "codec_id"),
              Language = Str(p, "language"),
              LanguageIetf = Str(p, "language_ietf"),
              Name = Str(p, "track_name"),
              Forced = Bool(p, "forced_track") ?? false,
              Default = Bool(p, "default_track") ?? false,
              Events = Int(p, "num_index_entries"),
            });
          }
        }
        return new MkvTrackList { Tracks = tracks };
      }
      catch (JsonException)
      {
        return MkvTrackList.Failed(NotJson);
      }
    }

    private const string NotJson = "mkvmerge did not print a track list (no JSON in its output)";

    /// <summary>The first of the JSON's <c>errors</c>, e.g. "The file '...' could not be opened for reading: open file error."</summary>
    private static string? FirstError(JsonElement root)
    {
      if (!root.TryGetProperty("errors", out JsonElement errors) || errors.ValueKind != JsonValueKind.Array)
        return null;
      foreach (JsonElement e in errors.EnumerateArray())
      {
        string text = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "").Trim() : "";
        if (text.Length > 0) return text;
      }
      return null;
    }

    private static string? FirstError(string json)
    {
      try
      {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? FirstError(doc.RootElement) : null;
      }
      catch (JsonException)
      {
        return null;
      }
    }

    private static string Str(JsonElement obj, string name)
      => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool? Bool(JsonElement obj, string name)
    {
      if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement v)) return null;
      return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null;
    }

    private static int? Int(JsonElement obj, string name)
      => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    // ── The pick ─────────────────────────────────────────────────────────

    /// <summary>
    /// The EN track (B2): among English text tracks (<see cref="MkvTrackInfo.IsEnglish"/>,
    /// <see cref="TextCodecIds"/>) that are not forced, the one with the most events. A track
    /// whose count is missing or 0 (a file without an index) counts as 0; a tie goes to the
    /// lower id, the first in the file. The default flag is ignored: mkvmerge sets it on every
    /// track unless told otherwise.
    /// <para>
    /// <paramref name="trackId"/> overrides the choice. It must name a text subtitle track; its
    /// language and forced flag are not checked (an override is how an untagged or mistagged
    /// English track is used).
    /// </para>
    /// </summary>
    public static MkvTrackPick Pick(IReadOnlyList<MkvTrackInfo> tracks, int? trackId = null)
    {
      if (trackId != null)
      {
        MkvTrackInfo? chosen = tracks.FirstOrDefault(t => t.Id == trackId.Value);
        string id = trackId.Value.ToString(CultureInfo.InvariantCulture);
        if (chosen == null)
          return MkvTrackPick.None("no track " + id + " in the file");
        if (!chosen.IsText)
          return MkvTrackPick.None("track " + id + " (" + (chosen.IsSubtitles ? "" : chosen.Type + " ")
            + chosen.CodecId + ") is not an ASS, SSA or SRT subtitle track");
        return new MkvTrackPick { Track = chosen };
      }

      MkvTrackInfo? best = tracks
        .Where(t => t.IsText && t.IsEnglish && !t.Forced)
        .OrderByDescending(t => t.Events ?? 0)
        .ThenBy(t => t.Id)
        .FirstOrDefault();
      if (best != null)
        return new MkvTrackPick { Track = best };

      List<MkvTrackInfo> subtitles = tracks.Where(t => t.IsSubtitles).ToList();
      if (subtitles.Count == 0)
        return MkvTrackPick.None(NoSubtitleTrack);
      if (subtitles.All(t => t.IsImage))
        return MkvTrackPick.None(OnlyImageTracks);

      // English tracks passed over, and why: what to check, or give to --track.
      List<string> passedOver = subtitles.Where(t => t.IsEnglish)
        .Select(t => t.Id.ToString(CultureInfo.InvariantCulture) + ": "
          + (t.IsText ? "forced" : t.IsImage ? "image " + t.CodecId : t.CodecId))
        .ToList();
      return MkvTrackPick.None(passedOver.Count == 0
        ? NoEnglishTextTrack
        : NoEnglishTextTrack + " (" + string.Join(", ", passedOver) + ")");
    }

    // ── Running mkvmerge ─────────────────────────────────────────────────

    /// <summary>
    /// Test hook: runs the process instead of starting it (the start info is the one
    /// <see cref="ListAsync"/> built). Null in normal runs; reset it in the test's Dispose.
    /// </summary>
    internal static Func<ProcessStartInfo, CancellationToken, Task<CliProcessResult>>? RunnerOverride { get; set; }

    /// <summary>What to tell the user when mkvmerge cannot be found.</summary>
    public const string NotFoundMessage =
      "mkvmerge not found: install MKVToolNix, or put its folder on PATH or in Tools Directory (Preferences)";

    /// <summary>
    /// The tracks of <paramref name="mkvFile"/> from <c>mkvmerge -J</c>. Every failure (mkvmerge
    /// not found or not starting, exit code 2 or more, output that is not its JSON) comes back as
    /// <see cref="MkvTrackList.Error"/>; only a cancel throws. Exit code 1 means warnings and the
    /// JSON is read as usual.
    /// </summary>
    public static async Task<MkvTrackList> ListAsync(string mkvFile, CancellationToken ct = default)
    {
      string? exe = ConstantSettings.ResolveTool(ConstantSettings.ExeMkvMerge);
      if (exe == null)
        return MkvTrackList.Failed(NotFoundMessage);

      CliProcessResult result;
      try
      {
        // A full path, so a name starting with '@' or '-' is not read as an option.
        ProcessStartInfo psi = StartInfo(exe, new[] { "--output-charset", "UTF-8", "-J", Path.GetFullPath(mkvFile) });
        result = await (RunnerOverride ?? RunAsync)(psi, ct).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        return MkvTrackList.Failed("could not start mkvmerge: " + ex.Message);
      }

      if (result.ExitCode == null)
        return MkvTrackList.Failed("mkvmerge did not finish");
      if (result.ExitCode.Value >= 2)
      {
        // Its JSON names the error ("The file ... could not be opened"); stderr otherwise.
        string detail = FirstError(result.Stdout)
          ?? LastLine(result.Stderr) ?? LastLine(result.Stdout) ?? "(no output)";
        return MkvTrackList.Failed("mkvmerge exited with code "
          + result.ExitCode.Value.ToString(CultureInfo.InvariantCulture) + ": " + detail);
      }
      return Parse(result.Stdout);
    }

    /// <summary><see cref="ListAsync"/>, then <see cref="Pick"/>; a listing error is the reason.</summary>
    public static async Task<MkvTrackPick> PickAsync(string mkvFile, int? trackId = null, CancellationToken ct = default)
    {
      MkvTrackList list = await ListAsync(mkvFile, ct).ConfigureAwait(false);
      return list.Error != null ? MkvTrackPick.None(list.Error) : Pick(list.Tracks, trackId);
    }

    /// <summary>
    /// The start info for an MKVToolNix tool: <see cref="UtilsCommon.makeToolStartInfo"/>
    /// (UTF-8 pipes, no window), the arguments one by one, and on Linux and macOS a UTF-8
    /// locale when the inherited one is not: under the C/POSIX locale mkvmerge drops a
    /// non-ASCII file name from its arguments (or cannot open it) and cuts its output at the
    /// first non-ASCII character.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> arguments)
    {
      ProcessStartInfo psi = UtilsCommon.makeToolStartInfo(exe, "", redirectStdout: true, redirectStderr: true);
      foreach (string arg in arguments) psi.ArgumentList.Add(arg);
      if (!OperatingSystem.IsWindows() && !IsUtf8Locale(psi.Environment))
        psi.Environment["LC_ALL"] = "C.UTF-8";
      return psi;
    }

    /// <summary>Whether the locale these variables select (LC_ALL, else LC_CTYPE, else LANG) is UTF-8.</summary>
    internal static bool IsUtf8Locale(IDictionary<string, string?> environment)
    {
      foreach (string name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
      {
        if (environment.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value))
          return value.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            || value.Contains("utf8", StringComparison.OrdinalIgnoreCase);
      }
      return false;
    }

    private static string? LastLine(string text)
      => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>
    /// Runs an MKVToolNix tool to its end. A cancel kills it and waits (a few seconds at most)
    /// for it to exit, so a file it was writing is closed when the cancel reaches the caller.
    /// </summary>
    internal static async Task<CliProcessResult> RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
      using var process = new Process { StartInfo = psi };
      process.Start();
      Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
      Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
      try
      {
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        try
        {
          process.Kill(entireProcessTree: true);
          process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        throw;
      }
      return new CliProcessResult
      {
        ExitCode = process.ExitCode,
        Stdout = await stdout.ConfigureAwait(false),
        Stderr = await stderr.ConfigureAwait(false),
      };
    }
  }
}
