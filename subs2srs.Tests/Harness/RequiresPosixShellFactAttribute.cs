using System;
using System.IO;
using Xunit;

namespace subs2srs.Tests.Harness
{
  /// <summary>
  /// A [Fact] that is skipped where there is no POSIX shell at <c>/bin/sh</c> (Windows): for a
  /// test that runs a command line the way a user on Linux or macOS would type it.
  /// </summary>
  public sealed class RequiresPosixShellFactAttribute : FactAttribute
  {
    public const string Shell = "/bin/sh";

    public RequiresPosixShellFactAttribute()
    {
      if (OperatingSystem.IsWindows() || !File.Exists(Shell))
        Skip = "no POSIX shell at " + Shell;
    }
  }
}
