using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Core.Types;

namespace Forge.Tools;

public sealed class AskCallerInput
{
  public required string Question { get; init; }
  public string? Hint { get; init; }
  public IReadOnlyList<string>? Choices { get; init; }
}

public sealed class AskCallerOutput
{
  public required string Response { get; init; }
}

public sealed class AskCallerTool : ToolBase<AskCallerInput, AskCallerOutput>
{
  public override string Name => "ask_caller";
  public override string Description =>
    "Ask the caller a question and wait for their response. " +
    "In interactive mode the user is prompted; in headless/pipeline mode the stage " +
    "transitions to needs_caller and is resumable via `forge resume --answer`.";

  public override string? SummarizeCall(AskCallerInput input, AskCallerOutput? output, string? error)
  {
    var q = Truncate(input.Question);
    var a = Truncate(output?.Response ?? "");
    return $"\"{q}\" → \"{a}\"";
  }

  protected override async Task<AskCallerOutput> ExecuteCoreAsync(
    AskCallerInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    if (ctx.CallerIo is null)
    {
      throw new InvalidOperationException(
        "ask_caller requires an ICallerIo transport — none is wired in this run context. " +
        "Run `forge agent --callers auto-deny` (or a terminal session) to enable caller-IO tools.");
    }

    var prompt = new CallerPrompt
    {
      Question = input.Question,
      Hint = input.Hint,
      Choices = input.Choices,
      SourceToolName = Name
    };

    var response = await ctx.CallerIo.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);

    if (string.Equals(response.Response?.Trim(), ChatExitException.ExitCommand, StringComparison.OrdinalIgnoreCase))
    {
      throw new ChatExitException();
    }

    ctx.Trace.Trace(new CallerPromptEvent
    {
      Iteration = ctx.IterationIndex ?? 0,
      QuestionLength = input.Question.Length,
      ResponseLength = response.Response?.Length ?? 0,
      ChoicesCount = input.Choices?.Count ?? 0,
      Resumed = false
    });

    return new AskCallerOutput { Response = response.Response ?? string.Empty };
  }
}
