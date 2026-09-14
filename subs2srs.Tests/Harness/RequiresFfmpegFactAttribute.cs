using System;
using Xunit;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// A [Fact] that is skipped when ffmpeg cannot be found (Tools Directory or PATH, PATHEXT-aware).
    /// </summary>
    public sealed class RequiresFfmpegFactAttribute : FactAttribute
    {
        public RequiresFfmpegFactAttribute()
        {
            if (!FfmpegProbe.IsAvailable)
                Skip = "ffmpeg not found on PATH";
        }
    }

    /// <summary>
    /// A [Theory] that is skipped when ffmpeg cannot be found.
    /// </summary>
    public sealed class RequiresFfmpegTheoryAttribute : TheoryAttribute
    {
        public RequiresFfmpegTheoryAttribute()
        {
            if (!FfmpegProbe.IsAvailable)
                Skip = "ffmpeg not found on PATH";
        }
    }

    /// <summary>
    /// A [Fact] that is skipped when ffmpeg is missing or offers none of the
    /// encoders subs2srs can use for the given animated snapshot format.
    /// </summary>
    public sealed class RequiresFfmpegEncoderFactAttribute : FactAttribute
    {
        public RequiresFfmpegEncoderFactAttribute(AnimatedSnapshotFormat format)
        {
            if (!FfmpegProbe.IsAvailable)
                Skip = "ffmpeg not found on PATH";
            else if (UtilsAnimatedSnapshot.EncoderFor(format) == null)
                Skip = "ffmpeg has no encoder for animated " + format.ToString().ToLowerInvariant();
        }
    }

    public static class FfmpegProbe
    {
        private static readonly Lazy<bool> _available = new(() =>
        {
            try { return ConstantSettings.ResolveTool("ffmpeg") != null; }
            catch { return false; }
        });

        public static bool IsAvailable => _available.Value;

        public static string Exe => ConstantSettings.ResolveToolOrName("ffmpeg");
    }
}
