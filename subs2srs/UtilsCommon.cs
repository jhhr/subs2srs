//  Copyright (C) 2009-2016 Christopher Brochtrup
//  Copyright (C) 2026 fkzys and contributors
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  subs2srs is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with subs2srs.  If not, see <http://www.gnu.org/licenses/>.
//
//////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace subs2srs
{
  /// <summary>
  /// General utilities.
  /// </summary>
  public class UtilsCommon
  {
    /// <summary>
    /// Check if an integer is within its valid range. If it isn't, set to a default.
    /// </summary>
    public static int checkRange(int value, int min, int max, int def)
    {
      return (value >= min && value <= max) ? value : def;
    }


    /// <summary>
    /// If value is in set of valid values, use it. Otherwise, set to the default.
    /// </summary>
    public static T checkRangeInSet<T>(T value, List<T> validValues, T def)
    {
      return validValues.Contains(value) ? value : def;
    }


    private static bool _encodingsRegistered;

    /// <summary>
    /// Make legacy code pages (Shift-JIS, GBK, EUC-KR, Windows-125x …) available to
    /// Encoding.GetEncoding. .NET Core only ships Unicode/ASCII/Latin-1 by default.
    /// Idempotent; called from Program.Main and from the processing pipeline.
    /// </summary>
    public static void RegisterEncodings()
    {
      if (_encodingsRegistered) return;
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      _encodingsRegistered = true;
    }


    /// <summary>
    /// Get the directory that the executable resides in.
    /// </summary>
    public static string getAppDir(bool addSlash)
    {
      string appDir = AppContext.BaseDirectory;
      if (!addSlash)
        appDir = appDir.TrimEnd(Path.DirectorySeparatorChar);
      return appDir;
    }


    /// <summary>
    /// Get the multiple nearest to the provided value.
    /// </summary>
    public static int getNearestMultiple(int value, int multiple)
    {
      int remainder = value % multiple;

      if (remainder == 0)
        return value;
      if (remainder < multiple / 2)
        return value - remainder;
      return value + multiple - remainder;
    }


    /// <summary>
    /// Get a list of non-hidden files in a directory that match the given file pattern.
    /// </summary>
    public static string[] getNonHiddenFilesInDir(string dir, string searchPattern)
    {
      if (!Directory.Exists(dir))
        return Array.Empty<string>();

      List<string> files = Directory.GetFiles(dir, searchPattern, SearchOption.TopDirectoryOnly).ToList();
      files.Sort();
      return files.Where(f => (File.GetAttributes(f) & FileAttributes.Hidden) == 0).ToArray();
    }


    /// <summary>
    /// Get a list of non-hidden files in a directory.
    /// </summary>
    public static string[] getNonHiddenFilesInDir(string dir)
    {
      return getNonHiddenFilesInDir(dir, "*");
    }


    /// <summary>
    /// Get the non-hidden files based on the provided file pattern.
    /// File pattern can be the full path to a dir or it can be a dir + wildcard (D:\temp\*.mp3).
    /// </summary>
    public static string[] getNonHiddenFiles(string filePattern)
    {
      if (filePattern.Length == 0)
        return Array.Empty<string>();

      string dir = Path.GetDirectoryName(filePattern);

      if (!Directory.Exists(dir))
        return Array.Empty<string>();

      List<string> allFiles = Directory.GetFiles(Path.GetFullPath(dir), Path.GetFileName(filePattern)).ToList();
      allFiles.Sort();
      return allFiles.Where(f => (File.GetAttributes(f) & FileAttributes.Hidden) == 0).ToArray();
    }


    /// <summary>
    /// Return string containing each element of provided list separated by semicolons.
    /// </summary>
    public static string makeSemiString(string[] words)
    {
      return string.Join(";", words.Select(w => w.Trim()));
    }


    /// <summary>
    /// Trim spaces from words in provided list.
    /// </summary>
    public static string[] removeExtraSpaces(string[] words)
    {
      for (int i = 0; i < words.Length; i++)
        words[i] = words[i].Trim();
      return words;
    }


    // ── Exe resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Get the distinct list of exe paths to try, in order: the resolved full
    /// path (Tools Directory / PATH), the relative path, then the bare file name.
    /// Never mutates the process PATH.
    /// </summary>
    private static IEnumerable<string> getExePaths(string relPath, string fullPath)
    {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var candidate in new[] { fullPath, relPath, Path.GetFileName(fullPath) })
      {
        if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
          yield return candidate;
      }
    }

    /// <summary>
    /// Build a ProcessStartInfo for an external tool with UTF-8 output decoding
    /// and no console window. ffmpeg's output is UTF-8 regardless of the
    /// console code page, so the default OEM decoding mangles non-ASCII paths.
    /// </summary>
    internal static ProcessStartInfo makeToolStartInfo(string exe, string args,
      bool redirectStdout, bool redirectStderr)
    {
      var psi = new ProcessStartInfo
      {
        FileName = exe,
        Arguments = args,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = redirectStdout,
        RedirectStandardError = redirectStderr,
      };
      if (redirectStdout) psi.StandardOutputEncoding = new UTF8Encoding(false);
      if (redirectStderr) psi.StandardErrorEncoding = new UTF8Encoding(false);
      return psi;
    }

    private static IEnumerable<string> getFFmpegPaths()
    {
      return getExePaths(ConstantSettings.PathFFmpegExe, ConstantSettings.PathFFmpegFullExe);
    }


    // ── Simple process launching (blocking, no progress) ────────────────

    /// <summary>
    /// Try to call an exe with provided arguments. Returns true on success.
    /// </summary>
    private static string? callExe(string exe, string args, bool useShellExecute, bool createNoWindow)
    {
      try
      {
        using var process = new Process();
        if (useShellExecute)
        {
          process.StartInfo.FileName = exe;
          process.StartInfo.Arguments = args;
          process.StartInfo.UseShellExecute = true;
          process.StartInfo.CreateNoWindow = createNoWindow;
        }
        else
        {
          process.StartInfo = makeToolStartInfo(exe, args, redirectStdout: true, redirectStderr: true);
          process.StartInfo.CreateNoWindow = createNoWindow;
        }
        var stderr = new StringBuilder();
        if (!useShellExecute)
        {
          process.ErrorDataReceived += (s, e) =>
          {
            if (e.Data != null) stderr.AppendLine(e.Data);
          };
          process.OutputDataReceived += (s, e) => { };
        }
        process.Start();
        if (!useShellExecute)
        {
          process.BeginErrorReadLine();
          process.BeginOutputReadLine();
        }
        process.WaitForExit();
        if (!useShellExecute && process.ExitCode != 0)
        {
          string toolName = Path.GetFileNameWithoutExtension(exe);
          string full = stderr.ToString();
          if (full.Length > 0)
            Console.Error.WriteLine($"[{toolName} stderr]\n{full}");
          string lastLine = GetLastNonEmptyLine(full);
          return $"{toolName} exited with code {process.ExitCode}: {lastLine}";
        }
        return null;
      }
      catch (Exception ex)
      {
        return ex.Message;
      }
    }

    private static string GetLastNonEmptyLine(string text)
    {
      if (string.IsNullOrWhiteSpace(text))
        return "(no output)";
      var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      for (int i = lines.Length - 1; i >= 0; i--)
      {
        string line = lines[i].Trim();
        if (line.Length > 0)
          return line;
      }
      return "(no output)";
    }


    /// <summary>
    /// Try to call an exe and return stdout. Returns "Error." on failure.
    /// </summary>
    private static string callExeAndGetStdout(string exe, string args)
    {
      try
      {
        using var process = new Process();
        process.StartInfo = makeToolStartInfo(exe, args, redirectStdout: true, redirectStderr: false);
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
      }
      catch
      {
        return null;
      }
    }


    /// <summary>
    /// Try to call an exe and return stderr. Returns null on failure.
    /// </summary>
    private static string callExeAndGetStderr(string exe, string args)
    {
      try
      {
        using var process = new Process();
        process.StartInfo = makeToolStartInfo(exe, args, redirectStdout: false, redirectStderr: true);
        process.Start();
        string output = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
      }
      catch
      {
        return null;
      }
    }


    /// <summary>
    /// Run a process with progress monitoring and cancel support.
    /// Uses WaitForExitAsync with CancellationToken instead of polling.
    /// Returns true if process started successfully (even if cancelled).
    /// </summary>
    private static bool runProcessWithProgress(string exe, string args, IProgressReporter dialogProgress)
    {
      try
      {
        using var process = new Process();
        process.StartInfo = makeToolStartInfo(exe, args, redirectStdout: false, redirectStderr: true);
        process.ErrorDataReceived += new DataReceivedEventHandler(dialogProgress.OnFFmpegOutput);
        process.Start();
        process.BeginErrorReadLine();

        try
        {
          process.WaitForExitAsync(dialogProgress.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
          try { process.Kill(entireProcessTree: true); } catch { }
        }

        return true;
      }
      catch (OperationCanceledException)
      {
        return true;
      }
      catch
      {
        return false;
      }
    }


    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Call an exe with the provided arguments, trying multiple paths.
    /// Returns null on success, error message on failure.
    /// </summary>
    public static string? startProcess(string relExePath, string fullExePath, string args,
      bool useShellExecute, bool createNoWindow)
    {
      string? lastError = null;
      foreach (string exe in getExePaths(relExePath, fullExePath))
      {
        lastError = callExe(exe, args, useShellExecute, createNoWindow);
        if (lastError == null)
          return null;
      }
      return lastError;
    }


    /// <summary>
    /// Call an exe with the provided arguments. Don't open a window.
    /// Returns null on success, error message on failure.
    /// </summary>
    public static string? startProcess(string relExePath, string fullExePath, string args)
    {
      return startProcess(relExePath, fullExePath, args, false, true);
    }


    /// <summary>
    /// Call an exe with the provided arguments. Return stdout.
    /// </summary>
    public static string startProcessAndGetStdout(string relExePath, string fullExePath, string args)
    {
      foreach (string exe in getExePaths(relExePath, fullExePath))
      {
        string result = callExeAndGetStdout(exe, args);
        if (result != null)
          return result;
      }

      return "Error.";
    }


    /// <summary>
    /// Call ffmpeg with provided arguments. Blocking.
    /// Throws Exception on ffmpeg failure.
    /// </summary>
    public static void startFFmpeg(string ffmpegArgs, bool useShellExecute, bool createNoWindow)
    {
      string? error = startProcess(ConstantSettings.PathFFmpegExe, ConstantSettings.PathFFmpegFullExe,
        withNoStdin(ffmpegArgs), useShellExecute, createNoWindow);
      if (error != null)
        throw new Exception(error);
    }

    /// <summary>
    /// ffmpeg reads stdin for interactive commands ("q" to quit, "?" for help).
    /// With no console attached (WinExe / redirected streams) it can stall or
    /// consume the parent's input, so always pass -nostdin.
    /// </summary>
    private static string withNoStdin(string ffmpegArgs)
    {
      return ffmpegArgs.Contains("-nostdin") ? ffmpegArgs : "-nostdin " + ffmpegArgs;
    }


    /// <summary>
    /// Call ffmpeg with provided arguments and update the progress dialog.
    /// </summary>
    public static void startFFmpegProgress(string ffmpegArgs, IProgressReporter dialogProgress)
    {
      foreach (string exe in getFFmpegPaths())
      {
        if (runProcessWithProgress(exe, withNoStdin(ffmpegArgs), dialogProgress))
          return;
      }
    }


    /// <summary>
    /// Call ffmpeg with the provided arguments. Return the ffmpeg console text (stderr).
    /// </summary>
    public static string getFFmpegText(string ffmpegArgs)
    {
      foreach (string exe in getFFmpegPaths())
      {
        string result = callExeAndGetStderr(exe, withNoStdin(ffmpegArgs));
        if (result != null)
          return result;
      }

      return "";
    }

    /// <summary>
    /// Run ffprobe with the given arguments and return stdout ("" on failure).
    /// </summary>
    public static string getFFprobeStdout(string ffprobeArgs, int timeoutMs = 10000)
    {
      string exe = ConstantSettings.ResolveToolOrName("ffprobe");
      try
      {
        using var proc = new Process();
        proc.StartInfo = makeToolStartInfo(exe, ffprobeArgs, redirectStdout: true, redirectStderr: true);
        proc.Start();
        // Drain stderr asynchronously so a chatty ffprobe cannot block on a full pipe.
        proc.ErrorDataReceived += (s, e) => { };
        proc.BeginErrorReadLine();
        string json = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
          try { proc.Kill(entireProcessTree: true); } catch { }
        }
        return json;
      }
      catch
      {
        return "";
      }
    }
  }
}
