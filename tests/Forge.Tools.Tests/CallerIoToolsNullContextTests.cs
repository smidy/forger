using System.Text.Json;
using FluentAssertions;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Tools.Tests;

public class CallerIoToolsNullContextTests
{
  [Fact]
  public async Task AskCallerTool_throws_when_CallerIo_is_null()
  {
    var ctx = BuildContextWithoutCallerIo();
    var tool = new AskCallerTool();
    var input = JsonSerializer.SerializeToNode(
      new AskCallerInput { Question = "hello?" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var act = async () => await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);

    (await act.Should().ThrowAsync<InvalidOperationException>())
      .WithMessage("*ask_caller requires an ICallerIo transport*");
  }

  [Fact]
  public async Task NotifyCallerTool_throws_when_CallerIo_is_null()
  {
    var ctx = BuildContextWithoutCallerIo();
    var tool = new NotifyCallerTool();
    var input = JsonSerializer.SerializeToNode(
      new NotifyCallerInput { Level = "info", Summary = "test notice" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var act = async () => await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);

    (await act.Should().ThrowAsync<InvalidOperationException>())
      .WithMessage("*notify_caller requires an ICallerIo transport*");
  }

  [Fact]
  public async Task RequestApprovalTool_throws_when_CallerIo_is_null()
  {
    var ctx = BuildContextWithoutCallerIo();
    var tool = new RequestApprovalTool();
    var input = JsonSerializer.SerializeToNode(
      new RequestApprovalInput { Action = "delete_files", Summary = "delete 4 tmp fixtures" },
      JsonSerializationDefaults.CamelCaseTool)!;

    var act = async () => await tool.ExecuteAsync(input, ctx, TestContext.Current.CancellationToken);

    (await act.Should().ThrowAsync<InvalidOperationException>())
      .WithMessage("*request_approval requires an ICallerIo transport*");
  }

  private static ToolContext BuildContextWithoutCallerIo()
  {
    var tmp = Path.GetTempPath();
    return new ToolContext(
      RunId: "run-0",
      RunWorkspace: tmp,
      StageDir: tmp,
      StageId: "stage-0",
      IterationIndex: null,
      Llm: NullLlmClient.Instance,
      Trace: new NullTraceSink(),
      Logger: NullLogger.Instance,
      CancellationToken: default,
      NextToolOutputIdx: () => 0);
  }

  private sealed class NullTraceSink : ITraceSink
  {
    public void Trace(TraceEvent e) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class NullLlmClient : ILlmClient
  {
    public static NullLlmClient Instance { get; } = new();

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct) =>
      throw new NotSupportedException("LLM not used in these tests.");
  }
}
