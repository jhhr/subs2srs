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
using System.Diagnostics;
using System.IO;
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
  ///  - exit 0 = a file was saved, 2 = nothing saved, 1 = error on stderr.
  /// </summary>
  public static class SubsRetimerLauncher
  {
    public const int ExitSaved = 0;
    public const int ExitError = 1;
    public const int ExitNothingSaved = 2;

    /// <summary>What to run. Encodings are subs2srs short names (e.g. "utf-8", "shift_jis").</summary>
    public sealed record Request(
      string ReferencePath,
      string TargetPath,
      string ReferenceEncoding,
      string TargetEncoding,
      bool Auto,
      string? OutputPath = null);

    /// <summary>Outcome of a run. <see cref="SavedPath"/> is set only when a file was saved.</summary>
    public sealed record Result(int ExitCode, string? SavedPath, string StdErr)
    {
      public bool Saved => ExitCode == ExitSaved && SavedPath != null;
      public bool NothingSaved => ExitCode == ExitNothingSaved || (ExitCode == ExitSaved && SavedPath == null);
      public bool Failed => !Saved && !NothingSaved;
    }

    /// <summary>True when the executable was found on PATH at startup.</summary>
    public static bool IsAvailable => File.Exists(ConstantSettings.PathSubsRetimerExeFull);

    /// <summary>Argument list for <c>subsretimer</c>. Paths go after <c>--</c> so odd names cannot be read as options.</summary>
    public static List<string> BuildArguments(Request r)
    {
      var args = new List<string> { "--print-output" };
      if (r.Auto) args.Add("--auto");
      if (!string.IsNullOrEmpty(r.OutputPath)) { args.Add("--output"); args.Add(r.OutputPath); }
      args.Add("--ref-encoding"); args.Add(string.IsNullOrEmpty(r.ReferenceEncoding) ? "utf-8" : r.ReferenceEncoding);
      args.Add("--target-encoding"); args.Add(string.IsNullOrEmpty(r.TargetEncoding) ? "utf-8" : r.TargetEncoding);
      args.Add("--");
      args.Add(r.ReferencePath);
      args.Add(r.TargetPath);
      return args;
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

    /// <summary>Run a specific <c>subsretimer</c> executable. Never throws for tool failures; see <see cref="Result.Failed"/>.</summary>
    public static async Task<Result> RunAsync(string exePath, Request request, CancellationToken token = default)
    {
      var psi = new ProcessStartInfo(exePath)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
      };
      foreach (string a in BuildArguments(request)) psi.ArgumentList.Add(a);

      Logger.Instance.info($"Running {exePath} {string.Join(" ", psi.ArgumentList)}");

      try
      {
        using var process = Process.Start(psi);
        if (process == null) return new Result(ExitError, null, $"Could not start {exePath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        var result = ParseResult(process.ExitCode, stdout, stderr);
        Logger.Instance.info($"subsretimer exited {result.ExitCode}, saved: {result.SavedPath ?? "(none)"}");
        return result;
      }
      catch (OperationCanceledException)
      {
        return new Result(ExitNothingSaved, null, "Cancelled");
      }
      catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
      {
        return new Result(ExitError, null, $"Could not run {exePath}: {ex.Message}");
      }
    }
  }
}
