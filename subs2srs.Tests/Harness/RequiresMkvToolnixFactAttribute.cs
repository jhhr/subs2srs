using System;
using Xunit;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// A [Fact] that is skipped when mkvmerge cannot be found (Tools Directory, PATH, or on
    /// Windows MKVToolNix's install folder): MKVToolNix is not installed on every machine or CI job.
    /// </summary>
    public sealed class RequiresMkvToolnixFactAttribute : FactAttribute
    {
        public RequiresMkvToolnixFactAttribute()
        {
            if (!MkvToolnixProbe.IsAvailable)
                Skip = "mkvmerge (MKVToolNix) not found";
        }
    }

    public static class MkvToolnixProbe
    {
        private static readonly Lazy<bool> _available = new(() =>
        {
            try { return ConstantSettings.ResolveTool(ConstantSettings.ExeMkvMerge) != null; }
            catch { return false; }
        });

        public static bool IsAvailable => _available.Value;
    }
}
