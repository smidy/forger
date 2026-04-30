---
layer: adr
title: "002 JSON at every boundary"
updated: 2026-04-21
code_refs: [src/Forge.Core/Json/, src/Forge.Pipeline/PipelineExecutor.cs, src/Forge.Cli/Program.cs]
related: [Infrastructure/logging.md, Data/workspace.md]
---

# 002 JSON at every boundary

## Status
Accepted

## Context
Forge must compose cleanly with shells, CI jobs, and other agent runners (possibly other Forge instances). Mixed output (text + JSON, tables + machine output) forces callers to parse heuristically and couples them to version-specific layouts.

## Decision
All boundaries emit JSON: stdin (agent/pipeline inputs), stdout (structured results), run artifacts (`input.json`, `plan.json`, `result.json`, `status.json`), tool I/O (validated against JSON Schema), trace events (`trace.jsonl`). Logs go to stderr only. All agent/pipeline/tool configs are YAML parsed to `JsonNode` via `YamlFront.ParseToJson` — the in-memory shape is JSON everywhere.

## Consequences
- Shell pipelines (`forge ... | jq ...`) work without special flags.
- Another Forge instance can invoke `forge agent …`, read stdout, and parse mechanically.
- Human-readable output is a separate concern (`--human` flag, tracked in `plans/P6-cli-polish.md`) — the JSON contract is primary.
- Every field added to a run artifact becomes part of a stable contract; breaking changes require a version bump.
