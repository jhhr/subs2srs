using System;
using System.Diagnostics;
using System.Threading;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// IProgressReporter that records the last status and never cancels.
    /// </summary>
    public class NullProgressReporter : IProgressReporter
    {
        public virtual bool Cancel => false;
        public int StepsTotal { get; set; }
        public int LastStep { get; private set; }
        public string LastDescription { get; private set; } = "";
        public int LastPercent { get; private set; }
        public string LastText { get; private set; } = "";

        public virtual void NextStep(int step, string description)
        {
            LastStep = step;
            LastDescription = description;
        }

        public void UpdateProgress(int percent, string text)
        {
            LastPercent = percent;
            LastText = text;
        }

        public void UpdateProgress(string text) => LastText = text;
        public void EnableDetail(bool enable) { }
        public void SetDuration(TimeSpan duration) { }
        public void OnFFmpegOutput(object sender, DataReceivedEventArgs e) { }
        public virtual CancellationToken Token => CancellationToken.None;
    }

    /// <summary>
    /// Reports Cancel = true (and a cancelled token) after the given number of
    /// NextStep calls, or immediately when 0.
    /// </summary>
    public sealed class CancellingProgressReporter : NullProgressReporter
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly int _cancelAfterSteps;
        private int _steps;

        public CancellingProgressReporter(int cancelAfterSteps = 0)
        {
            _cancelAfterSteps = cancelAfterSteps;
            if (cancelAfterSteps == 0) _cts.Cancel();
        }

        public override bool Cancel => _cts.IsCancellationRequested;
        public override CancellationToken Token => _cts.Token;

        public override void NextStep(int step, string description)
        {
            base.NextStep(step, description);
            if (++_steps >= _cancelAfterSteps) _cts.Cancel();
        }
    }
}
