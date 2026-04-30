---
recipe: add-built-in-tool
audience: code-writer
estimated_reads: 2 docs
task: Add a new built-in tool (ToolBase<TIn, TOut>) to Forge
updated: 2026-04-29
---

# Add a built-in tool

## Read first
- `Domain/tools.md` — `ITool` contract, `ToolBase` pattern, `submit_final`, registration

## Optional reading
- `Application/AgentRunner.md` — how tools are invoked in the loop; result capping
- `Domain/runs.md` — `ToolContext` members (`ctx.Trace`, `ctx.RunWorkspace`, `ctx.WriteLedger`)

## Touch
- `src/Forge.Tools/<NewTool>.cs` — new class `class NewTool : ToolBase<NewInput, NewOutput>`
- `src/Forge.Tools/BuiltInToolsRegistration.cs` — `services.AddSingleton<NewTool>()` **and** `r.Register(sp.GetRequiredService<NewTool>())`

## Verify
- `dotnet build Forge.sln` — zero warnings (`TreatWarningsAsErrors=true`)
- `dotnet test Forge.sln` — passes

## Common pitfalls
- Forgetting one of the two registration sites — tool becomes DI-resolvable but invisible to agents.
- Using `System.Text.Json` defaults — use `JsonSerializationDefaults.CamelCaseTool` for tool I/O (inherited by `ToolBase`, don't override).
- Writing log-like text to stdout from the tool — stdout is reserved for JSON results. Use `ctx.Logger` (stderr).
- NEVER reuse the name `submit_final` — `AgentRunner` short-circuits on it before the registry is consulted.
- Filesystem work belongs in the agent's `bash:` config + `bash` tool, not a custom file-touching ITool. Forge no longer ships read/write/glob/grep/patch tools.
