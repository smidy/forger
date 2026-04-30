using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Types;
using Forge.Core.Workspace;

namespace Forge.Cli;

/// <summary>
/// <see cref="ICallerIo"/> implementation for headless (pipeline / CI) runs.
/// Prompts are deferred or fail-fast per <see cref="CallerPolicy"/>; approvals
/// are resolved via policy; notifications write to stderr.
/// Chat-mode surface methods are no-ops.
/// </summary>
internal sealed class HeadlessCallerIo : ICallerIo
{
  private readonly CallerPolicy _policy;
  private readonly string _stageDir;

  public HeadlessCallerIo(CallerPolicy policy, string stageDir)
  {
    _policy = policy;
    _stageDir = stageDir;
  }

  public Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct)
  {
    switch (_policy.OnPrompt)
    {
      case PromptBehavior.FailFast:
        throw new AgentException("caller_io prompt in fail-fast policy: the agent called ask_caller but the caller is not interactive.");

      case PromptBehavior.SilentEmpty:
        return Task.FromResult(new CallerPromptResponse { Response = string.Empty });

      case PromptBehavior.Defer:
      default:
        return DeferPromptAsync(prompt);
    }
  }

  private Task<CallerPromptResponse> DeferPromptAsync(CallerPrompt prompt)
  {
    var pending = new JsonObject
    {
      ["question"] = prompt.Question,
      ["sourceToolName"] = prompt.SourceToolName ?? "ask_caller",
      ["captured_at"] = DateTimeOffset.UtcNow.ToString("o")
    };

    if (prompt.Hint is not null)
    {
      pending["hint"] = prompt.Hint;
    }

    if (prompt.Choices is { Count: > 0 })
    {
      var choicesArr = new JsonArray();
      foreach (var c in prompt.Choices)
      {
        choicesArr.Add(c);
      }
      pending["choices"] = choicesArr;
    }

    var pendingJson = pending.ToJsonString();
    var pendingPath = WorkspacePaths.PendingQuestionPath(_stageDir);
    var dir = Path.GetDirectoryName(pendingPath);
    if (dir is not null)
    {
      Directory.CreateDirectory(dir);
    }
    File.WriteAllText(pendingPath, pendingJson);

    throw new CallerDeferredException(pendingJson, _stageDir, null);
  }

  public Task NotifyAsync(CallerNotice notice, CancellationToken ct)
  {
    // Write to stderr when enabled (warn/error always emit)
    var shouldEmit = _policy.EmitNotificationsToStderr || notice.Level is NoticeLevel.Warn or NoticeLevel.Error;
    if (shouldEmit)
    {
      var line = $"[notify:{notice.Level.ToString().ToLowerInvariant()}] {notice.Summary}";
      Console.Error.WriteLine(line);
    }

    return Task.CompletedTask;
  }

  public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
  {
    var decision = _policy.ResolveApproval(request.Action);
    return Task.FromResult(decision);
  }

  public Task WriteAssistantTextAsync(string text, CancellationToken ct) => Task.CompletedTask;
  public Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct) => Task.CompletedTask;
  public Task WriteSystemNoticeAsync(string notice, CancellationToken ct) => Task.CompletedTask;
  public string? TryTakeUserInterjection() => null;
}
