using System;
using System.IO;
using System.Threading.Tasks;
using subs2srs.UiTests.Harness;
using Xunit;

namespace subs2srs.UiTests.Tests
{
    /// <summary>
    /// Preferences dialog: OK persists to the (redirected) preferences.json,
    /// Cancel leaves it untouched.
    /// </summary>
    [Collection(GtkCollection.Name)]
    public class DialogPrefFlowTests
    {
        private readonly GtkFixture _gtk;

        public DialogPrefFlowTests(GtkFixture gtk) => _gtk = gtk;

        [Fact]
        public async Task Ok_PersistsValuesToPreferencesFile()
        {
            using var scope = new UiTestScope(_gtk);
            string toolsDir = Path.Combine(scope.TempDir, "tools");

            var dlg = await scope.OpenAsync(() => new DialogPref(null!));

            await _gtk.RunOnGtkAsync(async () =>
            {
                dlg.SetPropertyValue("Max Parallel Tasks", 3);
                dlg.SetPropertyValue("Tools Directory", toolsDir);
                dlg.SetPropertyValue("Enable Logging", false);
                dlg.AcceptAndClose();
                await Pump.IdleAsync();
            });

            Assert.Equal(3, ConstantSettings.MaxParallelTasks);
            Assert.Equal(toolsDir, ConstantSettings.ToolsDir);
            Assert.False(ConstantSettings.EnableLogging);
            Assert.True(File.Exists(scope.PreferencesJsonPath), "preferences.json not written");

            // Re-read from disk into a fresh Prefs object.
            ConstantSettings.Prefs = new PreferencesData();
            PrefIO.read();
            Assert.Equal(3, ConstantSettings.MaxParallelTasks);
            Assert.Equal(toolsDir, ConstantSettings.ToolsDir);
            Assert.False(ConstantSettings.EnableLogging);

            Assert.Equal(0, scope.OpenWindowCount);
        }

        [Fact]
        public async Task Cancel_LeavesPreferencesFileUnchanged()
        {
            using var scope = new UiTestScope(_gtk);
            PrefIO.Write();
            string before = File.ReadAllText(scope.PreferencesJsonPath);
            var beforeStamp = File.GetLastWriteTimeUtc(scope.PreferencesJsonPath);
            int origParallel = ConstantSettings.MaxParallelTasks;

            var dlg = await scope.OpenAsync(() => new DialogPref(null!));

            await _gtk.RunOnGtkAsync(async () =>
            {
                dlg.SetPropertyValue("Max Parallel Tasks", 7);
                dlg.Close(); // Cancel path: no SavePreferences
                await Pump.IdleAsync();
            });

            Assert.Equal(origParallel, ConstantSettings.MaxParallelTasks);
            Assert.Equal(before, File.ReadAllText(scope.PreferencesJsonPath));
            Assert.Equal(beforeStamp, File.GetLastWriteTimeUtc(scope.PreferencesJsonPath));
            Assert.Equal(0, scope.OpenWindowCount);
        }
    }
}
