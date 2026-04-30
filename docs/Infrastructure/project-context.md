---
layer: Infrastructure
title: Project context injection
updated: 2026-04-29
code_refs: [src/Forge.Core/Filesystem/ProjectMarkdownLoader.cs, src/Forge.Core/Filesystem/ProjectContextPaths.cs, src/Forge.Core/Filesystem/SkillDirectoryPaths.cs]
related: [Infrastructure/plugins.md, Domain/agents.md, Application/AgentRunner.md]
---

# Project context injection

When `agent.inject_project_context` is true (default), `AgentRunner` prepends `AGENTS.md` and/or `CLAUDE.md` from an ordered list of roots to the agent's system prompt before the agent's own `system_prompt` template is appended.

## Root order (`ProjectContextPaths.GetOrderedRoots`)

1. `<cwd>` — where the user ran `forge`
2. Agent YAML's directory (when the agent was loaded from a file)
3. Each entry in `agent.project_context_roots` (YAML-declared extras)

## Load rules (`ProjectMarkdownLoader.LoadOrderedRoots`)

Within each root, in order:
- `AGENTS.md` is loaded first, then `CLAUDE.md`.
- Per-file hard cap: `DefaultMaxBytesPerFile = 256 KiB`. Truncation emits a `project_markdown_truncated` trace event with path and cap.
- Files within a root are joined with `\n\n---\n\n`; roots are joined with `\n\n========\n\n`.
- Missing files are silently skipped.

## Skill-directory roots

`SkillDirectoryPaths.GetDefaultSkillRoots` returns the canonical list used by `SkillScanner` (for the catalog). When adding a new skill root, update this helper.

## Disabling

Set in agent YAML:
- `inject_project_context: false` — skip `AGENTS.md` / `CLAUDE.md` loading entirely.
- `inject_skills_catalog: false` — skip the skills appendix.

Disable per agent when the agent should run "clean" (e.g. self-contained evaluation harnesses, reproducibility fixtures).
