using System;
using System.IO;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>
    /// <see cref="ProjectFiles.Resolve"/>, the file resolution the GUI's Go and subs2srs-cli
    /// share. <c>MainWindowFlowTests.SaveSettings_ExpandsThePatterns_AndCutsToTheEndNumber</c>
    /// pins the GUI's result on the same <see cref="PatternSet"/>.
    /// </summary>
    public class ProjectFilesTests
    {
        private static void SetPatterns(PatternSet set, int start, int end, bool subs2 = true)
        {
            var s = Settings.Instance;
            s.Subs[0].FilePattern = set.Subs1Pattern;
            s.Subs[1].FilePattern = subs2 ? set.Subs2Pattern : "";
            s.VideoClips.FilePattern = set.VideoPattern;
            s.AudioClips.FilePattern = set.AudioPattern;
            s.EpisodeStartNumber = start;
            s.EpisodeEndNumber = end;
        }

        [Fact]
        public void Resolve_GivesTheGuisFiles_ForAPatternSet()
        {
            using var scope = new TestScope(" ä 日本");
            var set = PatternSet.Create(scope.TempDir);
            SetPatterns(set, PatternSet.Start, PatternSet.End);

            ProjectFiles.Resolve();

            var s = Settings.Instance;
            Assert.Equal(3, set.Subs1.Length);
            Assert.Equal(set.Subs1, s.Subs[0].Files);
            Assert.Equal(set.Subs2, s.Subs[1].Files);
            Assert.Equal(set.Video, s.VideoClips.Files);
            Assert.Equal(set.Audio, s.AudioClips.Files);
        }

        [Theory]
        [InlineData(1, 0)] // no end number
        [InlineData(5, 3)] // an end below the start is ignored, as the GUI always did
        [InlineData(1, 9)] // more room than files
        public void Resolve_KeepsEveryFile_WhenTheEndNumberCutsNothing(int start, int end)
        {
            using var scope = new TestScope(" ä 日本");
            var set = PatternSet.Create(scope.TempDir);
            SetPatterns(set, start, end);

            ProjectFiles.Resolve();

            Assert.Equal(set.AllSubs1, Settings.Instance.Subs[0].Files);
            Assert.Equal(set.AllSubs2, Settings.Instance.Subs[1].Files);
            Assert.Equal(set.AllVideo, Settings.Instance.VideoClips.Files);
            Assert.Equal(set.AllAudio, Settings.Instance.AudioClips.Files);
        }

        [Fact]
        public void Resolve_EmptySubs2Pattern_GivesNoSubs2Files_AndAStaleListIsReplaced()
        {
            using var scope = new TestScope();
            var set = PatternSet.Create(scope.TempDir);
            SetPatterns(set, 1, 0, subs2: false);
            Settings.Instance.Subs[1].Files = new[] { "stale.srt" };
            Settings.Instance.VideoClips.FilePattern = "";

            ProjectFiles.Resolve();

            Assert.Empty(Settings.Instance.Subs[1].Files);
            Assert.Empty(Settings.Instance.VideoClips.Files);
            Assert.Equal(set.AllSubs1, Settings.Instance.Subs[0].Files);
        }

        [Fact]
        public void Resolve_UpdatesTheAudioFileNameFormat_ToTheProjectsAudioFormat()
        {
            using var scope = new TestScope();
            try
            {
                Settings.Instance.AudioClips.AudioFormat = "MP3";
                ConstantSettings.UpdateAudioFilenameFormats();
                Assert.EndsWith(".mp3", ConstantSettings.AudioFilenameFormatWithExt);

                Settings.Instance.AudioClips.AudioFormat = "Opus";
                ProjectFiles.Resolve();

                Assert.EndsWith(".opus", ConstantSettings.AudioFilenameFormatWithExt);
                Assert.EndsWith(".opus", ConstantSettings.TempAudioFilename);
            }
            finally
            {
                // Back to the formats' initial value; other tests name audio files with it.
                Settings.Instance.AudioClips.AudioFormat = "MP3";
                ConstantSettings.UpdateAudioFilenameFormats();
            }
        }

        [Theory]
        [InlineData(1, 0, null)]
        [InlineData(2, 4, 3)]
        [InlineData(3, 3, 1)]
        [InlineData(5, 3, null)]
        public void EpisodeLimit_CountsFromTheStartNumber(int start, int end, int? expected)
        {
            Assert.Equal(expected, ProjectFiles.EpisodeLimit(start, end));
        }
    }
}
