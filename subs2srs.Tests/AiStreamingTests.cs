//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace subs2srs.Tests
{
  /// <summary>
  /// Streamed (server-sent events) answers: the reader, the Anthropic adapter's assembly of a
  /// stream, stops inside a stream, error events inside a 200, and the output limit. No network.
  /// </summary>
  public class AiStreamingTests
  {
    private static JsonElement Schema => AiGroupingPrompt.Schema;

    private const string AnswerJson = "{\"snippets\":[{\"first\":0,\"last\":1,\"note\":\"answer to 0\"}]}";

    /// <summary>An SSE body from (event name, data object) pairs.</summary>
    private static string Sse(params (string evt, object data)[] events)
    {
      var sb = new StringBuilder();
      foreach (var (evt, data) in events)
      {
        sb.Append("event: ").Append(evt).Append('\n');
        sb.Append("data: ").Append(JsonSerializer.Serialize(data)).Append("\n\n");
      }
      return sb.ToString();
    }

    private static (string, object) MessageStart(int input = 73, int cacheRead = 10) =>
      ("message_start", new
      {
        type = "message_start",
        message = new
        {
          id = "msg_01", type = "message", role = "assistant", content = Array.Empty<object>(), model = "claude-sonnet-5",
          stop_reason = (string?)null, usage = new { input_tokens = input, cache_read_input_tokens = cacheRead, output_tokens = 1 }
        }
      });

    private static (string, object)[] ThinkingBlock(int index) => new (string, object)[]
    {
      ("content_block_start", new { type = "content_block_start", index, content_block = new { type = "thinking", thinking = "" } }),
      ("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking = "" } }),
      ("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "signature_delta", signature = "EqQBCgIYAhIM" } }),
      ("content_block_stop", new { type = "content_block_stop", index }),
    };

    private static (string, object)[] TextBlock(int index, params string[] pieces)
    {
      var list = new List<(string, object)>
      {
        ("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = "" } }),
        ("ping", new { type = "ping" }),
      };
      foreach (string piece in pieces)
        list.Add(("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text = piece } }));
      list.Add(("content_block_stop", new { type = "content_block_stop", index }));
      return list.ToArray();
    }

    private static (string, object) MessageDelta(string stop, int output) =>
      ("message_delta", new { type = "message_delta", delta = new { stop_reason = stop, stop_sequence = (string?)null }, usage = new { output_tokens = output } });

    private static (string, object) MessageStop() => ("message_stop", new { type = "message_stop" });

    private static string OkStream() =>
      Sse(new[] { MessageStart() }
        .Concat(ThinkingBlock(0))
        .Concat(TextBlock(1, "{\"snippets\":[{\"first\":0,", "\"last\":1,\"note\":\"answer to 0\"}]}"))
        .Append(MessageDelta("end_turn", 27))
        .Append(MessageStop())
        .ToArray());

    // ── reader ────────────────────────────────────────────────────────

    [Fact]
    public async Task ServerSentEvents_ReadsNamesMultiLineDataCommentsCrlfAndAnUnterminatedLastEvent()
    {
      string body = "event: a\r\ndata: {\"x\":1}\r\n\r\n: keep-alive comment\r\n\r\nevent: b\ndata: line1\ndata: line2\n\ndata:no-space\n\nevent: c\ndata: tail";
      using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));

      List<ServerSentEvent> events = await ServerSentEvents.ReadAllAsync(stream, CancellationToken.None);

      Assert.Equal(4, events.Count);
      Assert.Equal(("a", "{\"x\":1}"), (events[0].Event, events[0].Data));
      Assert.Equal(("b", "line1\nline2"), (events[1].Event, events[1].Data));
      Assert.Equal(("", "no-space"), (events[2].Event, events[2].Data));
      Assert.Equal(("c", "tail"), (events[3].Event, events[3].Data));
    }

    // ── Anthropic streamed answers ────────────────────────────────────

    [Fact]
    public async Task Anthropic_Stream_IsAssembledFromTextDeltas_WithCumulativeUsage()
    {
      var handler = new ScriptedHandler().ReplySse(OkStream());
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(AnswerJson, c.Text);
      Assert.Equal(83, c.InputTokens); // input + cache read from message_start
      Assert.Equal(27, c.OutputTokens); // the cumulative count from message_delta, not message_start's 1
      Assert.Equal("claude-sonnet-5", c.Model);

      using JsonDocument request = JsonDocument.Parse(handler.Requests[0].body);
      Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
      Assert.Equal(32_000, request.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Anthropic_Stream_MaxTokensStop_IsACutOff()
    {
      string body = Sse(new[] { MessageStart() }
        .Concat(TextBlock(0, "{\"snippets\":[{\"first\":0,"))
        .Append(MessageDelta("max_tokens", 32_000))
        .Append(MessageStop())
        .ToArray());
      var handler = new ScriptedHandler().ReplySse(body);
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Contains("cut off at max_tokens (32000)", ex.Message);
      Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Anthropic_Stream_ThinkingOnly_ReportsNoTextBlock()
    {
      string body = Sse(new[] { MessageStart() }
        .Concat(ThinkingBlock(0))
        .Append(MessageDelta("max_tokens", 32_000))
        .Append(MessageStop())
        .ToArray());
      var handler = new ScriptedHandler().ReplySse(body);
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Contains("No text block in the response (stop_reason: max_tokens)", ex.Message);
    }

    [Fact]
    public async Task Anthropic_Stream_MidStreamOverload_IsRetriedLikeA529()
    {
      string overloaded = Sse(MessageStart(),
        ("error", new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }));
      var delays = new List<TimeSpan>();
      var handler = new ScriptedHandler().ReplySse(overloaded).ReplySse(OkStream());
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry(delays));

      ChatCompletion c = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(2, handler.Requests.Count);
      Assert.Single(delays);
      Assert.Equal(AnswerJson, c.Text);
    }

    [Fact]
    public async Task Anthropic_Stream_MidStreamOverload_GivesUpWithTheStatusAndText()
    {
      string overloaded = Sse(MessageStart(),
        ("error", new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }));
      var handler = new ScriptedHandler().ReplySse(overloaded).ReplySse(overloaded).ReplySse(overloaded);
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Equal(3, handler.Requests.Count);
      Assert.Equal(529, ex.Status);
      Assert.Equal("Overloaded", ex.ProviderMessage);
    }

    [Fact]
    public async Task Anthropic_Stream_MidStreamInvalidRequest_IsTerminal()
    {
      string body = Sse(MessageStart(),
        ("error", new { type = "error", error = new { type = "invalid_request_error", message = "bad request" } }));
      var handler = new ScriptedHandler().ReplySse(body);
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None));

      Assert.Single(handler.Requests);
      Assert.Equal(400, ex.Status);
      Assert.Equal("bad request", ex.ProviderMessage);
    }

    [Fact]
    public async Task Anthropic_JsonAnswer_IsStillAccepted()
    {
      var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, AiProviderTests.Fixture("anthropic-ok.json"));
      var p = new AnthropicProvider("claude-sonnet-5", "k", new HttpClient(handler), AiProviderTests.FastRetry());

      ChatCompletion c = await p.CompleteJsonAsync("s", "u", Schema, CancellationToken.None);

      Assert.Equal(83, c.InputTokens);
      Assert.Equal(27, c.OutputTokens);
    }

    [Theory]
    [InlineData("overloaded_error", 529)]
    [InlineData("rate_limit_error", 429)]
    [InlineData("api_error", 500)]
    [InlineData("timeout_error", 504)]
    [InlineData("authentication_error", 401)]
    [InlineData("invalid_request_error", 400)]
    [InlineData("something_new", 400)]
    public void Anthropic_StreamErrorTypes_MapToTheirHttpStatus(string type, int status)
    {
      Assert.Equal(status, AnthropicProvider.StatusForStreamError(type));
    }

    // ── output limit ──────────────────────────────────────────────────

    [Fact]
    public void Anthropic_OutputLimit_IsTheTarget_LoweredToTheModelsCap()
    {
      Assert.Equal(128_000, AnthropicProvider.OutputCapFor("claude-sonnet-5"));
      Assert.Equal(128_000, AnthropicProvider.OutputCapFor("claude-opus-5"));
      Assert.Equal(128_000, AnthropicProvider.OutputCapFor("claude-fable-5-1"));
      Assert.Equal(64_000, AnthropicProvider.OutputCapFor("claude-haiku-4-5-20251001"));
      Assert.Null(AnthropicProvider.OutputCapFor("claude-sonnet-50"));
      Assert.Null(AnthropicProvider.OutputCapFor(""));

      Assert.Equal(32_000, AnthropicProvider.MaxTokensFor("claude-sonnet-5"));
      Assert.Equal(32_000, AnthropicProvider.MaxTokensFor("claude-haiku-4-5"));
      Assert.Equal(32_000, AnthropicProvider.MaxTokensFor("claude-unlisted-model"));

      var p = new AnthropicProvider("claude-haiku-4-5", "k");
      Assert.Equal(32_000, p.MaxTokens);
      var custom = new AnthropicProvider("claude-sonnet-5", "k") { MaxTokens = 1000 };
      Assert.Equal(1000, custom.MaxTokens);
    }

    [Fact]
    public void RequestTimeout_DefaultsTo600Seconds()
    {
      Assert.Equal(TimeSpan.FromSeconds(600), new RetryPolicy().RequestTimeout);
      Assert.Equal(600, PrefDefaults.AiRequestTimeoutSeconds);
    }
  }
}
