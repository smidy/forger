using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Llm;
using Forge.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Llm.Tests;

public class LiteLlmClientReasoningTests
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
  public async Task Omitted_reasoning_produces_no_reasoning_keys_on_wire()
  {
    var (client, capture) = Build();
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } }
    };

    await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    var body = capture.LastRequestBody!;
    using var doc = JsonDocument.Parse(body);
    doc.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    doc.RootElement.TryGetProperty("thinking", out _).Should().BeFalse();
  }

  [Fact]
  public async Task Reasoning_effort_serialises_as_top_level_string()
  {
    var (client, capture) = Build();
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
      ReasoningEffort = "medium"
    };

    await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    using var doc = JsonDocument.Parse(capture.LastRequestBody!);
    doc.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("medium");
    doc.RootElement.TryGetProperty("thinking", out _).Should().BeFalse();
  }

  [Fact]
  public async Task Thinking_budget_serialises_as_enabled_object()
  {
    var (client, capture) = Build();
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
      ThinkingBudgetTokens = 2048
    };

    await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    using var doc = JsonDocument.Parse(capture.LastRequestBody!);
    var thinking = doc.RootElement.GetProperty("thinking");
    thinking.GetProperty("type").GetString().Should().Be("enabled");
    thinking.GetProperty("budget_tokens").GetInt32().Should().Be(2048);
    doc.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
  }

  [Fact]
  public async Task Both_set_serialises_both_keys_independently()
  {
    var (client, capture) = Build();
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
      ReasoningEffort = "high",
      ThinkingBudgetTokens = 4096
    };

    await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    using var doc = JsonDocument.Parse(capture.LastRequestBody!);
    doc.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
    doc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(4096);
  }

  [Fact]
  public async Task Reasoning_tokens_parsed_from_completion_tokens_details()
  {
    var respJson = """
      {
        "id": "r",
        "choices": [{ "index": 0, "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" }],
        "usage": {
          "prompt_tokens": 10,
          "completion_tokens": 50,
          "completion_tokens_details": { "reasoning_tokens": 30 }
        }
      }
      """;
    var (client, _) = Build(respJson);
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } }
    };

    var resp = await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    resp.Usage!.ReasoningTokens.Should().Be(30);
  }

  [Fact]
  public async Task Reasoning_tokens_null_when_provider_omits_details()
  {
    var (client, _) = Build();
    var req = new CompletionRequest
    {
      Model = "m",
      Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "hi" } }
    };

    var resp = await client.CompleteAsync(req, TestContext.Current.CancellationToken);

    resp.Usage!.ReasoningTokens.Should().BeNull();
  }

  private static (LiteLlmClient Client, CapturingHandler Handler) Build(string? responseBody = null)
  {
    var handler = new CapturingHandler(responseBody ?? CannedResponse);
    var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:4000/v1/") };
    var client = new LiteLlmClient(http, NullLogger<LiteLlmClient>.Instance);
    return (client, handler);
  }

  private sealed class CapturingHandler : HttpMessageHandler
  {
    private readonly string _response;
    public string? LastRequestBody { get; private set; }

    public CapturingHandler(string response) => _response = response;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      LastRequestBody = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(_response, Encoding.UTF8, "application/json")
      };
    }
  }
}
