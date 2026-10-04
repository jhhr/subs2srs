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
using System.Globalization;
using System.IO;

namespace subs2srs
{
  /// <summary>An error refuses the run; a warning asks first (the GUI) or needs <c>--yes</c> (the command line).</summary>
  public enum GoProblemKind
  {
    Error,
    Warning,
  }

  /// <summary>One finding of <see cref="GoChecks.Run"/>, in words for the user.</summary>
  public sealed class GoProblem
  {
    public GoProblem(GoProblemKind kind, string message)
    {
      Kind = kind;
      Message = message;
    }

    public GoProblemKind Kind { get; }
    public string Message { get; }
    public bool IsError => Kind == GoProblemKind.Error;
  }

  /// <summary>
  /// The checks before a run, shared by the GUI's Go and <c>subs2srs-cli go</c>. Each one
  /// guards a failure that would otherwise come part-way through the run, after the first
  /// files are written. They read the resolved settings (after <c>MainWindow.SaveSettings</c>
  /// or the command line's file resolution) and return every problem at once. No GTK here.
  /// </summary>
  public static class GoChecks
  {
    /// <summary>
    /// Every problem found: the errors in a fixed order, then the warning.
    /// <paramref name="audioStreamIndex"/> is the chosen audio stream's position among a
    /// video's audio streams (the GUI's stream list; <see cref="AudioStreamIndex"/> for a
    /// loaded project). <paramref name="aiGroupingRuns"/>: whether the run asks the model
    /// (for Go, <see cref="WorkerSubs.aiGroupingOnGoApplies"/>). The audio-stream check runs
    /// ffprobe on every video: call this off the GTK thread.
    /// </summary>
    public static List<GoProblem> Run(Settings settings, int audioStreamIndex, bool aiGroupingRuns)
    {
      var problems = new List<GoProblem>();
      void Error(string message) => problems.Add(new GoProblem(GoProblemKind.Error, message));

      // The import file goes into the output dir, the media into <deck>.media inside it.
      if (string.IsNullOrWhiteSpace(settings.OutputDir))
        Error("Please provide Output Directory.");
      else if (OutputDirProblem(settings.OutputDir) is string outputDir)
        Error(outputDir);
      if (string.IsNullOrWhiteSpace(settings.DeckName))
        Error("Please provide Deck Name.");

      bool ffmpeg = ConstantSettings.IsFFmpegAvailable;
      if (!ffmpeg)
        Error(ConstantSettings.FFmpegMissingMessage);
      // WorkerAnimatedSnapshot throws without an encoder, after the other media are cut.
      // Without ffmpeg there is none either; the message above says why.
      else if (settings.AnimatedSnapshots.Enabled && UtilsAnimatedSnapshot.EncoderFor(settings.AnimatedSnapshots.Format) == null)
        Error(UtilsAnimatedSnapshot.MissingEncoderHint(settings.AnimatedSnapshots.Format));

      // Without the executable every episode's grouping fails (Go then uses the rules for it).
      string model = settings.Snippets.AiModel ?? "";
      if (aiGroupingRuns && ClaudeCli.IsTerminalModel(model) && string.IsNullOrWhiteSpace(ClaudeCliProvider.ResolveExecutable()))
        Error($"Snippets are grouped by AI with {model.Trim()}. {ClaudeCliProvider.NoCliMessage}");

      // Every video's audio is cut from the stream at the same position.
      bool audioFromVideo = (settings.AudioClips.Enabled && settings.AudioClips.UseAudioFromVideo) || settings.VideoClips.Enabled;
      string[]? videos = settings.VideoClips.Files;
      if (audioFromVideo && videos != null && videos.Length > 1
          && UtilsVideo.validateAudioStreamConsistency(videos, audioStreamIndex) is string warning)
        problems.Add(new GoProblem(GoProblemKind.Warning, warning));

      return problems;
    }

    /// <summary>
    /// The position of the project's audio stream among a video's audio streams, as the GUI's
    /// stream list counts them (<see cref="InfoStream.DisplayNum"/>); 0 when there is none.
    /// </summary>
    public static int AudioStreamIndex(Settings settings)
      => int.TryParse(settings.VideoClips.AudioStream?.DisplayNum, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
        ? index
        : 0;

    /// <summary>
    /// Null when <paramref name="dir"/> exists or can be created and a file can be written in
    /// it; otherwise why not. Leaves nothing behind: the test file and every folder this
    /// created are removed again.
    /// </summary>
    internal static string? OutputDirProblem(string dir)
    {
      var created = new List<string>(); // deepest first
      try
      {
        string full = Path.GetFullPath(dir);
        for (string? d = full; d != null && !Directory.Exists(d) && !File.Exists(d); d = Path.GetDirectoryName(d))
          created.Add(d);
        Directory.CreateDirectory(full);
        string probe = Path.Combine(full, ".subs2srs-write-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(probe, Array.Empty<byte>());
        File.Delete(probe);
        return null;
      }
      catch (Exception ex)
      {
        return $"Cannot write to output directory \"{dir}\": {ex.Message}";
      }
      finally
      {
        // Without recursion: a folder is removed only while it is still empty.
        foreach (string d in created)
        {
          try { Directory.Delete(d); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }
  }
}
