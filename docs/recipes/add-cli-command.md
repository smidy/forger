---
recipe: add-cli-command
audience: code-writer
estimated_reads: 2 docs
task: Add a new Spectre.Console CLI command to Forge
updated: 2026-04-21
---

# Add a CLI command

## Read first
- `Presentation/cli.md` — existing commands, flag conventions, output-format contract
- `Infrastructure/logging.md` — **stdout = JSON results, stderr = logs** (CRITICAL)

## Optional reading
- `Domain/runs.md` — if the command triggers a run (run id, workspace paths)

## Touch
- `src/Forge.Cli/Commands/<Name>Command.cs` — `internal class <Name>Command : AsyncCommand<Settings>`
- `src/Forge.Cli/Program.cs` — register the command in the Spectre app builder
- (optional) separate settings class for complex flag sets

## Verify
- `dotnet build Forge.sln` — zero warnings
- `dotnet run --project src/Forge.Cli -- <name> --help` — help text renders
- `dotnet run --project src/Forge.Cli -- <name> …` — stdout is JSON only; logs on stderr
- Command types must be `internal` — there is no public CLI API

## Common pitfalls
- `Console.WriteLine` for log-like text — logging lives on stderr. Use `ILogger`.
- Forgetting to wire the command in `Program.cs` — Spectre will not discover it.
- Public command types leaking internals — keep them `internal`.
- Chaining PowerShell examples in docs with `&&` — PowerShell doesn't support it; use `;` or separate lines.
