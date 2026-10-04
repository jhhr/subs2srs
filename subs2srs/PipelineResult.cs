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

namespace subs2srs
{
  /// <summary>How a run of the card pipeline ended.</summary>
  public enum PipelineStatus
  {
    /// <summary>Every step ran; the import file and the media are written.</summary>
    Completed,

    /// <summary>The user cancelled: the progress reporter's <c>Cancel</c> or its token.</summary>
    Cancelled,

    /// <summary>A step failed, or the output directory could not be created.</summary>
    Failed,
  }

  /// <summary>
  /// What <c>SubsProcessor.StartAsync</c> returns. The GUI still learns the outcome from the
  /// dialogs the run shows through <see cref="UtilsMsg"/>; the command line reads this. No GTK here.
  /// </summary>
  public sealed class PipelineResult
  {
    public PipelineStatus Status { get; }

    /// <summary>
    /// One line for the user: the time taken when completed, "Action cancelled." when
    /// cancelled, and when failed the failing step's progress label and what went wrong
    /// ("Generate audio clips failed: Failed to extract the audio from the video. ...").
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Cards made for each episode, by index into the <c>Files</c> arrays (map an index to
    /// its number with <see cref="Settings.EpisodeNumber"/>): the lines left after "Remove
    /// inactive lines" that are not only context for a neighbour, which is one TSV line each.
    /// Empty when the run stopped before that step.
    /// </summary>
    public IReadOnlyList<int> CardsPerEpisode { get; }

    public PipelineResult(PipelineStatus status, string message, IReadOnlyList<int>? cardsPerEpisode = null)
    {
      Status = status;
      Message = message;
      CardsPerEpisode = cardsPerEpisode ?? Array.Empty<int>();
    }
  }
}
