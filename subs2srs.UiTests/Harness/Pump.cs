using System;
using System.Threading.Tasks;

namespace subs2srs.UiTests.Harness
{
    /// <summary>
    /// Helpers that let the GTK main loop keep running while a test waits for a
    /// condition. All methods must be awaited on the GTK thread (inside
    /// GtkFixture.RunOnGtkAsync); they never block.
    /// </summary>
    public static class Pump
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Poll <paramref name="condition"/> every 16 ms until true or timeout.</summary>
        public static Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
        {
            if (condition()) return Task.CompletedTask;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
            GLib.Functions.TimeoutAdd(0, 16, () =>
            {
                try
                {
                    if (condition()) { tcs.TrySetResult(); return false; }
                    if (DateTime.UtcNow > deadline)
                    {
                        tcs.TrySetException(new TimeoutException(
                            $"Condition not met within {timeout ?? DefaultTimeout}: {what ?? "(unnamed)"}"));
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    return false;
                }
            });
            return tcs.Task;
        }

        /// <summary>Resolve after the main loop has gone idle once.</summary>
        public static Task IdleAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GLib.Functions.IdleAdd(0, () => { tcs.TrySetResult(); return false; });
            return tcs.Task;
        }

        /// <summary>Resolve after <paramref name="ms"/> milliseconds of main-loop time.</summary>
        public static Task DelayAsync(uint ms)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GLib.Functions.TimeoutAdd(0, ms, () => { tcs.TrySetResult(); return false; });
            return tcs.Task;
        }

        /// <summary>
        /// Wait until the window is mapped and at least two frames have been drawn,
        /// then one more idle so deferred IdleAdd work (header styling etc.) has run.
        /// </summary>
        public static async Task SettleAsync(Gtk.Window window, TimeSpan? timeout = null)
        {
            await WaitUntilAsync(() => window.GetMapped(), timeout, "window mapped");
            await FramesAsync(window, 2, timeout);
            await IdleAsync();
        }

        /// <summary>Wait for <paramref name="frames"/> frame-clock ticks on the widget.</summary>
        public static Task FramesAsync(Gtk.Widget widget, int frames, TimeSpan? timeout = null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int remaining = frames;
            bool usedTick = false;
            try
            {
                widget.AddTickCallback((w, clock) =>
                {
                    if (--remaining <= 0) { tcs.TrySetResult(); return false; }
                    return true;
                });
                usedTick = true;
            }
            catch
            {
                // Fall through to the timer-based fallback.
            }

            if (!usedTick)
                return DelayAsync(100);

            // Safety net: a hidden/unmapped widget never ticks.
            var deadline = timeout ?? DefaultTimeout;
            GLib.Functions.TimeoutAdd(0, (uint)deadline.TotalMilliseconds, () =>
            {
                tcs.TrySetResult();
                return false;
            });
            return tcs.Task;
        }
    }
}
