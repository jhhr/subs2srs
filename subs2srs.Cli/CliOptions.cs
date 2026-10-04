using System;

namespace subs2srs.Cli
{
  /// <summary>A usage error or an error before any work; <see cref="ExitCode"/> becomes the process exit code.</summary>
  internal sealed class CliException : Exception
  {
    public int ExitCode { get; }
    public CliException(string message, int exitCode = CliOptions.ExitError) : base(message) { ExitCode = exitCode; }
  }


  /// <summary>Command line of subs2srs-cli (see <see cref="Usage"/>).</summary>
  internal sealed class CliOptions
  {
    /// <summary>Every episode done (with <c>--dry-run</c>: resolved).</summary>
    public const int ExitOk = 0;
    /// <summary>A usage error, an error before any work, or a step of the run failed.</summary>
    public const int ExitError = 1;
    /// <summary>Some episodes were skipped; the table says why.</summary>
    public const int ExitSkipped = 3;
    public const int ExitCancelled = 130;

    public string? Command { get; set; }
    public string ProjectPath { get; set; } = "";
    /// <summary>Null = the project's own file patterns.</summary>
    public string? SeasonDir { get; set; }
    public bool DryRun { get; set; }
    /// <summary><c>--grouping</c>: the snippet mode for this run instead of the project's; null = the project's.</summary>
    public SnippetMode? Grouping { get; set; }
    public bool Yes { get; set; }
    public bool Verbose { get; set; }
    public bool NoPrefs { get; set; }
    public string? PrefsPath { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }

    public const string Usage = @"subs2srs-cli - make Anki cards from a subs2srs project without the GUI

usage: subs2srs-cli go --project <file> [--season <dir>] [--dry-run] [options]
       subs2srs-cli --help | --version

go makes the cards of a project saved from the GUI (.s2s.json), with its card fields,
encodings, audio, snapshots, deck name and output directory.

  --project <file>  the project file
  --season <dir>    take the episodes from a season folder instead of the project's
                    Subs1, Subs2 and video patterns: every *.mkv in <dir>, sorted as
                    the GUI sorts files and numbered from the project's Episode Start #,
                    with s2s/<video name>.ja.<ext> as Subs1 and s2s/<video name>.en.<ext>
                    as Subs2 (<ext> ass, ssa or srt; the tag in any case). An episode
                    without exactly one of each is skipped and keeps its number; videos
                    after the project's Episode End # are left out. Audio clips come
                    from the videos.
  --dry-run         print the episode list (with whether each episode's AI grouping
                    is cached) and the checks before starting, and stop
  --grouping <mode> group the lines into snippets by this mode instead of the
                    project's: rules or off, so a project that groups by AI runs
                    without the model.
  --yes             answer yes when asked to confirm (the answer is no otherwise),
                    as at a warning of the checks
  --prefs <file>    read this preferences JSON file instead of the user's preferences.json
  --no-prefs        do not read preferences.json
  --verbose         echo the application log to stderr
  --help            this text
  --version         the version

Without --season, the files of the project's patterns are paired by position, as in
the GUI, and counts that differ are refused. The checks before starting are the GUI's
Go's: the output directory can be written, a deck name, ffmpeg (and the encoder of
animated snapshots), claude when snippets are grouped by AI through it, and the same
audio stream in every video (a warning).

When the project groups snippets by AI, go first asks the model to group each ready
episode, as the GUI's Preview does; a cached answer is used without asking. An
episode the model cannot group is skipped, not grouped by the rules; one where only
some parts failed keeps the rules' grouping for those parts, with a warning. Once
the Claude usage limit is reached, every remaining episode without a cached answer
is skipped without asking; run go again after the limit resets, and it asks only
for those.

Then go makes the cards of every episode still in, in one run, into one import file
(TSV), and prints a table: each episode, its AI grouping (cached, grouped, k/n by
rules, usage limit, failed; - without AI), done, skipped and why, or failed, and its
cards; then the TSV's path. The table (with --dry-run, the episode list) goes to
stdout and everything else, the progress included, to stderr. Preferences are read,
never written.

Exit codes: 0 every episode done (with --dry-run: resolved), 3 some episodes skipped,
1 an error before any work (usage, project, folder, file counts, a failed check, no
to a warning) or a step of the run failed, 130 cancelled.";

    public static CliOptions Parse(string[] args)
    {
      var o = new CliOptions();
      int first = 0;
      if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
      {
        if (args[0] != "go") throw new CliException($"unknown command '{args[0]}'");
        o.Command = args[0];
        first = 1;
      }
      for (int i = first; i < args.Length; i++)
      {
        string a = args[i];
        string Next()
        {
          if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new CliException($"{a} needs a value");
          return args[++i];
        }
        switch (a)
        {
          case "--project": o.ProjectPath = Next(); break;
          case "--season": o.SeasonDir = Next(); break;
          case "--dry-run": o.DryRun = true; break;
          case "--grouping": o.Grouping = ParseGrouping(Next()); break;
          case "--yes": o.Yes = true; break;
          case "--prefs": o.PrefsPath = Next(); break;
          case "--no-prefs": o.NoPrefs = true; break;
          case "--verbose": o.Verbose = true; break;
          case "--help": case "-h": case "-?": o.Help = true; return o;
          case "--version": o.Version = true; return o;
          default: throw new CliException($"unknown argument '{a}'");
        }
      }
      if (o.Command == null) throw new CliException("no command: the first argument is the command (go)");
      if (string.IsNullOrWhiteSpace(o.ProjectPath)) throw new CliException("go needs --project <file>");
      if (o.SeasonDir != null && string.IsNullOrWhiteSpace(o.SeasonDir)) throw new CliException("--season needs a folder");
      if (o.NoPrefs && o.PrefsPath != null) throw new CliException("--prefs and --no-prefs cannot be combined");
      return o;
    }

    private static SnippetMode ParseGrouping(string value) => value.ToLowerInvariant() switch
    {
      "rules" => SnippetMode.Rules,
      "off" => SnippetMode.Off,
      _ => throw new CliException($"--grouping takes rules or off, not '{value}'"),
    };
  }
}
