using Forge.Core.Llm;
using Forge.Core.Trace;
using Microsoft.Extensions.Logging;

namespace Forge.Core.Types;

public sealed record ToolContext(
  string RunId,
  string RunWorkspace,
  string StageDir,
  string StageId,
  int? IterationIndex,
  ILlmClient Llm,
  ITraceSink Trace,
  ILogger Logger,
  CancellationToken CancellationToken,
  Func<int> NextToolOutputIdx)
{
  public string Workspace => RunWorkspace;

  /// <summary>
  /// Per-run ledger of authoritative tool writes. Set by <c>AgentRunner</c>
  /// when it enters its loop so <c>write_file</c> and <c>apply_patch</c> can
  /// append entries for post-loop diff verification. Null for callers outside
  /// the agent loop (pipeline-only tool contexts, unit tests) — tools must
  /// null-check before recording.
  /// </summary>
  public AgentWriteLedger? WriteLedger { get; init; }

  /// <summary>
  /// The caller-IO transport wired for this run. Null when no <c>--callers</c>
  /// flag was supplied or when running <c>forge tool …</c>. Tools that need
  /// caller interaction (<c>ask_caller</c>, <c>notify_caller</c>,
  /// <c>request_approval</c>) must null-check and return a structured error
  /// when this is null.
  /// </summary>
  public ICallerIo? CallerIo { get; init; }
}
