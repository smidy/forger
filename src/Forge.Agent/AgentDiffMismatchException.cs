using Forge.Core.Exceptions;

namespace Forge.Agent;

/// <summary>
/// Thrown by <c>AgentRunner</c> when the <c>files_modified</c> list in the
/// agent's <c>submit_final</c> payload diverges from the write ledger. The
/// run does not write <c>result.json</c>; the exception bubbles up through
/// the same path that surface schema-validation failures.
/// </summary>
public sealed class AgentDiffMismatchException : ForgeException
{
  public IReadOnlyList<string> Missing { get; }
  public IReadOnlyList<string> Extra { get; }

  public AgentDiffMismatchException(IReadOnlyList<string> missing, IReadOnlyList<string> extra)
    : base(BuildMessage(missing, extra))
  {
    Missing = missing;
    Extra = extra;
  }

  private static string BuildMessage(IReadOnlyList<string> missing, IReadOnlyList<string> extra)
  {
    var parts = new List<string>();
    if (missing.Count > 0)
    {
      parts.Add($"declared but never written: [{string.Join(", ", missing)}]");
    }

    if (extra.Count > 0)
    {
      parts.Add($"written but not declared: [{string.Join(", ", extra)}]");
    }

    var detail = parts.Count == 0 ? "no details" : string.Join("; ", parts);
    return $"submit_final `files_modified` does not match the write ledger: {detail}.";
  }
}
