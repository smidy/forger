using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Llm.Tests;

public class LiteLlmClientRetryTests
{
  private const string CannedResponse = """
    {
      "id": "test",
      "choices": [
        { "index": 0, "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" }
      ],
      "usage": { "prompt_tokens": 1, "completion_tokens": 1 }
    }
    """;

  [Fact]
  public async Task Single_429_with_RetryAfter_2_retries_once_and_succeeds()
  {
    var responses = new Queue<HttpResponseMessage>();
    // First: 429 with Retry-After: 2
    var retry429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    retry429.Headers.Add("Retry-After", "2");
    retry429.Content = new StringContent("Rate limit exceeded", Encoding.UTF8, "text/plain");
    responses.Enqueue(retry429);
    // Second: 200 success
    responses.Enqueue(OkResponse(CannedResponse));

    var (client, trace) = Build(responses);
    var req = SimpleRequest();

    var resp = await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    resp.Choices.Should().HaveCount(1);
    resp.Choices[0].Message!.Content!.GetValue<string>().Should().Be("ok");

    // One retry event — the DelayMs assertion below is the authoritative check
    // that Retry-After was honoured (no stopwatch races).
    trace.Events.Should().HaveCount(1);
    var ev = trace.Events[0].As<LlmRetryEvent>();
    ev.Attempt.Should().Be(1);
    ev.MaxAttempts.Should().Be(3);
    ev.DelayMs.Should().Be(2000);
    ev.StatusCode.Should().Be(429);
    ev.RetryAfterHeader.Should().Be("2");
  }

  [Fact]
  public async Task Three_consecutive_429s_exhausts_retries_and_throws_RateLimited()
  {
    var responses = new Queue<HttpResponseMessage>();
    for (var i = 0; i < 4; i++)
    {
      var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
      r429.Content = new StringContent("Too many requests", Encoding.UTF8, "text/plain");
      responses.Enqueue(r429);
    }

    var (client, trace) = Build(responses);

    Func<Task> act = () => client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    var ex = await act.Should().ThrowAsync<RateLimitedException>();
    ex.Which.RetryAfter.Should().BeNull();

    // 3 retry events (attempts 1, 2, 3)
    trace.Events.Should().HaveCount(3);
    trace.Events.Cast<LlmRetryEvent>().Select(e => e.Attempt).Should().Equal(1, 2, 3);
  }

  [Fact]
  public async Task Quota_429_short_circuits_retry_and_throws_QuotaExhausted()
  {
    var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    r429.Content = new StringContent(
      "You exceeded your current quota, please check your plan and billing details.",
      Encoding.UTF8,
      "text/plain");
    var responses = new Queue<HttpResponseMessage>();
    responses.Enqueue(r429);

    var (client, trace) = Build(responses);

    Func<Task> act = () => client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    await act.Should().ThrowAsync<QuotaExhaustedException>();

    // No retry events
    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public async Task RetryAfter_exceeds_cap_surfaces_immediately_no_retry()
  {
    var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    r429.Headers.Add("Retry-After", "120"); // > MaxRetryAfterSeconds (60)
    r429.Content = new StringContent("Rate limited", Encoding.UTF8, "text/plain");
    var responses = new Queue<HttpResponseMessage>();
    responses.Enqueue(r429);

    var (client, trace) = Build(responses);

    Func<Task> act = () => client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    var ex = await act.Should().ThrowAsync<RateLimitedException>();
    ex.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));

    // No retry events (surfaced immediately)
    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public async Task Non_429_error_throws_plain_ProviderException_no_retry()
  {
    var r500 = new HttpResponseMessage(HttpStatusCode.InternalServerError);
    r500.Content = new StringContent("Boom", Encoding.UTF8, "text/plain");
    var responses = new Queue<HttpResponseMessage>();
    responses.Enqueue(r500);

    var (client, trace) = Build(responses);

    Func<Task> act = () => client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    var ex = await act.Should().ThrowAsync<ProviderException>();
    ex.Which.StatusCode.Should().Be(500);

    // No retry events
    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public async Task Normal_200_response_works_as_before()
  {
    var responses = new Queue<HttpResponseMessage>();
    responses.Enqueue(OkResponse(CannedResponse));

    var (client, trace) = Build(responses);
    var resp = await client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    resp.Choices.Should().HaveCount(1);
    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public async Task Two_429s_then_200_retries_twice_and_succeeds()
  {
    var responses = new Queue<HttpResponseMessage>();
    for (var i = 0; i < 2; i++)
    {
      var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
      r429.Content = new StringContent("Too many requests", Encoding.UTF8, "text/plain");
      responses.Enqueue(r429);
    }
    responses.Enqueue(OkResponse(CannedResponse));

    var (client, trace) = Build(responses);
    var resp = await client.CompleteAsync(SimpleRequest(), TestContext.Current.CancellationToken);
    resp.Choices.Should().HaveCount(1);

    trace.Events.Should().HaveCount(2);
    trace.Events.Cast<LlmRetryEvent>().Select(e => e.Attempt).Should().Equal(1, 2);
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private static CompletionRequest SimpleRequest() => new()
  {
    Model = "test-model",
    Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } }
  };

  private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
  {
    Content = new StringContent(body, Encoding.UTF8, "application/json")
  };

  private static (LiteLlmClient Client, CapturingTraceSink Trace) Build(
    Queue<HttpResponseMessage> responses,
    RateLimitConfig? rateLimit = null)
  {
    var handler = new QueuedHandler(responses);
    var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:4000/v1/") };
    var config = new LiteLlmConfig { RateLimit = rateLimit ?? new RateLimitConfig() };
    var trace = new CapturingTraceSink();
    var client = new LiteLlmClient(http, NullLogger<LiteLlmClient>.Instance, config) { TraceSink = trace };
    return (client, trace);
  }

  private sealed class QueuedHandler : HttpMessageHandler
  {
    private readonly Queue<HttpResponseMessage> _queue;
    public QueuedHandler(Queue<HttpResponseMessage> queue) => _queue = queue;

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
    {
      if (_queue.TryDequeue(out var msg))
      {
        return Task.FromResult(msg);
      }
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
      {
        Content = new StringContent("No response queued")
      });
    }
  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
