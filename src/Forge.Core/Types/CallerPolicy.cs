namespace Forge.Core.Types;

/// <summary>
/// How a headless caller handles <c>ask_caller</c> prompts.
/// </summary>
public enum PromptBehavior
{
  /// <summary>
  /// Write <c>pending_question.json</c>, throw <see cref="Exceptions.CallerDeferredException"/>,
  /// and let the pipeline transition to <c>needs_caller</c>. Resumable with
  /// <c>forge resume --answer</c>.
  /// </summary>
  Defer,

  /// <summary>
  /// Immediately throw <see cref="Exceptions.AgentException"/> — the run fails.
  /// </summary>
  FailFast,

  /// <summary>
  /// Return an empty string response without blocking. Use with care; the agent
  /// may not handle an empty answer well.
  /// </summary>
  SilentEmpty
}

/// <summary>
/// How a headless caller handles <c>request_approval</c> calls.
/// </summary>
public enum ApprovalBehavior
{
  /// <summary>Deny all approval requests not explicitly allowed per-action.</summary>
  AutoDeny,

  /// <summary>
  /// Allow all approval requests except <c>force_push</c> (which stays deny).
  /// Explicit <c>PerAction</c> entries override.
  /// </summary>
  AutoAllow,

  /// <summary>Resolve per <see cref="CallerPolicy.PerAction"/> dictionary.</summary>
  PerAction
}

/// <summary>
/// Policy that controls how non-interactive (headless) callers respond to
/// <c>ask_caller</c>, <c>notify_caller</c>, and <c>request_approval</c>.
/// Loaded from <c>--callers</c> CLI flag, <c>~/.forge/callers.json</c>, or
/// built-in defaults.
/// </summary>
public sealed class CallerPolicy
{
  /// <summary>Default policy applied when no CLI flag or config file is present.</summary>
  public static readonly CallerPolicy Default = new()
  {
    OnPrompt = PromptBehavior.Defer,
    OnApproval = ApprovalBehavior.AutoDeny,
    EmitNotificationsToStderr = true
  };

  /// <summary>
  /// Preset: <c>--callers auto-allow</c>. Prompts fail-fast; approvals auto-allowed
  /// (except force_push).
  /// </summary>
  public static readonly CallerPolicy AutoAllow = new()
  {
    OnPrompt = PromptBehavior.FailFast,
    OnApproval = ApprovalBehavior.AutoAllow,
    EmitNotificationsToStderr = true
  };

  /// <summary>
  /// Preset: <c>--callers auto-deny</c>. Prompts deferred; approvals auto-denied.
  /// </summary>
  public static readonly CallerPolicy AutoDeny = new()
  {
    OnPrompt = PromptBehavior.Defer,
    OnApproval = ApprovalBehavior.AutoDeny,
    EmitNotificationsToStderr = true
  };

  /// <summary>
  /// Preset: <c>--callers fail-fast</c>. Prompts and approvals both fail-fast.
  /// </summary>
  public static readonly CallerPolicy FailFast = new()
  {
    OnPrompt = PromptBehavior.FailFast,
    OnApproval = ApprovalBehavior.AutoDeny,
    EmitNotificationsToStderr = true
  };

  /// <summary>How to handle <c>ask_caller</c> prompts.</summary>
  public PromptBehavior OnPrompt { get; init; } = PromptBehavior.Defer;

  /// <summary>How to handle <c>request_approval</c> requests.</summary>
  public ApprovalBehavior OnApproval { get; init; } = ApprovalBehavior.AutoDeny;

  /// <summary>
  /// Per-action allow/deny map. Keys are action names (e.g. "delete_files").
  /// Only consulted when <see cref="OnApproval"/> is <see cref="ApprovalBehavior.PerAction"/>.
  /// </summary>
  public IReadOnlyDictionary<string, bool> PerAction { get; init; } =
    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// When true, <c>notify_caller</c> messages are written to stderr in addition
  /// to the trace stream. Warn and Error levels always emit regardless of this flag.
  /// </summary>
  public bool EmitNotificationsToStderr { get; init; } = true;

  /// <summary>
  /// Actions that are always denied regardless of <see cref="OnApproval"/>.
  /// </summary>
  private static readonly HashSet<string> AlwaysDenyActions = new(StringComparer.OrdinalIgnoreCase)
  {
    "force_push"
  };

  /// <summary>
  /// Resolve whether an action is allowed under this policy.
  /// </summary>
  public ApprovalDecision ResolveApproval(string action)
  {
    // force_push is always denied regardless of policy
    if (AlwaysDenyActions.Contains(action))
    {
      return new ApprovalDecision { Allowed = false, Reason = "auto-deny (policy: force_push is always denied)" };
    }

    switch (OnApproval)
    {
      case ApprovalBehavior.AutoDeny:
        return new ApprovalDecision { Allowed = false, Reason = "auto-deny (policy)" };

      case ApprovalBehavior.AutoAllow:
        return new ApprovalDecision { Allowed = true, Reason = "auto-allow (policy)" };

      case ApprovalBehavior.PerAction:
        if (PerAction.TryGetValue(action, out var allowed))
        {
          return new ApprovalDecision
          {
            Allowed = allowed,
            Reason = allowed ? $"per-action: allow ({action})" : $"per-action: deny ({action})"
          };
        }
        // Unknown actions default to deny under PerAction
        return new ApprovalDecision { Allowed = false, Reason = $"per-action: deny (unknown action '{action}')" };

      default:
        return new ApprovalDecision { Allowed = false, Reason = "auto-deny (policy)" };
    }
  }
}
