using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Cli
{
  /// <summary>One file found by its name (full path), or why there is none: no file, or several, named.</summary>
  internal sealed record FoundFile(string? Path, string? Problem)
  {
    public static FoundFile Of(string path) => new(path, null);
    public static FoundFile Missing(string problem) => new(null, problem);
  }

  internal enum RetimeKind { Kept, Retimed, BelowMinMatch, NoTimedLines, NotSaved, Failed, NoJpFile, NoEnFile }

  /// <summary>
  /// What the retime stage made of one episode: the table's Retime column, the retimed JP file
  /// (<see cref="OutputPath"/>, set when <see cref="Ready"/>), subsretimer's report, and for a
  /// pair it did not save (exit 2) the command that opens its editor on that pair (design 9).
  /// </summary>
  internal sealed record RetimeOutcome(RetimeKind Kind, string? OutputPath = null, RetimeReport? Report = null,
    string? Reason = null, string? EditorCommand = null)
  {
    /// <summary>
    /// The episode has a <c>.ja</c> file <c>go</c> should use, so it can get cards. Otherwise go
    /// should leave it out: it has none, or, with no JP or no EN file found, one an earlier run
    /// or the editor left, which a lookup problem never deletes.
    /// </summary>
    public bool Ready => Kind is RetimeKind.Kept or RetimeKind.Retimed;

    public string Column => Kind switch
    {
      RetimeKind.Kept => "kept",
      RetimeKind.Retimed => Report == null ? "retimed"
        : Segments(Report.Segments) + (Report.ReferenceCoverage is double share ? ", " + Percent(share) + " of EN covered" : ""),
      RetimeKind.BelowMinMatch => "below --min-match" + (Report?.ReferenceCoverage is double share ? " (" + Percent(share) + ")" : ""),
      RetimeKind.NoTimedLines => "no timed lines",
      RetimeKind.NotSaved => "not saved",
      RetimeKind.Failed => "failed: " + Reason,
      _ => Reason ?? "",
    };

    /// <summary>The report's segments, the runs of target lines moved together, as subsretimer counts them.</summary>
    private static string Segments(int segments) =>
      segments.ToString(CultureInfo.InvariantCulture) + (segments == 1 ? " segment" : " segments");

    /// <summary>A share as a whole percentage, rounded down as subsretimer prints it (79.9% is "79%", below 80).</summary>
    internal static string Percent(double share) =>
      ((int)Math.Floor(share * 100 + 1e-9)).ToString(CultureInfo.InvariantCulture) + "%";
  }

  /// <summary>
  /// The season-wide inputs of the retime stage.
  /// </summary>
  /// <param name="Exe">subsretimer's full path (<see cref="RetimeStage.ResolveExe"/>); null when it was not found.</param>
  /// <param name="TargetEncoding">The JP files' encoding, a short name: the project's Subs1 encoding, which the retimed file keeps.</param>
  /// <param name="MinMatch">subsretimer's <c>--min-match</c>; null passes none.</param>
  /// <param name="Force">Retime again even where a retime is newer than both its files (an editor fix included).</param>
  internal sealed record RetimeOptions(string? Exe, string TargetEncoding, double? MinMatch = null, bool Force = false)
  {
    /// <summary>The options for a loaded project: subsretimer looked up as every tool is, the Subs1 encoding.</summary>
    public static RetimeOptions FromSettings(Settings settings, double? minMatch, bool force) =>
      new(RetimeStage.ResolveExe(), settings.Subs[0].Encoding, minMatch, force);
  }

  /// <summary>
  /// The season's retime stage (season plan C, design 7 and 9): each video's JP file found by
  /// name (<see cref="FindJpFiles"/>), retimed with subsretimer to the video's EN extract
  /// (<see cref="RetimeAsync"/>) into <c>s2s/&lt;video name&gt;.ja.&lt;ext&gt;</c>, the Subs1
  /// file <c>go --season</c> takes. A retime newer than both its files is kept, so a fix saved
  /// from the editor survives later runs; any other is done again. After the stage an episode
  /// has a <c>.ja</c> file go should use exactly when its outcome is <see cref="RetimeOutcome.Ready"/>.
  /// </summary>
  internal static class RetimeStage
  {
    /// <summary>Tags that mark a subtitle file beside the video as English, so not its JP file.</summary>
    public static readonly string[] EnglishTags = { "en", "eng" };

    /// <summary>subsretimer's <c>--report</c> beside the retime: <c>s2s/&lt;video name&gt;.retime.json</c>.</summary>
    public const string ReportExtension = ".retime.json";

    public const string NoJpFileReason = "no JP file named like the video";
    public const string NoEnFileReason = "no EN file";
    public const string NotFoundMessage = "subsretimer not found: put its folder on PATH or in Tools Directory (Preferences)";

    // ── finding the files ──────────────────────────────────────────────

    /// <summary>Each video's JP file (<see cref="FindJpFiles(string, IReadOnlyList{string}, IReadOnlyCollection{string})"/>), reading the season folder once.</summary>
    public static FoundFile[] FindJpFiles(string seasonDir, IReadOnlyList<string> videos)
    {
      string dir = Path.GetFullPath(seasonDir);
      return FindJpFiles(dir, videos, UtilsCommon.getNonHiddenFilesInDir(dir).Select(f => Path.GetFileName(f)).ToArray());
    }

    /// <summary>
    /// Each video's JP file (season plan C1), from the season folder's file names, without
    /// touching the disk: the one name that <see cref="IsJpFileOf"/> the video. A name that is
    /// also a longer video's JP file is that video's (<c>Movie.Extended.srt</c> is not
    /// <c>Movie</c>'s, tagged <c>Extended</c>). None or several: the problem, naming them.
    /// </summary>
    public static FoundFile[] FindJpFiles(string seasonDir, IReadOnlyList<string> videos, IReadOnlyCollection<string> names)
    {
      string[] videoNames = videos.Select(v => Path.GetFileNameWithoutExtension(v)).ToArray();
      return videoNames.Select(video =>
      {
        List<string> found = names.Where(n => IsJpFileOf(n, video)
          && !videoNames.Any(other => other.Length > video.Length && IsJpFileOf(n, other))).ToList();
        return found.Count switch
        {
          1 => FoundFile.Of(Path.Combine(seasonDir, found[0])),
          0 => FoundFile.Missing(NoJpFileReason),
          _ => FoundFile.Missing(found.Count.ToString(CultureInfo.InvariantCulture) + " JP files (" + string.Join(", ", found) + ")"),
        };
      }).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is named like a JP file of the video named
    /// <paramref name="videoName"/>: a subtitle file (<see cref="EpisodeList.SubsExtensions"/>)
    /// named <c>&lt;video name&gt;.&lt;ext&gt;</c> or <c>&lt;video name&gt;.&lt;tag&gt;.&lt;ext&gt;</c>,
    /// the tag one or more non-empty words split by dots (<c>ja</c>, <c>ja.cc</c>,
    /// <c>jpn.sdh</c>), none of them English (<see cref="EnglishTags"/>: <c>en.sdh</c> and
    /// <c>cc.en</c> are English files). Compared as names, ignoring case, never as a wildcard
    /// pattern: video names hold dots and brackets, and <c>Ep 1</c> is the start of <c>Ep 10</c>.
    /// </summary>
    internal static bool IsJpFileOf(string fileName, string videoName)
    {
      string ext = Path.GetExtension(fileName);
      if (!EpisodeList.SubsExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
      string stem = fileName.Substring(0, fileName.Length - ext.Length);
      if (stem.Equals(videoName, StringComparison.OrdinalIgnoreCase)) return true;
      if (!stem.StartsWith(videoName + ".", StringComparison.OrdinalIgnoreCase)) return false;
      string[] words = stem.Substring(videoName.Length + 1).Split('.');
      return words.All(w => w.Length > 0) && !words.Any(w => EnglishTags.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Each video's EN extract (<see cref="FindEnFiles(string, IReadOnlyList{string}, IReadOnlyCollection{string})"/>), reading the season's <c>s2s</c> folder once.</summary>
    public static FoundFile[] FindEnFiles(string seasonDir, IReadOnlyList<string> videos)
    {
      string dir = Path.GetFullPath(seasonDir);
      string[] names = UtilsCommon.getNonHiddenFilesInDir(Path.Combine(dir, EpisodeList.SubsFolder))
        .Select(f => Path.GetFileName(f)).ToArray();
      return FindEnFiles(dir, videos, names);
    }

    /// <summary>
    /// Each video's EN file in <c>s2s</c>, without extracting (for a retime on its own): the one
    /// <c>&lt;video name&gt;.en.&lt;ext&gt;</c> among <paramref name="s2sNames"/>, as
    /// <c>go --season</c> finds its Subs2; none or several give go's own reason.
    /// </summary>
    public static FoundFile[] FindEnFiles(string seasonDir, IReadOnlyList<string> videos, IReadOnlyCollection<string> s2sNames)
      => videos.Select(v =>
      {
        List<string> found = EpisodeList.Named(s2sNames, Path.GetFileNameWithoutExtension(v), EpisodeList.Subs2Tag);
        return found.Count == 1
          ? FoundFile.Of(Path.Combine(seasonDir, EpisodeList.SubsFolder, found[0]))
          : FoundFile.Missing(EpisodeList.Problem(found, EpisodeList.Subs2Tag)!);
      }).ToArray();

    /// <summary>The retime of <paramref name="video"/>: <c>s2s/&lt;video name&gt;.ja.&lt;the JP file's extension&gt;</c>, the extension in lower case.</summary>
    public static string OutputPath(string seasonDir, string video, string jpFile) =>
      Path.Combine(seasonDir, EpisodeList.SubsFolder,
        Path.GetFileNameWithoutExtension(video) + EpisodeList.Subs1Tag + Path.GetExtension(jpFile).ToLowerInvariant());

    public static string ReportPath(string seasonDir, string video) =>
      Path.Combine(seasonDir, EpisodeList.SubsFolder, Path.GetFileNameWithoutExtension(video) + ReportExtension);

    // ── subsretimer ────────────────────────────────────────────────────

    /// <summary>subsretimer's full path (Tools Directory, then PATH), or null.</summary>
    public static string? ResolveExe() => ConstantSettings.ResolveTool(ConstantSettings.SubsRetimerExe);

    /// <summary>
    /// How the editor command names <paramref name="exe"/>: <c>subsretimer</c> when PATH finds
    /// that same file, so the line stays short; else its full path.
    /// </summary>
    public static string CommandName(string exe) =>
      CommandName(exe, ConstantSettings.FindInPath(ConstantSettings.SubsRetimerExe));

    /// <summary><see cref="CommandName(string)"/> with PATH's answer given.</summary>
    internal static string CommandName(string exe, string? onPath) =>
      onPath != null && string.Equals(Path.GetFullPath(onPath), Path.GetFullPath(exe), PathComparison)
        ? ConstantSettings.SubsRetimerExe : exe;

    /// <summary>How the file system compares names: two names that differ in case are one file on Windows.</summary>
    internal static StringComparison PathComparison =>
      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Retime one episode (season plan C2). With no JP or no EN file nothing is touched: a lookup
    /// problem (a second JP candidate, a renamed file) must not destroy an editor fix, so a
    /// <c>.ja</c> file may stay, and the caller leaves an episode that is not
    /// <see cref="RetimeOutcome.Ready"/> out of <c>go</c>. When a retime runs and saves nothing,
    /// fails or is cancelled, the episode's <c>.ja</c> files are gone afterwards.
    /// A retime newer than both its EN and JP file is kept (unless <see cref="RetimeOptions.Force"/>),
    /// without subsretimer, and any other <c>.ja</c> file of the episode deleted (go skips an
    /// episode with two). Otherwise the episode's <c>.ja</c> files and report are deleted and
    /// subsretimer runs: <c>--auto</c>, <c>--output</c>, <c>--min-match</c> when set,
    /// <c>--report</c>, the EN file as reference in UTF-8 (what mkvextract writes), the JP file
    /// as target in <see cref="RetimeOptions.TargetEncoding"/>. Exit 2 gives the editor command;
    /// exit 1 (or subsretimer not found) its message and no command. Only a cancel throws, after
    /// deleting what the killed run may have written.
    /// </summary>
    public static async Task<RetimeOutcome> RetimeAsync(string seasonDir, string video, FoundFile jp, FoundFile en,
      RetimeOptions options, CancellationToken ct = default)
    {
      ct.ThrowIfCancellationRequested();
      if (jp.Path == null)
        return new RetimeOutcome(RetimeKind.NoJpFile, Reason: jp.Problem ?? NoJpFileReason);
      if (en.Path == null)
        return new RetimeOutcome(RetimeKind.NoEnFile, Reason: en.Problem ?? NoEnFileReason);

      string dir = Path.GetFullPath(seasonDir);
      string s2s = Path.Combine(dir, EpisodeList.SubsFolder);
      // Every .ja file go would take as this episode's Subs1.
      List<string> retimes = Directory.Exists(s2s)
        ? EpisodeList.Named(Directory.GetFiles(s2s).Select(f => Path.GetFileName(f)).ToList(),
            Path.GetFileNameWithoutExtension(video), EpisodeList.Subs1Tag).Select(n => Path.Combine(s2s, n)).ToList()
        : new List<string>();

      string jpFile = Path.GetFullPath(jp.Path);
      string enFile = Path.GetFullPath(en.Path);
      string output = OutputPath(dir, video, jpFile);
      string report = ReportPath(dir, video);
      if (WouldKeep(dir, video, jpFile, enFile, options.Force))
      {
        string? error = Delete(retimes.Where(r => !string.Equals(r, output, PathComparison)));
        return error != null ? Failed(error) : new RetimeOutcome(RetimeKind.Kept, output);
      }

      string? cleanup = Delete(retimes.Append(output).Append(report));
      if (cleanup != null) return Failed(cleanup);
      if (options.Exe == null) return Failed(NotFoundMessage);

      // Full paths: the editor command has no "--", and works from any folder.
      var request = new SubsRetimerLauncher.Request(enFile, jpFile, "utf-8", options.TargetEncoding, Auto: true,
        OutputPath: output, MinMatch: options.MinMatch, ReportPath: report);
      SubsRetimerLauncher.Result result;
      try
      {
        result = await SubsRetimerLauncher.RunAsync(options.Exe, request, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // A file the killed run left would look like a finished retime to the next run.
        Delete(new[] { output, report });
        throw;
      }

      if (result.Saved)
        return new RetimeOutcome(RetimeKind.Retimed, output, RetimeReport.Read(report));
      // Only a saved retime may stay for go (an exit 1 can follow the save: a report it could not write).
      string? leftOver = Delete(new[] { output });
      if (leftOver != null) return Failed(leftOver);
      if (result.NothingSaved)
      {
        RetimeReport? read = RetimeReport.Read(report);
        RetimeKind kind = read?.Reason switch
        {
          RetimeReport.BelowMinMatch => RetimeKind.BelowMinMatch,
          RetimeReport.NoTimedLines => RetimeKind.NoTimedLines,
          _ => RetimeKind.NotSaved,
        };
        return new RetimeOutcome(kind, Report: read,
          EditorCommand: SubsRetimerLauncher.EditorCommand(CommandName(options.Exe), request));
      }
      return Failed(ToolMessage(result));
    }

    private static RetimeOutcome Failed(string reason) => new(RetimeKind.Failed, Reason: reason);

    /// <summary>
    /// Whether <see cref="RetimeAsync"/> would keep the episode's retime as it is: not forced, and
    /// the output there, not empty, and written after both its files. Reads only; a dry run asks it.
    /// </summary>
    internal static bool WouldKeep(string seasonDir, string video, string jpFile, string enFile, bool force)
    {
      if (force) return false;
      var info = new FileInfo(OutputPath(Path.GetFullPath(seasonDir), video, jpFile));
      if (info is not { Exists: true, Length: > 0 } || !File.Exists(enFile) || !File.Exists(jpFile)) return false;
      DateTime written = info.LastWriteTimeUtc;
      return written > File.GetLastWriteTimeUtc(enFile) && written > File.GetLastWriteTimeUtc(jpFile);
    }

    /// <summary>Deletes the files that are there; the first failure's message, else null.</summary>
    internal static string? Delete(IEnumerable<string> paths)
    {
      foreach (string path in paths)
      {
        try
        {
          if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
          return "cannot delete " + path + ": " + ex.Message;
        }
      }
      return null;
    }

    /// <summary>
    /// subsretimer's message for a failure: its first <c>subsretimer: </c> line without that
    /// word (a usage text may follow it), else its first stderr line; the exit code unless it is 1.
    /// </summary>
    private static string ToolMessage(SubsRetimerLauncher.Result result)
    {
      const string prefix = "subsretimer: ";
      string[] lines = result.StdErr.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
      string? own = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
      string? message = own != null ? own.Substring(prefix.Length) : lines.FirstOrDefault();
      if (result.ExitCode == SubsRetimerLauncher.ExitError && message != null) return message;
      string exit = "subsretimer exited with code " + result.ExitCode.ToString(CultureInfo.InvariantCulture);
      return message != null ? exit + ": " + message : exit;
    }
  }
}
