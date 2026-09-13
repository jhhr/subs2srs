using System;
using System.IO;
using System.Threading;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// Isolates a test from the user's real configuration:
    /// temp directory, redirected preferences file, fresh Settings, logging off,
    /// sequential media generation, recorded message hooks.
    /// </summary>
    public class TestScope : IDisposable
    {
        private readonly string _origSettingsFilename;
        private readonly PreferencesData _origPrefs;

        public string TempDir { get; }
        public string OutputDir { get; }
        public RecordingMsgHooks Msgs { get; }

        public TestScope(string? tempDirNameSuffix = null)
        {
            _origSettingsFilename = ConstantSettings.SettingsFilename;
            _origPrefs = ConstantSettings.Prefs;

            TempDir = Path.Combine(
                Path.GetTempPath(),
                "subs2srs_test_" + Guid.NewGuid().ToString("N") + (tempDirNameSuffix ?? ""));
            Directory.CreateDirectory(TempDir);
            OutputDir = Path.Combine(TempDir, "out");
            Directory.CreateDirectory(OutputDir);

            // Redirect preferences into the temp dir and start from defaults.
            ConstantSettings.SettingsFilename = Path.Combine(TempDir, "preferences.txt");
            ConstantSettings.Prefs = new PreferencesData
            {
                EnableLogging = false,
                MaxParallelTasks = 1,
            };
            Settings.Instance.Reset();

            Msgs = new RecordingMsgHooks();
        }

        public string PreferencesJsonPath => Path.Combine(TempDir, "preferences.json");

        public virtual void Dispose()
        {
            Msgs.Dispose();
            ConstantSettings.SettingsFilename = _origSettingsFilename;
            ConstantSettings.Prefs = _origPrefs;
            Settings.Instance.Reset();
            DeleteDirWithRetry(TempDir);
        }

        /// <summary>ffmpeg may still hold a handle for a moment after exit.</summary>
        public static void DeleteDirWithRetry(string dir, int attempts = 5)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return;
                }
                catch (IOException) when (i < attempts - 1) { Thread.Sleep(200 * (i + 1)); }
                catch (UnauthorizedAccessException) when (i < attempts - 1) { Thread.Sleep(200 * (i + 1)); }
                catch { return; }
            }
        }
    }
}
