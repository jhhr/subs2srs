using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace subs2srs.Tests.Harness
{
  /// <summary>
  /// IChatProvider that answers from a script instead of the network: a function of the chunk's
  /// user content (the JSON array of lines) to the JSON answer text. Records every request.
  /// Install it with <see cref="Install"/>, which sets <see cref="ChatProviders.Override"/> and
  /// restores it on dispose.
  /// </summary>
  public sealed class FakeChatProvider : IChatProvider
  {
    public string Name => "fake";
    public string Model { get; }
    public Func<string, string, string> Answer { get; set; }
    public List<(string system, string user)> Requests { get; } = new();
    public int InputTokensPerCall { get; set; } = 1000;
    public int OutputTokensPerCall { get; set; } = 50;
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    /// <summary>Throw this for every call (after recording it) instead of answering.</summary>
    public Exception? Failure { get; set; }
    public int MaxConcurrentSeen { get; private set; }
    private int inFlight;
    private readonly object sync = new object();

    public FakeChatProvider(string model = "fake-model", Func<string, string, string>? answer = null)
    {
      Model = model;
      Answer = answer ?? ((system, user) => "{\"snippets\":[]}");
    }

    /// <summary>Answer that joins every kept line of each chunk into one snippet with the given note.</summary>
    public static Func<string, string, string> JoinAll(string note = "fake") => (system, user) =>
    {
      List<int> ids = LineIds(user);
      if (ids.Count < 2) return "{\"snippets\":[]}";
      return FormattableString.Invariant($"{{\"snippets\":[{{\"first\":{ids[0]},\"last\":{ids[ids.Count - 1]},\"note\":\"{note}\"}}]}}");
    };

    /// <summary>Answer that joins consecutive pairs (0-1, 2-3, ...) of the chunk's lines.</summary>
    public static Func<string, string, string> JoinPairs(string note = "pair") => (system, user) =>
    {
      List<int> ids = LineIds(user);
      var parts = new List<string>();
      for (int i = 0; i + 1 < ids.Count; i += 2)
        parts.Add(FormattableString.Invariant($"{{\"first\":{ids[i]},\"last\":{ids[i + 1]},\"note\":\"{note}\"}}"));
      return "{\"snippets\":[" + string.Join(",", parts) + "]}";
    };

    /// <summary>The "i" values of the lines in a user content.</summary>
    public static List<int> LineIds(string user)
    {
      var ids = new List<int>();
      using JsonDocument doc = JsonDocument.Parse(user);
      foreach (JsonElement line in doc.RootElement.EnumerateArray())
        ids.Add(line.GetProperty("i").GetInt32());
      return ids;
    }

    public async Task<ChatCompletion> CompleteJsonAsync(string system, string user, JsonElement schema, CancellationToken ct)
    {
      lock (sync)
      {
        Requests.Add((system, user));
        inFlight++;
        if (inFlight > MaxConcurrentSeen) MaxConcurrentSeen = inFlight;
      }
      try
      {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        ct.ThrowIfCancellationRequested();
        if (Failure != null) throw Failure;
        return new ChatCompletion
        {
          Text = Answer(system, user),
          InputTokens = InputTokensPerCall,
          OutputTokens = OutputTokensPerCall,
          Model = Model,
        };
      }
      finally
      {
        lock (sync) inFlight--;
      }
    }

    /// <summary>Route every ChatProviders.Create call to this fake until the returned scope is disposed.</summary>
    public IDisposable Install()
    {
      Func<string, IChatProvider>? previous = ChatProviders.Override;
      ChatProviders.Override = model => this;
      return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
      private readonly Func<string, IChatProvider>? previous;
      public Restore(Func<string, IChatProvider>? previous) { this.previous = previous; }
      public void Dispose() { ChatProviders.Override = previous; }
    }
  }
}
