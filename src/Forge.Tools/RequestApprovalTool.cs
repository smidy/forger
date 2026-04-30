using System.Text.Json.Nodes;
using Forge.Core.Trace;
using Forge.Core.Types;

namespace Forge.Tools;

public sealed class RequestApprovalInput
{
  public required string Action { get; init; }
  public required string Summary { get; init; }

  /// <summary>"low" | "medium" | "high"</summary>
  public string? Risk { get; init; }

  /// <summary>Optional tool-specific structured context.</summary>
  public JsonNode? Context { get; init; }
}

public sealed class RequestApprovalOutput
{
  public required bool Allowed { get; init; }
  public string? Reason { get; init; }
}

public sealed class RequestApprovalTool : ToolBase<RequestApprovalInput, RequestApprovalOutput>
{
  public override string Name => "request_approval";
  public override string Description =>
    "Request caller approval for a guarded action. In interactive mode shows a yes/no prompt; " +
    "in headless mode consults the caller policy (auto-allow / auto-deny / per-action).";

  protected override async Task<RequestApprovalOutput> ExecuteCoreAsync(
    RequestApprovalInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    if (ctx.CallerIo is null)
    {
      throw new InvalidOperationException(
        "request_approval requires an ICallerIo transport — none is wired in this run context. " +
        "Run `forge agent --callers auto-deny` (or a terminal session) to enable caller-IO tools.");
    }

    var risk = Enum.TryParse<RiskLevel>(input.Risk, ignoreCase: true, out var parsed)
      ? parsed
      : RiskLevel.Unknown;

    IReadOnlyDictionary<string, JsonNode>? context = null;
    if (input.Context is JsonObject obj)
    {
      var dict = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
      foreach (var kv in obj)
      {
        if (kv.Value is not null)
        {
          dict[kv.Key] = kv.Value.DeepClone();
        }
      }
      context = dict;
    }

    var request = new ApprovalRequest
    {
      Action = input.Action,
      Summary = input.Summary,
      Risk = risk,
      Context = context
    };

    var decision = await ctx.CallerIo.RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false);

    ctx.Trace.Trace(new CallerApprovalEvent
    {
      Iteration = ctx.IterationIndex ?? 0,
      Action = input.Action,
      Risk = risk.ToString(),
      Allowed = decision.Allowed,
      DecisionReason = decision.Reason ?? "unknown"
    });

    return new RequestApprovalOutput
    {
      Allowed = decision.Allowed,
      Reason = decision.Reason
    };
  }
}
