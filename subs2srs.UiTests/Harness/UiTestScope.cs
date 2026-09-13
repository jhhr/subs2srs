using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.UiTests.Harness
{
    /// <summary>
    /// Per-test scope for GTK tests: everything <see cref="TestScope"/> does, plus
    /// window lifetime tracking. On dispose it destroys leaked windows, asserts the
    /// toplevel count is zero and fails the test if an error message was recorded
    /// (unless <see cref="ExpectErrors"/> was called).
    /// </summary>
    public sealed class UiTestScope : TestScope
    {
        private readonly GtkFixture _gtk;
        private readonly List<Gtk.Window> _open = new();
        private bool _expectErrors;
        private readonly int _toplevelsBefore;

        public UiTestScope(GtkFixture gtk, string? tempDirNameSuffix = null) : base(tempDirNameSuffix)
        {
            _gtk = gtk;
            _toplevelsBefore = gtk.RunOnGtk(CountToplevels);
        }

        public GtkFixture Fixture => _gtk;

        /// <summary>Allow the test to finish even though UtilsMsg.showErrMsg was called.</summary>
        public void ExpectErrors() => _expectErrors = true;

        public Task<MainWindow> OpenMainWindowAsync()
            => OpenAsync(() => new MainWindow(_gtk.App));

        /// <summary>Construct a window on the GTK thread, show it and wait until it settled.</summary>
        public Task<T> OpenAsync<T>(Func<T> ctor) where T : Gtk.Window
        {
            return _gtk.RunOnGtkAsync(async () =>
            {
                var win = ctor();
                Track(win);
                win.Show();
                await Pump.SettleAsync(win);
                return win;
            });
        }

        private void Track(Gtk.Window win)
        {
            _open.Add(win);
        }

        /// <summary>True while GTK still lists the window as a toplevel (i.e. not destroyed).</summary>
        public static bool IsAlive(Gtk.Window win)
        {
            var list = Gtk.Window.GetToplevels();
            uint n = list.GetNItems();
            nint handle = win.Handle.DangerousGetHandle();
            for (uint i = 0; i < n; i++)
            {
                var item = list.GetObject(i);
                if (item != null && item.Handle.DangerousGetHandle() == handle)
                    return true;
            }
            return false;
        }

        /// <summary>Destroy a window (DialogPreview uses its own cleanup) and let the loop settle.</summary>
        public Task CloseAsync(Gtk.Window win)
        {
            return _gtk.RunOnGtkAsync(async () =>
            {
                DestroyWindow(win);
                await Pump.IdleAsync();
            });
        }

        private void DestroyWindow(Gtk.Window win)
        {
            if (win is DialogPreview preview)
                preview.CleanupAndDestroy();
            else
                win.Destroy();
            _open.Remove(win);
        }

        public static int CountToplevels()
        {
            return (int)Gtk.Window.GetToplevels().GetNItems();
        }

        public int OpenWindowCount => _gtk.RunOnGtk(CountToplevels) - _toplevelsBefore;

        public override void Dispose()
        {
            List<string> leaked = new();
            try
            {
                _gtk.RunOnGtk(() =>
                {
                    foreach (var w in _open.ToArray())
                    {
                        if (!IsAlive(w)) { _open.Remove(w); continue; }
                        leaked.Add(w.GetType().Name);
                        try { DestroyWindow(w); } catch { }
                    }
                });
                // Let destroy notifications drain.
                _gtk.RunOnGtkAsync(() => Pump.IdleAsync()).GetAwaiter().GetResult();
            }
            finally
            {
                base.Dispose();
            }

            int remaining = OpenWindowCount;
            var errors = Msgs.Errors;

            Assert.True(leaked.Count == 0, $"Test leaked windows: {string.Join(", ", leaked)}");
            Assert.True(remaining == 0, $"{remaining} toplevel window(s) still alive after test");
            if (!_expectErrors)
                Assert.True(errors.Count == 0, "Unexpected error message(s):\n" + string.Join("\n---\n", errors));
        }
    }
}
