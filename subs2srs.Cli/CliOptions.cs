using System;
using System.Collections.Generic;
using System.Globalization;

namespace subs2srs.Cli
{
  /// <summary>A usage error or an error before any work; <see cref="ExitCode"/> becomes the process exit code.</summary>
  internal sealed class CliException : Exception
  {
    public int ExitCode { get; }
    public CliException(string message, int exitCode = CliOptions.ExitError) : base(message) { ExitCode = exitCode; }
  }

  /// <summary>The stage <c>season --only</c> runs on its own.</summary>
  internal enum SeasonStage { Extract, Retime, Go }


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

    public const string GoCommand = "go";
    public const string SeasonCommand = "season";

    public string? Command { get; set; }
    public string ProjectPath { get; set; } = "";
    /// <summary><c>go --season</c>, or <c>season</c>'s folder; null = the project's own file patterns (go).</summary>
    public string? SeasonDir { get; set; }
    /// <summary><c>season --track</c>: the track to extract in every episode; null = the pick.</summary>
    public int? Track { get; set; }
    /// <summary><c>season --min-match</c>, 0 to 1, passed to subsretimer; null = none passed.</summary>
    public double? MinMatch { get; set; }
    /// <summary><c>season --force</c>: extract and retime again what would be kept.</summary>
    public bool Force { get; set; }
    /// <summary><c>season --only</c>; null = every stage.</summary>
    public SeasonStage? Only { get; set; }
    public bool DryRun { get; set; }
    /// <summary><c>--grouping</c>: the snippet mode for this run instead of the project's; null = the project's.</summary>
    public SnippetMode? Grouping { get; set; }
    /// <summary><c>--deck</c>: the deck name for this run instead of the project's; null = the project's.</summary>
    public string? Deck { get; set; }
    public bool Yes { get; set; }
    public bool Verbose { get; set; }
    public bool NoPrefs { get; set; }
    public string? PrefsPath { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }

    public const string Usage = @"subs2srs-cli - make Anki cards from a subs2srs project without the GUI

usage: subs2srs-cli go --project <file> [--season <dir>] [--dry-run] [options]
       subs2srs-cli season <dir> --project <file> [--track <id>] [--min-match <f>]
                    [--force] [--only extract|retime|go] [--dry-run] [options]
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
  --deck <name>     the deck name instead of the project's: it names the import file
                    (<name>.tsv), the media folder (<name>.media) and the media, and
                    starts each card's tag. The output directory stays the project's.
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
to a warning) or a step of the run failed, 130 cancelled.

season makes the cards of a season folder in one command: every *.mkv in <dir>, in
go's order, without those after the project's Episode End #. It prepares each
episode's subtitles, one episode after another, then makes the cards as go --season
does.

  1. The EN track: of the English ASS, SSA and SRT tracks that are not forced, the
     one with the most events (mkvmerge -J, from MKVToolNix). Image tracks (PGS,
     VobSub) cannot be used.
  2. Extracted with mkvextract to s2s/<video name>.en.<ext>. A file there already is
     kept. Any other s2s/<video name>.en.* is an extraction of another track, and is
     deleted.
  3. The JP file: the one .ass, .ssa or .srt in <dir> named <video name>.<ext> or
     <video name>.<tag>.<ext>, the tag not en or eng.
  4. Retimed to the EN file with subsretimer into s2s/<video name>.ja.<ext>, read in
     the project's Subs1 encoding (the retimed file keeps it). A retime newer than
     both its files is kept, so a fix saved from subsretimer's editor survives later
     runs; any other is done again.
  5. The cards of every episode with a retime (kept or new), as go --season makes
     them from s2s, the AI grouping first when the project groups by AI. The other
     episodes are skipped and keep their numbers. The EN files are read as UTF-8,
     as mkvextract writes them, whatever the project's Subs2 encoding (a warning
     says when it differs).

  --project <file>  the project file: Subs1 encoding, Episode Start # and End #,
                    and everything go takes from it
  --track <id>      extract this track (an id mkvmerge -i shows) in every episode
                    instead of the pick
  --min-match <f>   passed to subsretimer: save a retime only when it covers at
                    least this share (0 to 1) of the EN lines. There is no default:
                    without it, none is passed and every retime is saved.
  --force           extract and retime every episode again, a fix saved from the
                    editor included. After changing --track this is needed: an
                    earlier extraction of another track in the same format has the
                    same name and would be kept.
  --only <stage>    run one stage: extract; retime (the EN files already in s2s;
                    MKVToolNix is not needed); or go (the cards of what s2s holds,
                    as go --season makes them)
  --dry-run         extract, retime, delete and make nothing: print the EN track
                    each episode would use, its JP file, whether its EN file and its
                    retime are there and would be kept, and whether the AI grouping
                    of each episode with a retime kept is cached; then go's checks
  --grouping, --deck, --yes, --prefs, --no-prefs, --verbose as for go

season prints a table (Episode, EN track, Retime, AI, Status, Cards), a note when the
picked track differs between episodes, go's TSV line with the exit code, and for
each pair subsretimer did not save (below --min-match, no timed lines) the command
that opens its editor on that pair: its Save writes the file the next run keeps. A
JP file subsretimer cannot read gets no command; check the project's Subs1 encoding.

