using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace subs2srs
{
  /// <summary>What the retry loop should do with a provider response.</summary>
  public enum ResponseAction
  {
    Ok,
    Retry,
    /// <summary>Terminal: retrying cannot help (bad request, no credit, daily quota used up).</summary>
    Fail,
  }


  /// <summary>
  /// A classified response: the action and, for a retry, the provider's own wait hint when it
  /// gave one. No hint means the loop falls back to exponential backoff.
  /// </summary>
  public readonly struct ResponseVerdict
  {
    public ResponseAction Action { get; }
    public TimeSpan? Delay { get; }

    private ResponseVerdict(ResponseAction action, TimeSpan? delay)
    {
      Action = action;
      Delay = delay;
    }

    public static ResponseVerdict Ok => new ResponseVerdict(ResponseAction.Ok, null);
    public static ResponseVerdict Fail => new ResponseVerdict(ResponseAction.Fail, null);
    public static ResponseVerdict Retry(TimeSpan? delay) => new ResponseVerdict(ResponseAction.Retry, delay);
  }


  /// <summary>
  /// The ways the providers say "wait this long", ported from <c>api_client.py</c> in the Python
  /// reference. Every parser returns null for junk and never a negative wait.
  /// </summary>
  public static class RateLimitHeaders
  {
    private static readonly Regex GoDurationPart = new Regex(@"([0-9]*\.?[0-9]+)(ms|us|µs|ns|[smh])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoogleDuration = new Regex(@"^([0-9]*\.?[0-9]+)s$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>First value of a header, or null when absent.</summary>
    public static string? First(HttpHeaders? headers, string name)
    {
      if (headers != null && headers.TryGetValues(name, out IEnumerable<string>? values))
      {
        foreach (string v in values) return v;
      }
      return null;
    }

    /// <summary>A plain number of seconds ("20", "1.5").</summary>
    public static TimeSpan? ParseSeconds(string? value)
    {
      if (string.IsNullOrWhiteSpace(value)) return null;
      if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs)) return null;
      if (double.IsNaN(secs) || double.IsInfinity(secs)) return null;
      return TimeSpan.FromSeconds(Math.Max(0, secs));
    }

    /// <summary><c>Retry-After</c>: seconds, or an HTTP-date measured from <paramref name="now"/>.</summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
      TimeSpan? secs = ParseSeconds(value);
      if (secs.HasValue) return secs;
      if (string.IsNullOrWhiteSpace(value)) return null;
      if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset at))
      {
        TimeSpan d = at - now;
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
      }
      return null;
    }

    /// <summary>An RFC 3339 timestamp (Anthropic's <c>*-reset</c> headers) as seconds from <paramref name="now"/>; a naive timestamp counts as UTC.</summary>
    public static TimeSpan? ParseRfc3339Reset(string? value, DateTimeOffset now)
    {
      if (string.IsNullOrWhiteSpace(value)) return null;
      string text = value.Trim();
      // Only ISO-like shapes: a date, a 'T' and a time. DateTimeOffset.TryParse is far too lenient on its own.
      if (text.Length < 19 || text[4] != '-' || text[7] != '-' || (text[10] != 'T' && text[10] != 't' && text[10] != ' ')) return null;
      if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out DateTimeOffset at)) return null;
      TimeSpan d = at - now;
      return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }

    /// <summary>OpenAI's Go-style durations: "1s", "6m0s", "20ms", "1.5s". Anything not fully understood is rejected.</summary>
    public static TimeSpan? ParseGoDuration(string? value)
    {
      if (string.IsNullOrWhiteSpace(value)) return null;
      string text = value.Trim();
      MatchCollection parts = GoDurationPart.Matches(text);
      if (parts.Count == 0) return null;
      var rebuilt = new System.Text.StringBuilder();
      double total = 0;
      foreach (Match m in parts)
      {
        rebuilt.Append(m.Value);
        double amount = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        total += amount * m.Groups[2].Value switch
        {
          "ns" => 1e-9,
          "us" => 1e-6,
          "µs" => 1e-6,
          "ms" => 1e-3,
          "s" => 1.0,
          "m" => 60.0,
          "h" => 3600.0,
          _ => 0.0,
        };
      }
      if (rebuilt.ToString() != text) return null; // junk between or after the parts, e.g. "6m0s extra"
      return TimeSpan.FromSeconds(Math.Max(0, total));
    }

    /// <summary>Google's protobuf-JSON durations, always plain seconds like "34s".</summary>
    public static TimeSpan? ParseGoogleDuration(string? value)
    {
      if (string.IsNullOrWhiteSpace(value)) return null;
      Match m = GoogleDuration.Match(value.Trim());
      if (!m.Success) return null;
      double secs = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
      return TimeSpan.FromSeconds(Math.Max(0, secs));
    }

    /// <summary>
    /// How long each spent limit bucket says it needs. A bucket with headroom, an absent bucket, or
    /// one whose remaining count or reset does not parse contributes nothing: a header the provider
    /// did not send is not a bucket at zero.
    /// </summary>
    public static List<TimeSpan> ExhaustedBucketWaits(HttpHeaders? headers,
      IReadOnlyList<(string remaining, string reset)> buckets, Func<string?, TimeSpan?> parseReset)
    {
      var waits = new List<TimeSpan>();
      foreach ((string remaining, string reset) in buckets)
      {
        string? left = First(headers, remaining);
        if (left == null) continue;
        if (!long.TryParse(left.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n > 0) continue;
        TimeSpan? wait = parseReset(First(headers, reset));
        if (wait.HasValue) waits.Add(wait.Value);
      }
      return waits;
    }

    /// <summary>The longest wait among the spent buckets, or null when every bucket still has headroom.</summary>
    public static TimeSpan? LongestExhaustedWait(HttpHeaders? headers,
      IReadOnlyList<(string remaining, string reset)> buckets, Func<string?, TimeSpan?> parseReset)
    {
      TimeSpan? longest = null;
      foreach (TimeSpan w in ExhaustedBucketWaits(headers, buckets, parseReset))
        if (!longest.HasValue || w > longest.Value) longest = w;
      return longest;
    }

    /// <summary>The smallest of the given waits, or null when there is none.</summary>
    public static TimeSpan? Soonest(params TimeSpan?[] waits)
    {
      TimeSpan? soonest = null;
      foreach (TimeSpan? w in waits)
        if (w.HasValue && (!soonest.HasValue || w.Value < soonest.Value)) soonest = w;
      return soonest;
    }

    /// <summary>The largest of the given waits, or null when there is none.</summary>
    public static TimeSpan? Longest(params TimeSpan?[] waits)
    {
      TimeSpan? longest = null;
      foreach (TimeSpan? w in waits)
        if (w.HasValue && (!longest.HasValue || w.Value > longest.Value)) longest = w;
      return longest;
    }
  }


  /// <summary>
  /// Which models are in a rate-limit cooldown. One entry per <c>provider:model</c> because limits
  /// are enforced per model; shared by every request in the process (<see cref="Shared"/>), so one
  /// task's 429 makes the others wait instead of spending a request on the same rejection.
  /// Thread-safe. The clock is monotonic (<see cref="Stopwatch"/>) and injectable for tests.
  /// </summary>
  public sealed class RateLimitTracker
  {
    private static readonly Stopwatch processClock = Stopwatch.StartNew();

    public static RateLimitTracker Shared { get; } = new RateLimitTracker();

    private readonly object sync = new object();
    private readonly Func<TimeSpan> clock;
    private readonly Dictionary<string, TimeSpan> cooldownUntil = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
    // When each cooldown was installed, to tell a response that predates it from one that can speak to whether the limit cleared.
    private readonly Dictionary<string, TimeSpan> cooldownSetAt = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    public RateLimitTracker(Func<TimeSpan>? clock = null)
    {
      this.clock = clock ?? (() => processClock.Elapsed);
    }

    public static string KeyFor(string provider, string model) => provider + ":" + model;

    /// <summary>The tracker's idea of now; stamp a request with it before sending (see <see cref="NoteSuccess"/>).</summary>
    public TimeSpan Now => clock();

    /// <summary>Time left on the model's cooldown, zero when it is clear.</summary>
    public TimeSpan WaitTime(string key)
    {
      lock (sync)
      {
        if (!cooldownUntil.TryGetValue(key, out TimeSpan until)) return TimeSpan.Zero;
        TimeSpan remaining = until - clock();
        if (remaining <= TimeSpan.Zero)
        {
          cooldownUntil.Remove(key);
          cooldownSetAt.Remove(key);
          return TimeSpan.Zero;
        }
        return remaining;
      }
    }

    /// <summary>Hold further requests to the model for <paramref name="delay"/>. Never shortens an existing cooldown.</summary>
    public void NoteRateLimited(string key, TimeSpan delay)
    {
      lock (sync)
      {
        TimeSpan now = clock();
        TimeSpan until = now + delay;
        if (cooldownUntil.TryGetValue(key, out TimeSpan existing) && existing > until) until = existing;
        cooldownUntil[key] = until;
        cooldownSetAt[key] = now;
      }
    }

    /// <summary>
    /// A successful response clears the model's cooldown, unless it is stale: a request that was
    /// already in flight when the cooldown went up was accepted before the limit was reached, so
    /// its 200 says nothing about whether the limit has cleared. Only a request sent strictly after
    /// the cooldown was installed can clear it (a tie counts as stale). No <paramref name="sentAt"/>
    /// clears unconditionally.
    /// </summary>
    public void NoteSuccess(string key, TimeSpan? sentAt = null)
    {
      lock (sync)
      {
        if (sentAt.HasValue && cooldownSetAt.TryGetValue(key, out TimeSpan setAt) && sentAt.Value <= setAt) return;
        cooldownUntil.Remove(key);
        cooldownSetAt.Remove(key);
      }
    }

    public void Reset()
    {
      lock (sync)
      {
        cooldownUntil.Clear();
        cooldownSetAt.Clear();
      }
    }
  }
}
