//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs
{
  /// <summary>
  /// Runs the external <c>subsretimer</c> tool (https://github.com/jhhr/subsretimer)
  /// and reads its result back.
  ///
  /// Contract relied upon:
  ///  - with <c>--print-output</c>, stdout carries only saved paths, one per line;
  ///  - exit 0 = a file was saved, 2 = nothing saved, 1 = error on stderr;
  ///  - redirected stdout and stderr are UTF-8 without a byte-order mark;
  ///  - with <c>--auto</c>, <c>--report</c> leaves a JSON report of a run that exits 0 or 2
  ///    (<see cref="RetimeReport"/>).
  /// </summary>
  public static class SubsRetimerLauncher
  {
    public const int ExitSaved = 0;
    public const int ExitError = 1;
    public const int ExitNothingSaved = 2;

    /// <summary>
    /// What to run. Encodings are subs2srs short names (e.g. "utf-8", "shift_jis").
    /// <see cref="MinMatch"/> (a share from 0 to 1) and <see cref="ReportPath"/> are passed only
    /// when set; subsretimer refuses both without <see cref="Auto"/>.
    /// </summary>
    public sealed record Request(
      string ReferencePath,
      string TargetPath,
      string ReferenceEncoding,
      string TargetEncoding,
      bool Auto,
      string? OutputPath = null,
      double? MinMatch = null,
      string? ReportPath = null);

    /// <summary>Outcome of a run. <see cref="SavedPath"/> is set only when a file was saved.</summary>
    public sealed record Result(int ExitCode, string? SavedPath, string StdErr)
    {
      public bool Saved => ExitCode == ExitSaved && SavedPath != null;
      public bool NothingSaved => ExitCode == ExitNothingSaved || (ExitCode == ExitSaved && SavedPath == null);
      public bool Failed => !Saved && !NothingSaved;
    }

    /// <summary>True when the executable is found in the Tools Directory or on PATH, looked up on every call.</summary>
    public static bool IsAvailable => File.Exists(ConstantSettings.PathSubsRetimerExeFull);

    /// <summary>Test hook: replaces the process run (gets the start info, returns what the tool would have).</summary>
    internal static Func<ProcessStartInfo, CancellationToken, Task<CliProcessResult>>? RunnerOverride { get; set; }

    /// <summary>Argument list for <c>subsretimer</c>. Paths go after <c>--</c> so odd names cannot be read as options.</summary>
    public static List<string> BuildArguments(Request r)
    {
      var args = new List<string> { "--print-output" };
      if (r.Auto) args.Add("--auto");
      if (!string.IsNullOrEmpty(r.OutputPath)) { args.Add("--output"); args.Add(r.OutputPath); }
      if (r.MinMatch is double minMatch)
      {
        // subsretimer reads it in the invariant culture and refuses "0,85".
        args.Add("--min-match"); args.Add(minMatch.ToString(CultureInfo.InvariantCulture));
      }
      if (!string.IsNullOrEmpty(r.ReportPath)) { args.Add("--report"); args.Add(r.ReportPath); }
      args.Add("--ref-encoding"); args.Add(EncodingOrUtf8(r.ReferenceEncoding));
      args.Add("--target-encoding"); args.Add(EncodingOrUtf8(r.TargetEncoding));
      args.Add("--");
      args.Add(r.ReferencePath);
      args.Add(r.TargetPath);
      return args;
    }

    /// <summary>
    /// The start info: <see cref="UtilsCommon.makeToolStartInfo"/> (UTF-8 pipes, which the tool
    /// writes when redirected; no window) and the arguments one by one.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string exePath, Request request)
    {
      ProcessStartInfo psi = UtilsCommon.makeToolStartInfo(exePath, "", redirectStdout: true, redirectStderr: true);
      foreach (string a in BuildArguments(request)) psi.ArgumentList.Add(a);
      return psi;
    }

    /// <summary>Interpret exit code and captured output. The last non-empty stdout line is the saved path.</summary>
    public static Result ParseResult(int exitCode, string stdout, string stderr)
    {
      string? saved = null;
      if (exitCode == ExitSaved)
      {
        foreach (string line in (stdout ?? "").Split('\n'))
        {
          string t = line.Trim();
          if (t.Length > 0) saved = t;
        }
      }
      return new Result(exitCode, saved, (stderr ?? "").Trim());
    }

    /// <summary>Run <c>subsretimer</c> from PATH.</summary>
    public static Task<Result> RunAsync(Request request, CancellationToken token = default) =>
      RunAsync(ConstantSettings.PathSubsRetimerExeFull, request, token);

    /// <summary>
    /// Run a specific <c>subsretimer</c> executable. Never throws for tool failures; see
    /// <see cref="Result.Failed"/>. A cancel kills the tool, waits for it to exit and throws
    /// <see cref="OperationCanceledException"/>, so it cannot be mistaken for exit 2.
    /// </summary>
    public static async Task<Result> RunAsync(string exePath, Request request, CancellationToken token = default)
    {
      token.ThrowIfCancellationRequested();
      ProcessStartInfo psi = StartInfo(exePath, request);

      Logger.Instance.info($"Running {exePath} {string.Join(" ", psi.ArgumentList)}");

      CliProcessResult run;
      try
      {
        run = await (RunnerOverride ?? UtilsCommon.RunToolAsync)(psi, token).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
      {
        return new Result(ExitError, null, $"Could not run {exePath}: {ex.Message}");
      }
      if (run.ExitCode == null)
        return new Result(ExitError, null, "subsretimer did not finish");

      var result = ParseResult(run.ExitCode.Value, run.Stdout, run.Stderr);
      Logger.Instance.info($"subsretimer exited {result.ExitCode}, saved: {result.SavedPath ?? "(none)"}");
      return result;
    }

    /// <summary>
    /// The command that opens subsretimer's editor on the pair of <paramref name="request"/>,
    /// for the user to type: no <c>--auto</c>, <c>--min-match</c>, <c>--report</c> or
    /// <c>--print-output</c>; <c>--target-encoding</c>, <c>--ref-encoding</c> unless it is UTF-8,
    /// and <c>--output</c>, where the editor's Save writes. Quoted for PowerShell on Windows and
    /// a POSIX shell elsewhere. Paths are printed as given, without <c>--</c>: pass full paths,
    /// so the command works from any folder and no path can be read as an option.
    /// </summary>
    public static string EditorCommand(string exePath, Request request) =>
      EditorCommand(exePath, request, OperatingSystem.IsWindows());

    /// <summary><see cref="EditorCommand(string, Request)"/> for the given platform's shell.</summary>
    internal static string EditorCommand(string exePath, Request request, bool windows)
    {
      var args = new List<string>();
      string refEncoding = EncodingOrUtf8(request.ReferenceEncoding);
      if (!refEncoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
      {
        args.Add("--ref-encoding"); args.Add(refEncoding);
      }
      args.Add("--target-encoding"); args.Add(EncodingOrUtf8(request.TargetEncoding));
      if (!string.IsNullOrEmpty(request.OutputPath)) { args.Add("--output"); args.Add(request.OutputPath); }
      args.Add(request.ReferencePath);
      args.Add(request.TargetPath);

      Func<string, string> quote = windows ? QuotePowerShell : QuotePosix;
      string exe = quote(exePath);
      // PowerShell reads a quoted string at the start of a line as a value: '&' runs it.
      if (windows && exe != exePath) exe = "& " + exe;
      return exe + " " + string.Join(" ", args.Select(quote));
    }

    private static string EncodingOrUtf8(string encoding) => string.IsNullOrEmpty(encoding) ? "utf-8" : encoding;

    /// <summary>
    /// Whether <paramref name="arg"/> means the same unquoted in the shell: ASCII letters and
    /// digits and a few marks that neither shell treats specially in the middle of a word or at
    /// its start; '\' only in PowerShell, where it is not an escape.
    /// </summary>
    private static bool IsPlain(string arg, bool windows) =>
      arg.Length > 0 && arg.All(c => c < 128
        && (char.IsLetterOrDigit(c) || "-_./:=+".IndexOf(c) >= 0 || (windows && c == '\\')));

    /// <summary>A POSIX single-quoted word; a quote inside becomes <c>'\''</c>.</summary>
    private static string QuotePosix(string arg) =>
      IsPlain(arg, windows: false) ? arg : "'" + arg.Replace("'", "'\\''") + "'";

    /// <summary>
    /// A PowerShell double-quoted string, which cmd reads the same way for a plain path. A
    /// backtick escapes what would end or expand inside it: the backtick, '$', and the four
    /// characters PowerShell reads as a double quote (a Windows file name cannot hold '"', but
    /// can hold the typographic ones).
    /// </summary>
    private static string QuotePowerShell(string arg)
    {
      if (IsPlain(arg, windows: true)) return arg;
      var sb = new StringBuilder("\"");
      foreach (char c in arg)
      {
        if (c is '`' or '$' or '"' or '“' or '”' or '„') sb.Append('`');
        sb.Append(c);
      }
      return sb.Append('"').ToString();
    }
  }
}
