using System.Text.Json.Nodes;
using Forge.Core.Exceptions;

namespace Forge.Agent.Compaction;

/// <summary>
/// Asserts structural invariants on the messages list after a compaction
/// transformation. On violation, throws <see cref="AgentCompactionInvariantException"/>
/// and the caller proceeds with the uncompacted messages (fail-closed).
/// </summary>
internal static class PairingInvariant
{
  /// <summary>
  /// Synthetic tool name emitted by the <c>summarise</c> strategy. Allowed only
  /// when the analyser recognises the pair was produced by compaction, never
  /// by the agent itself. A user tool with this name would be rejected.
  /// </summary>
  internal const string SyntheticToolName = "__compaction_summary__";

  public static void Check(IReadOnlyList<JsonNode> messages)
  {
    if (messages.Count < 2)
    {
      throw new AgentCompactionInvariantException(
        $"Messages list has {messages.Count} message(s); expected at least 2 (system + root user).");
    }

    var role0 = GetRole(messages[0]);
    if (role0 != "system")
    {
      throw new AgentCompactionInvariantException($"Messages[0] has role '{role0}'; expected 'system'.");
    }
    var role1 = GetRole(messages[1]);
    if (role1 != "user")
    {
      throw new AgentCompactionInvariantException($"Messages[1] has role '{role1}'; expected 'user'.");
    }

    // Single pass: collect every assistant tool-call id and every tool result id,
    // plus the index of the last assistant (mid-iteration assistants don't need
    // paired tool results yet).
    var declaredCallIds = new HashSet<string>();
    var satisfiedCallIds = new HashSet<string>();
    var lastAssistantIdx = -1;

    for (var i = 0; i < messages.Count; i++)
    {
      var role = GetRole(messages[i]);
      if (role == "assistant")
      {
        lastAssistantIdx = i;
        if (messages[i]?["tool_calls"] is JsonArray calls)
        {
          foreach (var call in calls)
          {
            var id = call?["id"]?.GetValue<string>();
            if (id is not null) declaredCallIds.Add(id);
          }
        }
      }
      else if (role == "tool")
      {
        var callId = messages[i]?["tool_call_id"]?.GetValue<string>();
        if (callId is not null) satisfiedCallIds.Add(callId);
      }
    }

    // Invariant 1: every tool message's tool_call_id must have been declared.
    for (var i = 0; i < messages.Count; i++)
    {
      if (GetRole(messages[i]) != "tool") continue;
      var callId = messages[i]?["tool_call_id"]?.GetValue<string>();
      if (callId is not null && !declaredCallIds.Contains(callId))
      {
        throw new AgentCompactionInvariantException(
          $"Tool message at index {i} references tool_call_id '{callId}' which has no matching assistant tool_calls entry.");
      }
    }

    // Invariant 2: every non-terminal assistant's declared tool_call must have
    // a matching tool result somewhere in the list. The final assistant is
    // exempt — it may be mid-iteration.
    for (var i = 0; i < messages.Count; i++)
    {
      if (i == lastAssistantIdx) continue;
      if (GetRole(messages[i]) != "assistant") continue;
      if (messages[i]?["tool_calls"] is not JsonArray calls || calls.Count == 0) continue;

      // The synthetic __compaction_summary__ tool call is exempt from the
      // satisfaction check — the paired tool result carries the summary data
      // but the "tool name" is namespaced and never actually invoked.
      var hasOnlySyntheticCalls = calls.All(c => c?["function"]?["name"]?.GetValue<string>() == SyntheticToolName);
      if (hasOnlySyntheticCalls) continue;

      foreach (var call in calls)
      {
        var id = call?["id"]?.GetValue<string>();
        if (id is not null && !satisfiedCallIds.Contains(id))
        {
          throw new AgentCompactionInvariantException(
            $"Assistant at index {i} declares tool_call '{id}' but no matching tool message exists after it.");
        }
      }
    }
  }

  private static string GetRole(JsonNode? node) => node?["role"]?.GetValue<string>() ?? "";
}
