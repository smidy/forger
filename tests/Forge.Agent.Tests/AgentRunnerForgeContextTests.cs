using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

/// <summary>
/// Regression tests for the <c>forge.*</c> Scriban namespace injected by
/// <see cref="AgentRunner"/>. Covers F5 of
/// <c>docs/plans/eval-forge-doctor-v2-fixes.md</c>: agents must see an
/// authoritative <c>forge.today</c> rather than guessing the date from model
/// training data.
/// </summary>
public class AgentRunnerForgeContextTests
{
  [Fact]
  public async Task UserPrompt_substitutes_forge_today_with_frozen_clock()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new CapturingLlmClient(SubmitFinalOnly());
    var trace = new NullTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig(
      systemPrompt: "System prompt — today is {{ forge.today }}.",
      userPrompt: "User prompt — today is {{ forge.today }}.");

    var frozen = new FixedTimeProvider(new DateTimeOffset(2026, 4, 22, 10, 30, 0, TimeSpan.Zero));

    await AgentRunner.RunAsync(
      cfg,
      new JsonObject(),
      ctx,
      new ToolRegistry(),
      agentYamlPath: null,
      TestContext.Current.CancellationToken,
      clock: frozen);

    var request = llm.Requests.Should().ContainSingle().Subject;
    // AgentRunner mutates the same messages list after CompleteAsync returns
    // (appending the assistant response + tool results). Assert on index 0/1
    // rather than on the list's current length — the captured reference grows
    // past the point the LLM actually saw.
    var system = request.Messages[0]!["content"]!.GetValue<string>();
    var user = request.Messages[1]!["content"]!.GetValue<string>();

    system.Should().Contain("today is 2026-04-22.");
    user.Should().Contain("today is 2026-04-22.");
  }

  [Fact]
  public async Task UserPrompt_defaults_to_TimeProvider_System_when_clock_omitted()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new CapturingLlmClient(SubmitFinalOnly());
    var ctx = BuildContext(tmp.Path, stageDir, llm, new NullTraceSink());
    var cfg = BuildAgentConfig(
      systemPrompt: "s",
      userPrompt: "date={{ forge.today }}");

    await AgentRunner.RunAsync(
      cfg,
      new JsonObject(),
      ctx,
      new ToolRegistry(),
      agentYamlPath: null,
      TestContext.Current.CancellationToken);

    var request = llm.Requests.Should().ContainSingle().Subject;
    var user = request.Messages[1]!["content"]!.GetValue<string>();

    // Default clock (TimeProvider.System) substitutes today's UTC ISO date.
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    user.Should().Be($"date={today}");
  }

  private static AgentConfig BuildAgentConfig(string systemPrompt, string userPrompt) => new()
  {
    Name = "test",
    Model = "test-model",
    SystemPrompt = systemPrompt,
    UserPrompt = userPrompt,
    MaxIterations = 4,
    Tools = new(),
    InputSchema = new JsonObject { ["type"] = "object" },
    OutputSchema = new JsonObject
    {
      ["type"] = "object",
      ["properties"] = new JsonObject { ["done"] = new JsonObject { ["type"] = "boolean" } },
      ["required"] = new JsonArray("done")
    },
    InjectProjectContext = false,
    InjectSkillsCatalog = false
  };

  private static ToolContext BuildContext(string runRoot, string stageDir, ILlmClient llm, ITraceSink trace)
  {
    var idx = 0;
    return new ToolContext(
      RunId: "test-run",
      RunWorkspace: runRoot,
      StageDir: stageDir,
      StageId: "agent",
      IterationIndex: null,
      Llm: llm,
      Trace: trace,
      Logger: NullLogger.Instance,
      CancellationToken: CancellationToken.None,
      NextToolOutputIdx: () => ++idx);
  }

  private static CompletionResponse SubmitFinalOnly() => new()
  {
    Id = "r",
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
              Id = "call-1",
              Type = "function",
              Function = new FunctionCallPayload { Name = "submit_final", Arguments = """{"done": true}""" }
            }
          }
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private sealed class CapturingLlmClient : ILlmClient
  {
    private readonly CompletionResponse _response;
    public ConcurrentBag<CompletionRequest> Requests { get; } = new();

    public CapturingLlmClient(CompletionResponse response) => _response = response;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      Requests.Add(request);
      return Task.FromResult(_response);
    }
  }

  private sealed class NullTraceSink : ITraceSink
  {
    public void Trace(TraceEvent e) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class FixedTimeProvider : TimeProvider
  {
    private readonly DateTimeOffset _now;
    public FixedTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
  }

  private sealed class TempDir : IDisposable
  {
    public string Path { get; }

    public TempDir()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
      try { Directory.Delete(Path, recursive: true); } catch { }
    }
  }
}
