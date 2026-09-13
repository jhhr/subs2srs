using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// Layer 1: every window can be constructed, shown, drawn and destroyed
    /// without errors or leaks.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class WindowSmokeTests
    {
        private readonly GtkFixture _gtk;

        public WindowSmokeTests(GtkFixture gtk) => _gtk = gtk;

        private static List<MkvTrack> FakeTracks() => new()
        {
            new MkvTrack { TrackID = "1", TrackType = UtilsMkv.TrackType.SUBTITLES, CodecID = "S_TEXT/ASS", Extension = "ass", Lang = "jpn" },
            new MkvTrack { TrackID = "2", TrackType = UtilsMkv.TrackType.SUBTITLES, CodecID = "S_TEXT/UTF8", Extension = "srt", Lang = "eng" },
            new MkvTrack { TrackID = "3", TrackType = UtilsMkv.TrackType.AUDIO, CodecID = "A_AAC", Extension = "aac", Lang = "jpn" },
        };

        public static IEnumerable<object[]> Windows()
        {
            yield return new object[] { "DialogPref", (Func<GtkFixture, Gtk.Window>)(_ => new DialogPref(null!)) };
            yield return new object[] { "DialogAdvancedSubtitleOptions", (Func<GtkFixture, Gtk.Window>)(_ => new DialogAdvancedSubtitleOptions(null!)) };
            yield return new object[] { "DialogDuelingSubtitles", (Func<GtkFixture, Gtk.Window>)(_ => new DialogDuelingSubtitles(null!)) };
            yield return new object[] { "DialogExtractAudioFromMedia", (Func<GtkFixture, Gtk.Window>)(_ => new DialogExtractAudioFromMedia(null!)) };
            yield return new object[] { "DialogMkvExtract", (Func<GtkFixture, Gtk.Window>)(_ => new DialogMkvExtract(null!)) };
            yield return new object[] { "DialogSelectMkvTrack", (Func<GtkFixture, Gtk.Window>)(_ => new DialogSelectMkvTrack(null!, "fake.mkv", 1, FakeTracks())) };
            yield return new object[] { "DialogSubtitleStyle", (Func<GtkFixture, Gtk.Window>)(_ => new DialogSubtitleStyle(null!)) };
        }

        [Theory]
        [MemberData(nameof(Windows))]
        public async Task Window_OpensDrawsAndCloses(string name, Func<GtkFixture, Gtk.Window> ctor)
        {
            using var scope = new UiTestScope(_gtk);

            var win = await scope.OpenAsync(() => ctor(_gtk));

            var (mapped, w, h) = _gtk.RunOnGtk(() =>
            {
                Screenshot.TrySave(win, name);
                return (win.GetMapped(), win.GetWidth(), win.GetHeight());
            });
            Assert.True(mapped, $"{name} not mapped");
            Assert.True(w > 0 && h > 0, $"{name} has zero size ({w}x{h})");

            await scope.CloseAsync(win);
            Assert.Equal(0, scope.OpenWindowCount);
        }

        [Fact]
        public async Task MainWindow_OpensWithPreferenceDefaults()
        {
            using var scope = new UiTestScope(_gtk);
            if (!FfmpegProbe.IsAvailable)
                scope.ExpectErrors(); // startup check reports missing ffmpeg

            var win = await scope.OpenMainWindowAsync();

            var state = _gtk.RunOnGtk(() =>
            {
                Screenshot.TrySave(win, "MainWindow");
                return new
                {
                    Mapped = win.GetMapped(),
                    Audio = win._chkGenerateAudio.GetActive(),
                    Snapshots = win._chkGenerateSnapshots.GetActive(),
                    Video = win._chkGenerateVideo.GetActive(),
                    GoSensitive = win._btnGo.GetSensitive(),
                    CancelSensitive = win._btnCancel.GetSensitive(),
                    Deck = win._txtDeckName.GetText(),
                };
            });

            Assert.True(state.Mapped);
            Assert.Equal(ConstantSettings.DefaultEnableAudioClipGeneration, state.Audio);
            Assert.Equal(ConstantSettings.DefaultEnableSnapshotsGeneration, state.Snapshots);
            Assert.Equal(ConstantSettings.DefaultEnableVideoClipsGeneration, state.Video);
            Assert.Equal(FfmpegProbe.IsAvailable, state.GoSensitive);
            Assert.False(state.CancelSensitive);
            Assert.False(string.IsNullOrEmpty(state.Deck));

            await scope.CloseAsync(win);
            Assert.Equal(0, scope.OpenWindowCount);
        }

        [Fact]
        public async Task DialogPreview_HidesOnCloseAndDestroysOnCleanup()
        {
            using var scope = new UiTestScope(_gtk);

            var win = await scope.OpenAsync(() => new DialogPreview());

            await _gtk.RunOnGtkAsync(async () =>
            {
                Screenshot.TrySave(win, "DialogPreview");
                win.Close(); // close-request handler hides instead of destroying
                await Pump.WaitUntilAsync(() => !win.GetVisible(), what: "preview hidden");
            });
            Assert.Equal(1, scope.OpenWindowCount); // still alive, just hidden

            await scope.CloseAsync(win); // CleanupAndDestroy
            Assert.Equal(0, scope.OpenWindowCount);
        }
    }
}