Exit codes as go's: 0 every episode done, 3 some episodes skipped (the table says
why), 1 an error before any work (usage, project, folder, no .mkv, MKVToolNix not
found), a failed check before the cards (the table is printed), or a step of the
run failed, 130 cancelled. With --only extract or retime: 0 every episode ready,
3 some not.";

    public static CliOptions Parse(string[] args)
    {
      var o = new CliOptions();
      int first = 0;
      if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
      {
        if (args[0] != GoCommand && args[0] != SeasonCommand) throw new CliException($"unknown command '{args[0]}'");
        o.Command = args[0];
        first = 1;
      }
      bool seasonOption = false; // --season, go's
      var seasonOptions = new List<string>(); // season's own, given
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
          case "--season": o.SeasonDir = Next(); seasonOption = true; break;
          case "--track": o.Track = ParseTrack(Next()); seasonOptions.Add(a); break;
          case "--min-match": o.MinMatch = ParseMinMatch(Next()); seasonOptions.Add(a); break;
          case "--force": o.Force = true; seasonOptions.Add(a); break;
          case "--only": o.Only = ParseOnly(Next()); seasonOptions.Add(a); break;
          case "--dry-run": o.DryRun = true; break;
          case "--grouping": o.Grouping = ParseGrouping(Next()); break;
          case "--deck": o.Deck = ParseDeck(Next()); break;
          case "--yes": o.Yes = true; break;
          case "--prefs": o.PrefsPath = Next(); break;
          case "--no-prefs": o.NoPrefs = true; break;
          case "--verbose": o.Verbose = true; break;
          case "--help": case "-h": case "-?": o.Help = true; return o;
          case "--version": o.Version = true; return o;
          default:
            // season's folder: its one argument that is not an option.
            if (o.Command == SeasonCommand && o.SeasonDir == null && !a.StartsWith("-", StringComparison.Ordinal))
            {
              o.SeasonDir = a;
              break;
            }
            throw new CliException($"unknown argument '{a}'");
        }
      }
      if (o.Command == null) throw new CliException("no command: the first argument is the command (go or season)");
      if (o.Command == SeasonCommand)
      {
        if (seasonOption) throw new CliException("season takes the season folder as its argument (season <dir>), not --season");
        if (string.IsNullOrWhiteSpace(o.SeasonDir)) throw new CliException("season needs the season folder: season <dir> --project <file>");
        if (o.Only is SeasonStage only)
        {
          string stage = "--only " + only.ToString().ToLowerInvariant();
          if (o.Track != null && only != SeasonStage.Extract)
            throw new CliException($"--track chooses the track to extract, and {stage} extracts nothing");
          if (o.MinMatch != null && only != SeasonStage.Retime)
            throw new CliException($"--min-match is passed to the retime, and {stage} does not retime");
          if (o.Force && only == SeasonStage.Go)
            throw new CliException("--force extracts and retimes again, and --only go does neither");
          if (only != SeasonStage.Go && (o.Grouping != null || o.Deck != null))
            throw new CliException($"{(o.Grouping != null ? "--grouping" : "--deck")} is for the cards, and {stage} makes none");
        }
      }
      else if (seasonOptions.Count > 0)
        throw new CliException($"{seasonOptions[0]} is an option of season, not go");
      if (string.IsNullOrWhiteSpace(o.ProjectPath)) throw new CliException($"{o.Command} needs --project <file>");
      if (o.SeasonDir != null && string.IsNullOrWhiteSpace(o.SeasonDir)) throw new CliException("--season needs a folder");
      if (o.NoPrefs && o.PrefsPath != null) throw new CliException("--prefs and --no-prefs cannot be combined");
      return o;
    }

    private static int ParseTrack(string value)
      => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
        ? id
        : throw new CliException($"--track takes a track id, a number as mkvmerge -i shows it; not '{value}'");

    /// <summary>A share from 0 to 1 with a decimal point, whatever the culture, as subsretimer reads it.</summary>
    private static double ParseMinMatch(string value)
      => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 && v <= 1
        ? v
        : throw new CliException($"--min-match takes a number from 0 to 1, like 0.85; not '{value}'");

    private static SeasonStage ParseOnly(string value) => value.ToLowerInvariant() switch
    {
      "extract" => SeasonStage.Extract,
      "retime" => SeasonStage.Retime,
      "go" => SeasonStage.Go,
      _ => throw new CliException($"--only takes extract, retime or go, not '{value}'"),
    };

    /// <summary>A deck name, as the GUI's field takes it (the settings trim it and turn spaces into '_').</summary>
    private static string ParseDeck(string value)
      => value.Trim().Length > 0 ? value : throw new CliException("--deck needs a name");

    private static SnippetMode ParseGrouping(string value) => value.ToLowerInvariant() switch
    {
      "rules" => SnippetMode.Rules,
      "off" => SnippetMode.Off,
      _ => throw new CliException($"--grouping takes rules or off, not '{value}'"),
    };
  }
}
