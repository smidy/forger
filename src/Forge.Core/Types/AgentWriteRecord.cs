namespace Forge.Core.Types;

/// <summary>
/// One authoritative write attributed to an agent tool call. The agent runner
/// reconciles this ledger against the <c>files_modified</c> claim in the
/// <c>submit_final</c> payload to catch drift between what the agent says it
/// changed and what actually landed on disk.
/// </summary>
/// <param name="ToolName">Tool that produced the write (e.g. <c>write_file</c>, <c>apply_patch</c>).</param>
/// <param name="RequestedPath">Path as the agent supplied it — relative or absolute, unresolved.</param>
/// <param name="ResolvedPath">Canonical absolute path the tool actually touched.</param>
/// <param name="RootCategory">One of <c>run-workspace</c> / <c>repo-root</c> / <c>other</c>.</param>
/// <param name="BytesWritten">Bytes written for <c>write_file</c>; for <c>apply_patch</c> the post-patch file size, or 0 for deletes.</param>
/// <param name="WasNoOp"><c>true</c> when the write produced no content change (e.g. an <c>apply_patch</c> whose hunks left the buffer identical).</param>
public sealed record AgentWriteRecord(
  string ToolName,
  string RequestedPath,
  string ResolvedPath,
  string RootCategory,
  long BytesWritten,
  bool WasNoOp);
