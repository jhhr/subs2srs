using System;
using Xunit;

namespace subs2srs.Tests.Harness
{
  /// <summary>
  /// A [Fact] that runs only when an environment variable is set (non-empty). Used for the
  /// live provider test, which is skipped in every normal `dotnet test` run.
  /// </summary>
  public sealed class RequiresEnvFactAttribute : FactAttribute
  {
    public RequiresEnvFactAttribute(string variable)
    {
      if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        Skip = $"set {variable} to run this test";
    }
  }
}
