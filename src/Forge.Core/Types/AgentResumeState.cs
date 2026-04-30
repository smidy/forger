using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Forge.Core.Types;

/// <summary>
/// Immutable snapshot of an agent's internal state at the end of an iteration.
/// Used to resume a stage that previously exhausted <c>max_iterations</c>.
/// </summary>
public sealed class AgentResumeState
{
    /// <summary>
    /// The iteration index to resume the loop with (i.e., the next iteration
    /// after the snapshot). If the snapshot was written at the end of iteration 39,
    /// this value should be 40.
    /// </summary>
    public required int StartingIter { get; init; }

    /// <summary>
    /// The conversation history up to and including the snapshot iteration.
    /// Includes the system and user preamble, all assistant messages, and all
    /// tool results. This list is authoritative; on resume we skip templating
    /// and seed the agent's message list directly from this collection.
    /// </summary>
    public required List<JsonNode> Messages { get; init; }

    /// <summary>
    /// Whether the agent has already been nudged (the one-shot "you must call tools"
    /// prod). Carried forward so we do not nudge again on resume.
    /// </summary>
    public required bool Nudged { get; init; }

    /// <summary>
    /// Cumulative write ledger entries recorded up to this snapshot.
    /// The resumed agent must re-create a fresh <c>AgentWriteLedger</c> and
    /// seed it with these entries in order, preserving the cumulative write
    /// history for the final <c>submit_final</c> diff verification.
    /// </summary>
    public required IReadOnlyList<AgentWriteRecord> LedgerEntries { get; init; }
}