using Forge.Core.Types;

namespace Forge.Cli;

/// <summary>
/// Wraps a <see cref="HeadlessCallerIo"/> and returns a pre-baked answer on the
/// first <c>PromptAsync</c> or <c>RequestApprovalAsync</c> call. Subsequent
/// caller-IO calls delegate to the inner <see cref="HeadlessCallerIo"/>.
/// Used by <c>forge resume --answer</c> to inject the operator-supplied answer
/// without modifying the resume path.
/// </summary>
internal sealed class PreAnsweredCallerIo : ICallerIo
{
  private readonly ICallerIo _inner;
  private readonly string? _promptResponse;
  private readonly ApprovalDecision? _approvalDecision;
  private bool _promptUsed;
  private bool _approvalUsed;

  /// <summary>
  /// Create a <c>PreAnsweredCallerIo</c> for an <c>ask_caller</c> answer.
  /// </summary>
  public PreAnsweredCallerIo(ICallerIo inner, string response)
  {
    _inner = inner;
    _promptResponse = response;
  }

  /// <summary>
  /// Create a <c>PreAnsweredCallerIo</c> for a <c>request_approval</c> answer.
  /// </summary>
  public PreAnsweredCallerIo(ICallerIo inner, ApprovalDecision decision)
  {
    _inner = inner;
    _approvalDecision = decision;
  }

  public Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct)
  {
    if (!_promptUsed && _promptResponse is not null)
    {
      _promptUsed = true;
      return Task.FromResult(new CallerPromptResponse { Response = _promptResponse });
    }

    return _inner.PromptAsync(prompt, ct);
  }

  public Task NotifyAsync(CallerNotice notice, CancellationToken ct) => _inner.NotifyAsync(notice, ct);

  public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
  {
    if (!_approvalUsed && _approvalDecision is not null)
    {
      _approvalUsed = true;
      return Task.FromResult(_approvalDecision);
    }

    return _inner.RequestApprovalAsync(request, ct);
  }

  public Task WriteAssistantTextAsync(string text, CancellationToken ct) => _inner.WriteAssistantTextAsync(text, ct);
  public Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct)
    => _inner.WriteToolCallSummaryAsync(iteration, toolName, summary, isError, ct);
  public Task WriteSystemNoticeAsync(string notice, CancellationToken ct) => _inner.WriteSystemNoticeAsync(notice, ct);
  public string? TryTakeUserInterjection() => _inner.TryTakeUserInterjection();
}
