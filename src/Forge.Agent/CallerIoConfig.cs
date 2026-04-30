namespace Forge.Agent;

/// <summary>
/// Budget and behaviour knobs for caller-IO tools (<c>ask_caller</c>,
/// <c>notify_caller</c>, <c>request_approval</c>). Counters persist in
/// <c>state.json</c> and resume across <c>forge resume</c>.
/// </summary>
public sealed class CallerIoConfig
{
  /// <summary>Maximum <c>ask_caller</c> calls per agent run. Default 5.</summary>
  public int MaxPrompts { get; init; } = 5;

  /// <summary>Maximum <c>notify_caller</c> calls per agent run. Default 50.</summary>
  public int MaxNotifications { get; init; } = 50;

  /// <summary>Maximum <c>request_approval</c> calls per agent run. Default 10.</summary>
  public int MaxApprovals { get; init; } = 10;

  /// <summary>
  /// What to do when a budget is exceeded: <c>"error"</c> (tool returns structured
  /// error to the agent) or <c>"silent"</c> (tool returns a stub success).
  /// Default <c>"error"</c>.
  /// </summary>
  public string OnBudgetExceeded { get; init; } = "error";
}

/// <summary>
/// Mutable budget counters persisted in <c>state.json</c> alongside messages
/// and the write ledger. Resumed runs continue from the persisted counter;
/// they do NOT reset.
/// </summary>
public sealed class CallerIoBudget
{
  public int PromptsUsed { get; set; }
  public int NotificationsUsed { get; set; }
  public int ApprovalsUsed { get; set; }
}
