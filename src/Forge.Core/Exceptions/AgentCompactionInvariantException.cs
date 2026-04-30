using Forge.Core.Exceptions;

namespace Forge.Core.Exceptions;

/// <summary>
/// Thrown when a compaction transformation violates a pairing or structural
/// invariant on the messages list. The caller (<c>AgentRunner</c>) catches
/// this, logs a warning, emits a <c>CompactionSkippedEvent</c>, and proceeds
/// with the uncompacted messages for this iteration — fail-closed behaviour
/// ensures a broken compaction never leaves the agent worse off than doing
/// nothing.
/// </summary>
public sealed class AgentCompactionInvariantException : ForgeException
{
  public AgentCompactionInvariantException(string message) : base(message) { }
}
