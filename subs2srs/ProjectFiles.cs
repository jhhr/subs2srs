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
using System.Linq;

namespace subs2srs
{
  /// <summary>
  /// The input files of a run, found from the project's file patterns. The GUI calls
  /// <see cref="Resolve"/> from <c>MainWindow.SaveSettings</c> after copying the form into
  /// <see cref="Settings.Instance"/>; subs2srs-cli calls it after loading a project, whose
  /// <c>Files</c> arrays are never saved. No GTK here.
  /// </summary>
  public static class ProjectFiles
  {
    /// <summary>
    /// Fill every <c>Files</c> array of <see cref="Settings.Instance"/> from its pattern
    /// (subtitles in a format subs2srs reads, non-hidden, sorted as
    /// <see cref="UtilsCommon.getNonHiddenFiles"/> sorts), cut each one to the episodes the
    /// end number lets through, and update the audio file name formats for the project's
    /// audio format. The arrays are paired by index; nothing here checks their counts.
    /// </summary>
    public static void Resolve()
    {
      Settings s = Settings.Instance;

      s.Subs[0].Files = UtilsSubs.getSubsFiles(s.Subs[0].FilePattern ?? "").ToArray();
      string subs2Pattern = s.Subs[1].FilePattern ?? "";
      s.Subs[1].Files = subs2Pattern.Length > 0
        ? UtilsSubs.getSubsFiles(subs2Pattern).ToArray()
        : Array.Empty<string>();
      s.VideoClips.Files = UtilsCommon.getNonHiddenFiles(s.VideoClips.FilePattern ?? "");
      s.AudioClips.Files = UtilsCommon.getNonHiddenFiles(s.AudioClips.FilePattern ?? "");

      if (EpisodeLimit(s.EpisodeStartNumber, s.EpisodeEndNumber) is int maxCount)
      {
        s.Subs[0].Files = Cut(s.Subs[0].Files, maxCount);
        s.Subs[1].Files = Cut(s.Subs[1].Files, maxCount);
        s.VideoClips.Files = Cut(s.VideoClips.Files, maxCount);
        s.AudioClips.Files = Cut(s.AudioClips.Files, maxCount);
      }

      ConstantSettings.UpdateAudioFilenameFormats();
    }

    /// <summary>
    /// How many episodes the end number lets through, counting from the start number:
    /// <c>end - start + 1</c>, or null for no limit (end 0, or an end below the start,
    /// which the GUI has always ignored).
    /// </summary>
    public static int? EpisodeLimit(int startNumber, int endNumber)
      => endNumber > 0 && endNumber >= startNumber ? endNumber - startNumber + 1 : null;

    private static string[] Cut(string[] files, int maxCount)
      => files.Length > maxCount ? files.Take(maxCount).ToArray() : files;
  }
}
