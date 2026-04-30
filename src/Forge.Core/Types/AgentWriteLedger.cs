namespace Forge.Core.Types;

/// <summary>
/// Append-only ledger of authoritative writes emitted by agent tool calls.
/// <see cref="Forge.Core.Types.ToolContext"/> carries it; <c>AgentRunner</c>
/// creates one per run and reconciles the entries against the final
/// <c>files_modified</c> claim at <c>submit_final</c>.
/// </summary>
/// <remarks>
/// Tool calls inside the agent loop are serial, so no synchronisation is
/// needed. Tools should call <see cref="Record"/> only after the physical
/// write has succeeded — e.g. <c>apply_patch</c> emits ledger entries after
/// the atomic apply block finishes, never from the rollback path.
/// </remarks>
public sealed class AgentWriteLedger
{
  private readonly List<AgentWriteRecord> _entries = new();

  /// <summary>Ordered view of every write recorded against this ledger.</summary>
  public IReadOnlyList<AgentWriteRecord> Entries => _entries;

  /// <summary>Append one record. Intended for tool code.</summary>
  public void Record(AgentWriteRecord entry) => _entries.Add(entry);
}
