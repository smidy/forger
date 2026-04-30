---
recipe: add-trace-event
audience: code-writer
estimated_reads: 1 doc
task: Add a new trace event type (TraceEvent record) to Forge
updated: 2026-04-21
---

# Add a trace event type

## Read first
- `Infrastructure/logging.md` — stream contract, event discipline, existing event kinds

## Optional reading
- `Data/workspace.md` — where `trace.jsonl` lands, atomic writes, retention

## Touch
- `src/Forge.Core/Trace/TraceEvent.cs` — add `public sealed record <Name>Event : TraceEvent`:
  - `override string Kind => "<snake_case_name>";`
  - `required` init-only properties for every payload field
- The call-site(s) that emit the event: `trace.Trace(new <Name>Event { … })`

## Verify
- `dotnet build Forge.sln` — zero warnings
- Run a pipeline that triggers the event; inspect `trace.jsonl` — entry appears with the correct `kind` and all required fields populated
- JSON uses camelCase (`JsonSerializationDefaults.Trace`) — visually verify the rendered line

## Common pitfalls
- Reusing `GenericTraceEvent` instead of a dedicated record — schema drift, painful for consumers.
- Omitting `required` on payload fields — silent nulls when a caller forgets to set them.
- PascalCase in the `Kind` string — convention is `snake_case` to match the existing event vocabulary.
