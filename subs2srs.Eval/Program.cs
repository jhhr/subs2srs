using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Eval
{
  /// <summary>
  /// Entry point: parse the command line, load the user's preferences (API keys, pacing, cache
  /// directory; never written back), run and translate failures into exit codes.
  /// </summary>
  public static class Program
  {
    public static async Task<int> Main(string[] args)
    {
      EvalOptions options;
      try
      {
        options = EvalOptions.Parse(args);
      }
      catch (EvalException ex)
      {
        Console.Error.WriteLine("subs2srs.Eval: " + ex.Message);
        Console.Error.WriteLine("Run with --help for usage.");
        return ex.ExitCode;
      }
      if (options.Help)
      {
        Console.WriteLine(EvalOptions.Usage);
        return 0;
      }

      if (!options.NoPrefs)
      {
        if (options.PrefsPath != null)
        {
          if (!File.Exists(options.PrefsPath))
          {
            Console.Error.WriteLine("subs2srs.Eval: preferences file not found: " + options.PrefsPath);
            return EvalOptions.ExitUsage;
          }
          // PrefIO derives preferences.json from the directory of the legacy settings file name.
          ConstantSettings.SettingsFilename = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.PrefsPath)) ?? ".", "preferences.txt");
        }
        PrefIO.read();
      }
      // No log file from the console; the app log is echoed to stderr on --verbose instead.
      ConstantSettings.EnableLogging = false;
      if (options.Verbose) Logger.Instance.Echo = line => Console.Error.WriteLine(line);

      using var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (sender, e) =>
      {
        e.Cancel = true;
        Console.Error.WriteLine("cancelling...");
        cts.Cancel();
      };

      try
      {
        await new EvalRunner(options).RunAsync(cts.Token);
        return 0;
      }
      catch (EvalException ex)
      {
        Console.Error.WriteLine("subs2srs.Eval: " + ex.Message);
        return ex.ExitCode;
      }
      catch (OperationCanceledException)
      {
        Console.Error.WriteLine("subs2srs.Eval: cancelled; nothing was written.");
        return 130;
      }
    }
  }
}
