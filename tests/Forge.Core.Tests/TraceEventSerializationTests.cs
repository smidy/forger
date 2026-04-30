using System.Text.Json;
using FluentAssertions;
using Forge.Core.Json;
using Forge.Core.Trace;

namespace Forge.Core.Tests;

public class TraceEventSerializationTests
{
  [Fact]
  public void AgentIterationEvent_serializes_with_agent_iteration_kind()
  {
    var ev = new AgentIterationEvent { Index = 3 };
    var json = JsonSerializer.Serialize(ev, ev.GetType(), JsonSerializationDefaults.Trace);
    json.Should().Contain("\"kind\":\"agent_iteration\"");
    json.Should().Contain("\"index\":3");
  }

  [Fact]
  public void LlmCallEvent_serializes_with_usage_fields_when_present()
  {
    var ev = new LlmCallEvent
    {
      Iteration = 2,
      DurationMs = 1234,
      FinishReason = "tool_calls",
      PromptTokens = 500,
      CompletionTokens = 80
    };
    var json = JsonSerializer.Serialize(ev, ev.GetType(), JsonSerializationDefaults.Trace);
    json.Should().Contain("\"kind\":\"llm_call\"");
    json.Should().Contain("\"iteration\":2");
    json.Should().Contain("\"durationMs\":1234");
    json.Should().Contain("\"promptTokens\":500");
    json.Should().Contain("\"completionTokens\":80");
    json.Should().Contain("\"finishReason\":\"tool_calls\"");
  }

  [Fact]
  public void LlmCallEvent_serializes_cache_fields_when_present()
  {
    var ev = new LlmCallEvent
    {
      Iteration = 0,
      DurationMs = 42,
      PromptTokens = 2048,
      CompletionTokens = 64,
      PromptCacheHitTokens = 1800,
      PromptCacheCreationTokens = 200
    };
    var json = JsonSerializer.Serialize(ev, ev.GetType(), JsonSerializationDefaults.Trace);
    json.Should().Contain("\"promptCacheHitTokens\":1800");
    json.Should().Contain("\"promptCacheCreationTokens\":200");
  }

  [Fact]
  public void ToolCallEvent_serializes_with_error_when_failed()
  {
    var ev = new ToolCallEvent
    {
      Iteration = 1,
      CallId = "call_abc",
      ToolName = "read_file",
      ArgsHash = "deadbeefcafef00d",
      DurationMs = 12,
      Error = "file not found"
    };
    var json = JsonSerializer.Serialize(ev, ev.GetType(), JsonSerializationDefaults.Trace);
    json.Should().Contain("\"kind\":\"tool_call\"");
    json.Should().Contain("\"toolName\":\"read_file\"");
    json.Should().Contain("\"argsHash\":\"deadbeefcafef00d\"");
    json.Should().Contain("\"error\":\"file not found\"");
  }

}
