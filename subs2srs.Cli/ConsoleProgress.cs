using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace subs2srs.Cli
{
  /// <summary>
  /// The pipeline's progress on stderr (from <c>subs2srs.Eval</c>'s reporter of the same name).
  /// Each step's label is a line of its own. Within a step, a terminal sees the latest text on
  /// one line rewritten with <c>\r</c>; a redirected stream (a log, a script) gets plain lines,
  /// and only when the percentage reaches a new tenth or the text carries none: the workers
  /// report every line and every clip, which would be thousands of lines per episode.
  /// Cancelled through the token (Ctrl+C).
  /// </summary>
  internal sealed class ConsoleProgress : IProgressReporter
  {
    private readonly TextWriter writer;
    private readonly CancellationToken token;
    private readonly bool terminal;
    // The media workers report from several threads.
    private readonly object gate = new();
    private string last = "";
    private int lastTenth = -1;
    // Whether the terminal's current line holds a progress text to overwrite or clear.
    private bool lineOpen;

    public ConsoleProgress(TextWriter writer, CancellationToken token)
    {
      this.writer = writer;
      this.token = token;
      terminal = ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected;
    }

    public bool Cancel => token.IsCancellationRequested;
    public CancellationToken Token => token;
    public int StepsTotal { get; set; }
    public void UpdateProgress(string text) => UpdateProgress(-1, text);
    public void EnableDetail(bool enable) { }
    public void SetDuration(TimeSpan duration) { }
    public void OnFFmpegOutput(object sender, DataReceivedEventArgs e) { }

    public void NextStep(int step, string description) => Line($"Step {step} of {StepsTotal}: {description}");

    /// <summary>A line of its own, as a step's label is: the AI pre-pass's steps and episodes.</summary>
    public void Line(string text)
    {
      lock (gate)
      {
        ClearLine();
        writer.WriteLine(text);
        last = "";
        lastTenth = -1;
      }
    }

    public void UpdateProgress(int percent, string text)
    {
      lock (gate)
      {
        if (text == last) return;
        last = text;
        if (terminal)
        {
          writer.Write("\r" + text.PadRight(Math.Max(text.Length, 60)));
          lineOpen = true;
          return;
        }
        if (percent >= 0)
        {
          int tenth = Math.Min(percent, 100) / 10;
          if (tenth == lastTenth) return;
          lastTenth = tenth;
        }
        writer.WriteLine("  " + text);
      }
    }

    /// <summary>Clear the terminal's progress line, so what follows starts on a clean one.</summary>
    public void Done()
    {
      lock (gate) ClearLine();
    }

    private void ClearLine()
    {
      if (!lineOpen) return;
      writer.Write("\r" + new string(' ', Math.Max(last.Length, 60)) + "\r");
      lineOpen = false;
    }
  }
}
