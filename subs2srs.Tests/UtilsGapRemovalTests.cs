//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace subs2srs.Tests
{
    public class UtilsGapRemovalTests
    {
        private static TimeRange R(double s, double e) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e));

        [Fact]
        public void SelectFilters_UseInvariantDecimalPoint()
        {
            var old = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var ranges = new List<TimeRange> { R(0, 2.1), R(2.6, 4) };
                Assert.Equal("aselect='between(t,0.000,2.100)+between(t,2.600,4.000)',asetpts=N/SR/TB",
                    UtilsGapRemoval.AudioSelectFilter(ranges));
                Assert.Equal("select='between(t,0.000,2.100)+between(t,2.600,4.000)',setpts=N/FRAME_RATE/TB",
                    UtilsGapRemoval.VideoSelectFilter(ranges));
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }

        [Fact]
        public void Relative_ShiftsAndClamps()
        {
            var rel = UtilsGapRemoval.Relative(new List<TimeRange> { R(10, 12), R(13, 14) }, TimeSpan.FromSeconds(10.5));
            Assert.Equal(TimeSpan.Zero, rel[0].Start);
            Assert.Equal(TimeSpan.FromSeconds(1.5), rel[0].End);
            Assert.Equal(TimeSpan.FromSeconds(2.5), rel[1].Start);
        }

        [Fact]
        public void ConcatListFile_EscapesQuotesAndUsesForwardSlashes()
        {
            string list = UtilsGapRemoval.ConcatListFile(new[] { @"C:\out\clip.part0.avi", "/tmp/it's.avi" });
            Assert.Equal("file 'C:/out/clip.part0.avi'\nfile '/tmp/it'\\''s.avi'\n", list);
        }

        [Fact]
        public void TotalDuration_SumsRanges()
        {
            Assert.Equal(TimeSpan.FromSeconds(3), UtilsGapRemoval.TotalDuration(new List<TimeRange> { R(0, 2), R(5, 6) }));
        }
    }
}
