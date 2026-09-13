using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.UiTests.Harness
{
    /// <summary>
    /// Owns the single GTK main loop for the test process. The loop runs on a
    /// background thread; tests marshal work onto it with <see cref="RunOnGtk{T}"/>
    /// and <see cref="RunOnGtkAsync{T}"/>.
    ///
    /// Rules: never block (.Wait/.Result) inside a GTK lambda, and never call the
    /// dialogs' nested-loop Run()/RunDialog() from tests.
    /// </summary>
    public sealed class GtkFixture : IDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly Thread _thread;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Gtk.Application _app = null!;

        public Gtk.Application App => _app;
        public int MainThreadId { get; private set; }
        public string LogDir { get; }

        public GtkFixture()
        {
            // Software rendering is the most portable choice (xvfb, RDP, VMs, CI).
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GSK_RENDERER")))
                Environment.SetEnvironmentVariable("GSK_RENDERER", "cairo");

            // Keep Logger away from the user's real log directory.
            LogDir = Path.Combine(Path.GetTempPath(), "subs2srs-uitests-logs", Guid.NewGuid().ToString("N"))
                     + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(LogDir);
            ConstantSettings.LogDir = LogDir;

            _thread = new Thread(MainLoop)
            {
                Name = "GTK main loop",
                IsBackground = true,
            };
            _thread.Start();

            if (!_ready.Task.Wait(DefaultTimeout))
                throw new TimeoutException("GTK application did not activate within 30 s. "
                    + "Is a display available (DISPLAY / xvfb-run on Linux)?");
            _ready.Task.GetAwaiter().GetResult(); // rethrow startup failure, if any
        }

        private void MainLoop()
        {
            try
            {
                UtilsCommon.RegisterEncodings();
                _app = Gtk.Application.New("org.subs2srs.uitests", Gio.ApplicationFlags.NonUnique);
                _app.OnActivate += (_, _) =>
                {
                    SynchronizationContext.SetSynchronizationContext(new GtkSynchronizationContext());
                    MainThreadId = Thread.CurrentThread.ManagedThreadId;
                    _app.Hold(); // keep Run() alive with zero windows
                    _ready.TrySetResult();
                };
                _app.RunWithSynchronizationContext(null);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
        }

        public bool IsOnGtkThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        /// <summary>Run a synchronous function on the GTK thread and wait for its result.</summary>
        public T RunOnGtk<T>(Func<T> func, TimeSpan? timeout = null)
        {
            if (IsOnGtkThread) return func();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            GLib.Functions.IdleAdd(0, () =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
                return false;
            });
            return Await(tcs.Task, timeout);
        }

        public void RunOnGtk(Action action, TimeSpan? timeout = null)
            => RunOnGtk(() => { action(); return true; }, timeout);

        /// <summary>
        /// Start an async function on the GTK thread (its continuations stay on the
        /// GTK thread via the synchronization context) and wait for completion here.
        /// </summary>
        public Task<T> RunOnGtkAsync<T>(Func<Task<T>> func, TimeSpan? timeout = null)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            GLib.Functions.IdleAdd(0, () =>
            {
                try
                {
                    func().ContinueWith(t =>
                    {
                        if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                        else if (t.IsCanceled) tcs.TrySetCanceled();
                        else tcs.TrySetResult(t.Result);
                    }, TaskScheduler.Default);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
                return false;
            });
            return WithTimeout(tcs.Task, timeout ?? DefaultTimeout);
        }

        public Task RunOnGtkAsync(Func<Task> func, TimeSpan? timeout = null)
            => RunOnGtkAsync(async () => { await func(); return true; }, timeout);

        private static T Await<T>(Task<T> task, TimeSpan? timeout)
        {
            if (!task.Wait(timeout ?? DefaultTimeout))
                throw new TimeoutException("GTK thread did not complete the request in time.");
            return task.GetAwaiter().GetResult();
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            var done = await Task.WhenAny(task, Task.Delay(timeout));
            if (done != task)
                throw new TimeoutException($"GTK async operation did not complete within {timeout}.");
            return await task;
        }

        public void Dispose()
        {
            try
            {
                RunOnGtk(() =>
                {
                    _app.Release();
                    _app.Quit();
                }, TimeSpan.FromSeconds(10));
            }
            catch { /* best effort */ }
            _thread.Join(TimeSpan.FromSeconds(10));
            try { Directory.Delete(LogDir, true); } catch { }
        }
    }
}
