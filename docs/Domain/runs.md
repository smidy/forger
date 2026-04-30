---
layer: Domain
title: Runs
updated: 2026-04-29
code_refs: [src/Forge.Core/Types/RunState.cs, src/Forge.Core/Types/RuntimeContext.cs, src/Forge.Core/Workspace/RunIdGenerator.cs, src/Forge.Core/Workspace/WorkspacePaths.cs]
related: [Data/workspace.md, Application/AgentRunner.md]
---

# Runs

A run is a single invocation of an agent (`forge agent <name>` or its resume). It has a unique id, a workspace directory on disk, and a `RunState` in memory. There is no user-facing pipeline-of-stages run anymore; an agent run is a single-stage execution under stage id `agent`.

## Run id

Format: `<slug>-<yyyyMMdd-HHmmss>-<8 hex>` where `slug` is the agent name with non-alphanumeric characters replaced by `-`. See `RunIdGenerator.Generate`.

## RunState (in-memory)

```csharp
public sealed class RunState
{
  public required JsonNode Input { get; set; }
  public Dictionary<string, StageResult> Stages { get; }
  public required RuntimeContext Runtime { get; set; }
  public JsonNode AsStateJson();
}
```

`AsStateJson()` returns the unified document against which Scriban templates (`{{ … }}`) are resolved:

```json
{
  "input":   <agent input>,
  "stages":  { "agent": { "output": <agent result> } },
  "runtime": { "run_id", "workspace", "stage_dir" }
}
```

The `Stages` dictionary always has at most one entry (`agent`) for an agent run.

## Workspace layout (on disk)

Everything under `WorkspacePaths.RunRoot(forgeHome, runId)` → `{forgeHome}/runs/{runId}/`. See [Data/workspace.md](../Data/workspace.md) for the full file inventory.
