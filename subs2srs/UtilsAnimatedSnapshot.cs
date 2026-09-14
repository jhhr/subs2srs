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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace subs2srs
{
  /// <summary>
  /// ffmpeg argument builder, encoder probe and runner for animated snapshots
  /// (animated webp/avif cut straight from the source video, no audio).
  /// </summary>
  public static class UtilsAnimatedSnapshot
  {
    /// <summary>Encoders that produce an animated webp with the webp muxer, best first.</summary>
    public static readonly string[] WebpEncoders = { "libwebp_anim", "libwebp" };

    /// <summary>AV1 encoders usable with the avif muxer, best first.</summary>
    public static readonly string[] AvifEncoders = { "libaom-av1", "libsvtav1" };

    public static string Extension(AnimatedSnapshotFormat format) =>
      format == AnimatedSnapshotFormat.Avif ? ".avif" : ".webp";

    public static IReadOnlyList<string> CandidateEncoders(AnimatedSnapshotFormat format) =>
      format == AnimatedSnapshotFormat.Avif ? AvifEncoders : WebpEncoders;

    // ── Encoder probe ────────────────────────────────────────────────────

    private static readonly Regex EncoderLine = new(@"^\s*[VAS][A-Z.]{5}\s+(\S+)", RegexOptions.Multiline);

    /// <summary>Encoder names listed by <c>ffmpeg -encoders</c> (pure; no process).</summary>
    public static HashSet<string> ParseEncoderNames(string ffmpegEncodersOutput)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      if (string.IsNullOrEmpty(ffmpegEncodersOutput)) return names;
      bool pastHeader = false;
      foreach (string raw in ffmpegEncodersOutput.Split('\n'))
      {
        string line = raw.TrimEnd('\r');
        if (!pastHeader)
        {
          // The legend ends with a line of dashes; everything after it is an encoder.
          if (line.TrimStart().StartsWith("------", StringComparison.Ordinal)) pastHeader = true;
          continue;
        }
        Match m = EncoderLine.Match(line);
        if (m.Success) names.Add(m.Groups[1].Value);
      }
      return names;
    }

    /// <summary>First candidate encoder for <paramref name="format"/> that is available, or null.</summary>
    public static string SelectEncoder(AnimatedSnapshotFormat format, ISet<string> available)
    {
      foreach (string enc in CandidateEncoders(format))
        if (available.Contains(enc)) return enc;
      return null;
    }

    private static readonly object probeLock = new object();
    private static HashSet<string> cachedEncoders;

    /// <summary>
    /// Encoders of the ffmpeg that subs2srs resolves at startup (cached; empty when
    /// ffmpeg is missing). Tests can preset it with <see cref="OverrideAvailableEncoders"/>.
    /// </summary>
    public static HashSet<string> AvailableEncoders
    {
      get
      {
        lock (probeLock)
        {
          if (cachedEncoders == null)
          {
            string text = "";
            try
            {
              if (ConstantSettings.IsFFmpegAvailable)
                text = UtilsCommon.startProcessAndGetStdout(ConstantSettings.PathFFmpegExe,
                  ConstantSettings.PathFFmpegFullExe, "-hide_banner -encoders");
            }
            catch (Exception ex)
            {
              Logger.Instance.info("ffmpeg -encoders failed: " + ex.Message);
            }
            cachedEncoders = ParseEncoderNames(text);
          }
          return cachedEncoders;
        }
      }
    }

    /// <summary>Replace (or, with null, forget) the cached probe result.</summary>
    public static void OverrideAvailableEncoders(IEnumerable<string> encoders)
    {
      lock (probeLock)
        cachedEncoders = encoders == null ? null : new HashSet<string>(encoders, StringComparer.Ordinal);
    }

    /// <summary>The encoder the current ffmpeg offers for <paramref name="format"/>, or null.</summary>
    public static string EncoderFor(AnimatedSnapshotFormat format) => SelectEncoder(format, AvailableEncoders);

    /// <summary>User-facing hint when <paramref name="format"/> cannot be produced.</summary>
    public static string MissingEncoderHint(AnimatedSnapshotFormat format) =>
      format == AnimatedSnapshotFormat.Avif
        ? "This ffmpeg has no AV1 encoder (libaom-av1 or libsvtav1); animated avif snapshots are unavailable."
        : "This ffmpeg has no libwebp encoder; animated webp snapshots are unavailable.";

    // ── Argument builder (pure) ──────────────────────────────────────────

    /// <summary>
    /// libwebp quality (0–100, higher is better) mapped onto the AV1 crf scale
    /// (0–63, lower is better).
    /// </summary>
    public static int QualityToCrf(int quality)
    {
      int q = Math.Clamp(quality, 0, 100);
      return (int)Math.Round(63 - q * 63.0 / 100.0);
    }

    /// <summary>
    /// The -vf chain: optional <c>select</c>/<c>setpts</c> prefix for a multi-part
    /// snippet (times relative to the input seek point), then fps, optional crop,
    /// scale to the height (never upscaling, even width) and setsar=1.
    /// </summary>
    public static string BuildFilter(IReadOnlyList<TimeRange> relativeRanges, int fps, int height, ImageCrop crop)
    {
      var parts = new List<string>();
      if (relativeRanges != null && relativeRanges.Count > 1)
        parts.Add(UtilsGapRemoval.VideoSelectFilter(relativeRanges));

      parts.Add(FormattableString.Invariant($"fps={Math.Max(1, fps)}"));

      if (crop != null && (crop.Top > 0 || crop.Bottom > 0 || crop.Left > 0 || crop.Right > 0))
      {
        parts.Add(FormattableString.Invariant(
          $"crop=iw-{Math.Max(0, crop.Left) + Math.Max(0, crop.Right)}:ih-{Math.Max(0, crop.Top) + Math.Max(0, crop.Bottom)}:{Math.Max(0, crop.Left)}:{Math.Max(0, crop.Top)}"));
      }

      int h = Math.Max(2, height);
      parts.Add(FormattableString.Invariant(
        $"scale='trunc(min({h},ih)*dar/2+0.5)*2':'min({h},ih)':flags=lanczos+accurate_rnd"));
      parts.Add("setsar=1");
      return string.Join(",", parts);
    }

    /// <summary>Codec options for <paramref name="encoder"/> at the given 0–100 quality.</summary>
    public static string CodecArgs(string encoder, int quality)
    {
      int q = Math.Clamp(quality, 0, 100);
      switch (encoder)
      {
        case "libwebp_anim":
        case "libwebp":
          return FormattableString.Invariant($"-c:v {encoder} -lossless 0 -compression_level 6 -quality {q}");
        case "libaom-av1":
          return FormattableString.Invariant($"-c:v libaom-av1 -crf {QualityToCrf(q)} -b:v 0 -cpu-used 8 -pix_fmt yuv420p");
        case "libsvtav1":
          return FormattableString.Invariant($"-c:v libsvtav1 -crf {QualityToCrf(q)} -preset 10 -pix_fmt yuv420p");
        default:
          throw new ArgumentException("Unsupported animated snapshot encoder: " + encoder);
      }
    }

    /// <summary>
    /// Full ffmpeg argument line. The seek (<c>-ss</c>/<c>-t</c>) goes before
    /// <c>-i</c> so only the span is decoded; <paramref name="relativeRanges"/>
    /// (kept parts of a multi-part snippet, relative to <paramref name="start"/>)
    /// may be null or a single range, in which case no select filter is added.
    /// </summary>
    public static string BuildArgs(string inFile, TimeSpan start, TimeSpan end,
      IReadOnlyList<TimeRange> relativeRanges, AnimatedSnapshots settings, string encoder, string outFile)
    {
      if (end < start) end = start;
      string filter = BuildFilter(relativeRanges, settings.Fps, settings.Height, settings.Crop);
      string codec = CodecArgs(encoder, settings.Quality);
      return string.Format(CultureInfo.InvariantCulture,
        "-y -an -sn -dn {0} {1} -i \"{2}\" -map_metadata -1 -loop 0 -vf \"{3}\" {4} -threads 1 \"{5}\"",
        UtilsVideo.formatStartTimeArg(start),
        UtilsVideo.formatDurationArg(start, end),
        inFile, filter, codec, outFile);
    }

    // ── Runner ───────────────────────────────────────────────────────────

    /// <summary>
    /// Encode one animated snapshot. <paramref name="absoluteRanges"/> are the kept
    /// parts (source timestamps) or null for a plain line.
    /// </summary>
    public static void Encode(string inFile, TimeSpan start, TimeSpan end,
      IReadOnlyList<TimeRange> absoluteRanges, AnimatedSnapshots settings, string encoder, string outFile)
    {
      List<TimeRange> rel = absoluteRanges == null ? null : UtilsGapRemoval.Relative(absoluteRanges, start);
      string args = BuildArgs(inFile, start, end, rel, settings, encoder, outFile);
      UtilsCommon.startFFmpeg(args, false, true);
    }
  }
}
