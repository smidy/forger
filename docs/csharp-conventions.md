---
layer: (root)
title: C# conventions
updated: 2026-04-23
code_refs: []
related: [index.md]
---

# C# conventions

Project-wide code-shape rules. `CLAUDE.md` delegates here so it stays lean; add new conventions here rather than bloating the agent prompt.

## Types

- **Data-shaped types → `sealed record class`.** Configs, DTOs, trace events, and specs that only carry auto-properties should be records. Records give you `with` expressions for defensive copies, value-based equality, and a readable `ToString()` for free. Plain `sealed class` is reserved for types that carry behavior or need reference-identity semantics.

  *Example of the cost when you get this wrong*: `BashConfig` (`src/Forge.Core/Config/BashConfig.cs`) started as a `sealed class`. When the Docker Desktop `--storage-opt` retry path landed, the lifecycle needed `config with { StorageOpt = "" }` to build a defensive copy — forcing a mid-plan conversion to `sealed record class` that could have been set up up-front.

- **`required` + `init` for mandatory fields.** Every field a type can't construct without must be `required`, and every property must be `init`-only. Let the compiler enforce construction shape; don't write constructor overloads for "partial construction."

(Add further conventions here as they come up.)
