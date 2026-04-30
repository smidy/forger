using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

public class AgentRunnerChatTests
{
  [Fact]
  public async Task Null_output_schema_excludes_submit_final_from_tool_specs()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    var cfg = BuildChatAgentConfig(outputSchema: null);
    var input = new JsonObject { ["message"] = "hello" };
    var ctx = env.NewContext(new RepeatingLlmClient(
      TextOnlyResponse("Hello")), cci);

    var act = async () => await AgentRunner.RunAsync(
      cfg, input, ctx, new ToolRegistry(), agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: true);

    await act.Should().ThrowAsync<AgentException>()
      .WithMessage("*MaxIterations*");
  }

  [Fact]
  public async Task Implicit_turn_exit_via_slash_exit_returns_exit_payload()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    cci.EnqueueResponse("/exit");
    var cfg = BuildChatAgentConfig(outputSchema: null);
    var input = new JsonObject { ["message"] = "hello" };

    var llm = new RepeatingLlmClient(
      TextOnlyResponse("Hello!"));

    var ctx = env.NewContext(llm, cci);

    var result = await AgentRunner.RunAsync(
      cfg, input, ctx, new ToolRegistry(), agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: true);

    result.Should().BeOfType<JsonObject>();
    ((JsonObject)result)["exited"]?.GetValue<bool>().Should().BeTrue();
    env.Trace.Events.OfType<ChatExitEvent>().Should().ContainSingle()
      .Which.Reason.Should().Be("user_exit");
  }

  [Fact]
  public async Task Chat_exit_exception_from_ask_caller_caught_when_isChat()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    cci.EnqueueResponse("/exit");
    var registry = new ToolRegistry();
    registry.Register(new AskCallerTool());
    var cfg = BuildChatAgentConfig(outputSchema: null, tools: new() { "ask_caller" });
    var input = new JsonObject { ["message"] = "ask me something" };

    var llm = new ScriptedLlmClient(
      ToolCall("ask_caller", new JsonObject { ["question"] = "Ok?" }, callId: "c1"));

    var ctx = env.NewContext(llm, cci);

    var result = await AgentRunner.RunAsync(
      cfg, input, ctx, registry, agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: true);

    ((JsonObject)result)["exited"]?.GetValue<bool>().Should().BeTrue();
    env.Trace.Events.OfType<ChatExitEvent>().Should().ContainSingle()
      .Which.Reason.Should().Be("user_exit");
  }

  [Fact]
  public async Task Chat_exit_exception_becomes_tool_error_when_not_isChat()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    cci.EnqueueResponse("/exit");
    var registry = new ToolRegistry();
    registry.Register(new AskCallerTool());
    var cfg = BuildChatAgentConfig(
      outputSchema: new JsonObject { ["type"] = "object" },
      tools: new() { "ask_caller" });
    var input = new JsonObject();

    var llm = new ScriptedLlmClient(
      ToolCall("ask_caller", new JsonObject { ["question"] = "Ok?" }, callId: "c1"),
      Completion("submit_final", "{}"));

    var ctx = env.NewContext(llm, cci);

    var result = await AgentRunner.RunAsync(
      cfg, input, ctx, registry, agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: false);

    result.Should().NotBeNull();
  }

  [Fact]
  public async Task Structured_agent_under_chat_still_validates_output_schema()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    var cfg = BuildChatAgentConfig(
      outputSchema: new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() });
    var input = new JsonObject();

    var payload = new JsonObject { ["answer"] = "42" };
    var llm = new ScriptedLlmClient(
      Completion("submit_final", payload.ToJsonString()));

    var ctx = env.NewContext(llm, cci);

    var result = await AgentRunner.RunAsync(
      cfg, input, ctx, new ToolRegistry(), agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: true);

    result.Should().NotBeNull();
    ((JsonObject)result)["answer"]?.GetValue<string>().Should().Be("42");
  }

  [Fact]
  public async Task Interjection_picked_up_at_top_of_iteration()
  {
    var env = new ChatTestEnv();
    var cci = new TestCallerIo();
    cci.EnqueueNudge("tell me more");
    var cfg = BuildChatAgentConfig(outputSchema: null);
    var input = new JsonObject { ["message"] = "hello" };

    var llm = new RepeatingLlmClient(
      TextOnlyResponse("here is more"));

    var ctx = env.NewContext(llm, cci);

    var act = async () => await AgentRunner.RunAsync(
      cfg, input, ctx, new ToolRegistry(), agentYamlPath: null,
      TestContext.Current.CancellationToken, isChat: true);

    await act.Should().ThrowAsync<AgentException>()
      .WithMessage("*MaxIterations*");

    env.Trace.Events.OfType<UserInterjectionEvent>().Should().ContainSingle()
      .Which.Length.Should().Be("tell me more".Length);
  }

  private static AgentConfig BuildChatAgentConfig(
    JsonNode? outputSchema = null,
    List<string>? tools = null) => new()
    {
      Name = "chat-test",
      Model = "test-model",
      SystemPrompt = "You are helpful.",
      UserPrompt = "{{ input.message }}",
      MaxIterations = 4,
      Tools = tools ?? new(),
      InputSchema = new JsonObject { ["type"] = "object" },
      OutputSchema = outputSchema,
      InjectProjectContext = false,
      InjectSkillsCatalog = false
    };

  private static CompletionResponse TextOnlyResponse(string content) => new()
  {
    Id = Guid.NewGuid().ToString("N"),
    Choices = new()
    {
      new CompletionChoice
      {
        Index = 0,
        FinishReason = "stop",
        Message = new ChatMessagePayload
        {
          Role = "assistant",
          Content = JsonValue.Create(content)
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private static CompletionResponse Completion(string toolName, string args) => new()
  {
    Id = Guid.NewGuid().ToString("N"),
    Choices = new()
    {
      new CompletionChoice
      {
        Index = 0,
        FinishReason = "tool_calls",
        Message = new ChatMessagePayload
        {
          Role = "assistant",
          ToolCalls = new()
          {
            new ToolCallPayload
            {
              Id = "c1",
              Type = "function",
              Function = new FunctionCallPayload
              {
                Name = toolName,
                Arguments = args
              }
            }
          }
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private static CompletionResponse ToolCall(string name, JsonNode args, string callId) =>
    Completion(name, args.ToJsonString());

  private sealed class ScriptedLlmClient : ILlmClient
  {
    private readonly Queue<CompletionResponse> _responses;

    public ScriptedLlmClient(params CompletionResponse[] responses)
    {
      _responses = new Queue<CompletionResponse>(responses);
    }

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      if (_responses.Count == 0)
        throw new InvalidOperationException("ScriptedLlmClient exhausted.");
      return Task.FromResult(_responses.Dequeue());
    }
  }

  private sealed class RepeatingLlmClient : ILlmClient
  {
    private readonly CompletionResponse _response;

    public RepeatingLlmClient(CompletionResponse response) => _response = response;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(_response);
  }

  private sealed class TestCallerIo : ICallerIo
  {
    private readonly Queue<CallerPromptResponse> _responses = new();
    private string? _nudge;
    public List<string> WrittenText { get; } = new();
    public int PromptCount { get; private set; }

    public void EnqueueResponse(string text) => _responses.Enqueue(new CallerPromptResponse { Response = text });
    public void EnqueueNudge(string text) => _nudge = text;

    public Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct)
    {
      PromptCount++;
      if (_responses.Count > 0)
        return Task.FromResult(_responses.Dequeue());
      return Task.FromResult(new CallerPromptResponse { Response = string.Empty });
    }

    public Task NotifyAsync(CallerNotice notice, CancellationToken ct) => Task.CompletedTask;
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
      Task.FromResult(new ApprovalDecision { Allowed = true, Reason = "test" });

    public Task WriteAssistantTextAsync(string text, CancellationToken ct)
    {
      WrittenText.Add(text);
      return Task.CompletedTask;
    }

    public Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct)
      => Task.CompletedTask;

    public Task WriteSystemNoticeAsync(string notice, CancellationToken ct) => Task.CompletedTask;

    public string? TryTakeUserInterjection()
    {
      var n = _nudge;
      _nudge = null;
      return n;
    }
  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class ChatTestEnv : IDisposable
  {
    public string Root { get; }
    public string Run { get; }
    public CapturingTraceSink Trace { get; } = new();

    public ChatTestEnv()
    {
      Root = Path.Combine(Path.GetTempPath(), "forge-chat-" + Guid.NewGuid());
      Run = Path.Combine(Root, "run");
      Directory.CreateDirectory(Run);
    }

    public ToolContext NewContext(ILlmClient llm, ICallerIo? callerIo = null)
    {
      var stageDir = WorkspacePaths.StageDir(Run, "agent");
      Directory.CreateDirectory(stageDir);
      var idx = 0;
      return new ToolContext(
        RunId: "run-chat",
        RunWorkspace: Run,
        StageDir: stageDir,
        StageId: "agent",
        IterationIndex: null,
        Llm: llm,
        Trace: Trace,
        Logger: NullLogger.Instance,
        CancellationToken: default,
        NextToolOutputIdx: () => ++idx)
      {
        CallerIo = callerIo
      };
    }

    public void Dispose()
    {
      try { Directory.Delete(Root, recursive: true); } catch { }
    }
  }
}
