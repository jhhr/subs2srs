using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>Outcome of one item of an <see cref="AiBulkRunner"/> run: a value or the exception that stopped it.</summary>
  public sealed class BulkResult<T>
  {
    public T? Value { get; init; }
    public Exception? Error { get; init; }
    public bool Ok => Error == null;
    public TimeSpan Elapsed { get; init; }
  }


  /// <summary>
  /// Port of <c>make_inner_bulk_op</c> (plan §A.4): a semaphore bounds the requests in flight, a
  /// pacer keeps request starts at most <c>rpm</c> per minute (60/rpm seconds apart), every item
  /// runs to completion or failure (retries live in the provider), cancellation comes from the
  /// progress reporter and a token, and progress says "17 of 60 chunks, ~40 s left" using the
  /// completed items' timing.
  /// </summary>
  public sealed class AiBulkRunner
  {
    private readonly int maxConcurrent;
    private readonly TimeSpan minInterval;
    private readonly object paceLock = new object();
    private long nextStartTicks; // Stopwatch ticks

    /// <summary>How to wait; tests replace it to record pacing instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public AiBulkRunner(int maxConcurrent, int rpm)
    {
      this.maxConcurrent = Math.Max(1, maxConcurrent);
      minInterval = rpm <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(60.0 / rpm);
    }

    /// <summary>Wait for the pacer's next slot. Slots are handed out in call order.</summary>
    public async Task WaitForSlotAsync(CancellationToken ct)
    {
      if (minInterval <= TimeSpan.Zero) return;
      TimeSpan wait;
      lock (paceLock)
      {
        long now = Stopwatch.GetTimestamp();
        long start = Math.Max(now, nextStartTicks);
        wait = TimeSpan.FromSeconds((start - now) / (double)Stopwatch.Frequency);
        nextStartTicks = start + (long)(minInterval.TotalSeconds * Stopwatch.Frequency);
      }
      if (wait > TimeSpan.Zero) await Delay(wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run <paramref name="op"/> over every item. Results keep the item order. A cancelled run
    /// throws <see cref="OperationCanceledException"/>; other failures land in the results.
    /// </summary>
    public async Task<BulkResult<TResult>[]> RunAsync<TItem, TResult>(
      IReadOnlyList<TItem> items,
      Func<TItem, CancellationToken, Task<TResult>> op,
      IProgressReporter? progress,
      string label,
      CancellationToken ct)
    {
      var results = new BulkResult<TResult>[items.Count];
      if (items.Count == 0) return results;

      using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progress?.Token ?? CancellationToken.None);
      CancellationToken token = linked.Token;
      using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
      int done = 0;
      double completedSeconds = 0;
      var runStart = Stopwatch.StartNew();
      object progressLock = new object();

      // Called under progressLock so that a slower task cannot overwrite a later count.
      void Report()
      {
        if (progress == null) return;
        int d = done;
        int percent = (int)Math.Round(100.0 * d / items.Count);
        string eta = "";
        if (d > 0 && d < items.Count)
        {
          double avg = completedSeconds / d;
          double left = (items.Count - d) * avg / maxConcurrent;
          eta = FormattableString.Invariant($", ~{Math.Max(1, Math.Round(left)):0} s left");
        }
        progress.UpdateProgress(percent, FormattableString.Invariant($"{label}: {d} of {items.Count} chunks{eta}"));
      }

      lock (progressLock) Report();

      var tasks = new List<Task>(items.Count);
      for (int i = 0; i < items.Count; i++)
      {
        int index = i;
        tasks.Add(Task.Run(async () =>
        {
          await gate.WaitAsync(token).ConfigureAwait(false);
          try
          {
            if (progress != null && progress.Cancel) linked.Cancel();
            token.ThrowIfCancellationRequested();
            await WaitForSlotAsync(token).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
              TResult value = await op(items[index], token).ConfigureAwait(false);
              results[index] = new BulkResult<TResult> { Value = value, Elapsed = sw.Elapsed };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
              throw;
            }
            catch (Exception ex)
            {
              results[index] = new BulkResult<TResult> { Error = ex, Elapsed = sw.Elapsed };
            }
            lock (progressLock)
            {
              completedSeconds += sw.Elapsed.TotalSeconds;
              done++;
              Report();
            }
          }
          finally
          {
            gate.Release();
          }
        }, token));
      }

      try
      {
        await Task.WhenAll(tasks).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        throw new OperationCanceledException(label + " cancelled", token);
      }
      if (progress != null && progress.Cancel) throw new OperationCanceledException(label + " cancelled");
      return results;
    }
  }
}
