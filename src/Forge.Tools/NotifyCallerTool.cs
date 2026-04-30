using System.Text.Json.Nodes;
using Forge.Core.Trace;
using Forge.Core.Types;

namespace Forge.Tools;

public sealed class NotifyCallerInput
{
  /// <summary>"info" | "warn" | "error"</summary>
  public required string Level { get; init; }

  public required string Summary { get; init; }
  public string? Detail { get; init; }
}

public sealed class NotifyCallerOutput
{
  public bool Delivered { get; init; }
}

public sealed class NotifyCallerTool : ToolBase<NotifyCallerInput, NotifyCallerOutput>
{
  public override string Name => "notify_caller";
  public override string Description =>
    "Send a one-shot notification to the caller. In interactive mode it prints in-band; " +
    "in headless mode it writes to stderr and the trace. Does not block or pause the agent.";

  protected override async Task<NotifyCallerOutput> ExecuteCoreAsync(
    NotifyCallerInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    if (ctx.CallerIo is null)
    {
      throw new InvalidOperationException(
        "notify_caller requires an ICallerIo transport — none is wired in this run context. " +
        "Run `forge agent --callers auto-deny` (or a terminal session) to enable caller-IO tools.");
    }

    var level = Enum.TryParse<NoticeLevel>(input.Level, ignoreCase: true, out var parsed)
      ? parsed
      : NoticeLevel.Info;

    var notice = new CallerNotice
    {
      Level = level,
      Summary = input.Summary,
      Detail = input.Detail
    };

    ctx.Trace.Trace(new CallerNotifyEvent
    {
      Iteration = ctx.IterationIndex ?? 0,
      Level = level.ToString(),
      SummaryLength = input.Summary.Length,
      DetailLength = input.Detail?.Length ?? 0
    });

    await ctx.CallerIo.NotifyAsync(notice, cancellationToken).ConfigureAwait(false);

    return new NotifyCallerOutput { Delivered = true };
  }
}
