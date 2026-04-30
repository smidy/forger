using System.Text.Json;
using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

namespace Forge.Tools.Tests;

public class AskCallerToolChatTests
{
  [Fact]
  public async Task Executing_ask_caller_with_exit_response_throws_ChatExitException()
  {
    var cci = new ExitTestCallerIo("/exit");
    var ctx = BuildContext(cci);
    var tool = new AskCallerTool();
    var input = JsonSerializer.SerializeToNode(
      new AskCallerInput { Question = "hello?" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var act = async () => await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);
    await act.Should().ThrowAsync<ChatExitException>();
  }

  [Fact]
  public async Task Executing_ask_caller_with_normal_response_does_not_throw()
  {
    var cci = new ExitTestCallerIo("normal response");
    var ctx = BuildContext(cci);
    var tool = new AskCallerTool();
    var input = JsonSerializer.SerializeToNode(
      new AskCallerInput { Question = "hello?" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var result = await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);
    var output = JsonSerializer.Deserialize<AskCallerOutput>(
      result.ToJsonString(), JsonSerializationDefaults.CamelCaseTool)!;
    output.Response.Should().Be("normal response");
  }

  [Fact]
  public async Task Exit_is_case_insensitive_and_trimmed()
  {
    var cci = new ExitTestCallerIo("  /ExIt  ");
    var ctx = BuildContext(cci);
    var tool = new AskCallerTool();
    var input = JsonSerializer.SerializeToNode(
      new AskCallerInput { Question = "hello?" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var act = async () => await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);
    await act.Should().ThrowAsync<ChatExitException>();
  }

  [Fact]
  public void SummarizeCall_produces_question_and_answer()
  {
    var tool = new AskCallerTool();
    var summary = tool.SummarizeCall(
      new AskCallerInput { Question = "What is your name?" },
      new AskCallerOutput { Response = "Alice" },
      null);
    summary.Should().Be("\"What is your name?\" → \"Alice\"");
  }

  [Fact]
  public void SummarizeCall_truncates_long_strings()
  {
    var tool = new AskCallerTool();
    var longQ = new string('x', 100);
    var longA = new string('y', 100);
    var summary = tool.SummarizeCall(
      new AskCallerInput { Question = longQ },
      new AskCallerOutput { Response = longA },
      null);
    summary.Should().Be($"\"{longQ[..60]}…\" → \"{longA[..60]}…\"");
  }

  private static ToolContext BuildContext(ICallerIo callerIo)
  {
    var idx = 0;
    return new ToolContext(
      RunId: "run-test",
      RunWorkspace: Path.GetTempPath(),
      StageDir: Path.Combine(Path.GetTempPath(), "stage"),
      StageId: "agent",
      IterationIndex: 0,
      Llm: new StubLlmClient(),
      Trace: new CapturingTraceSink(),
      Logger: NullLogger.Instance,
      CancellationToken: default,
      NextToolOutputIdx: () => ++idx)
    {
      CallerIo = callerIo
    };
  }

  private sealed class ExitTestCallerIo : ICallerIo
  {
    private readonly string _response;
    public ExitTestCallerIo(string response) => _response = response;

    public Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct) =>
      Task.FromResult(new CallerPromptResponse { Response = _response });

    public Task NotifyAsync(CallerNotice notice, CancellationToken ct) => Task.CompletedTask;
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
      Task.FromResult(new ApprovalDecision { Allowed = true });

    public Task WriteAssistantTextAsync(string text, CancellationToken ct) => Task.CompletedTask;
    public Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct)
      => Task.CompletedTask;
    public Task WriteSystemNoticeAsync(string notice, CancellationToken ct) => Task.CompletedTask;
    public string? TryTakeUserInterjection() => null;

  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class StubLlmClient : ILlmClient
  {
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct) =>
      Task.FromResult(new CompletionResponse());
  }
}
