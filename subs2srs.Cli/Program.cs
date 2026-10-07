using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Cli
{
  /// <summary>
  /// Entry point: set up the standard streams and Ctrl+C, then hand over to
  /// <see cref="CliRunner.RunAsync"/>.
  /// </summary>
  public static class Program
  {
    public static async Task<int> Main(string[] args)
    {
      // A redirected stream is read by a program (a script, a log), so file names, Japanese
      // or not, must not depend on how the tool was started: write UTF-8 without a byte order
      // mark. .NET would otherwise use the console's code page on Windows, or the charset of
      // LC_ALL, LC_MESSAGES or LANG elsewhere. Through SetOut and SetError, so anything that
      // writes to Console directly (UtilsMsg, the log echo) writes UTF-8 too.
      if (Console.IsOutputRedirected) Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
      if (Console.IsErrorRedirected) Console.SetError(Utf8Writer(Console.OpenStandardError()));

      // A stream that is a console shows Japanese names only in a UTF-8 code page. Setting
      // OutputEncoding changes the code page of the console this process shares with its
      // parent, so the old one is put back on the way out.
      Encoding? consoleEncoding = null;
      if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
      {
        UtilsCommon.RegisterEncodings(); // the console's own code page may be a legacy one
        consoleEncoding = Console.OutputEncoding;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
      }

      using var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (sender, e) =>
      {
        e.Cancel = true;
        Console.Error.WriteLine("cancelling...");
        cts.Cancel();
      };

      try
      {
        return await CliRunner.RunAsync(args, Console.Out, Console.Error, cts.Token);
      }
      finally
      {
        if (consoleEncoding != null)
        {
          try { Console.OutputEncoding = consoleEncoding; }
          catch (IOException) { }
        }
      }
    }

    /// <summary>
    /// The writer for a redirected standard stream: UTF-8 without a byte order mark (a reader
    /// would take one as part of the first line), flushed on every write, since nothing
    /// flushes it when the process exits.
    /// </summary>
    internal static TextWriter Utf8Writer(Stream stream) =>
      new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
  }
}
