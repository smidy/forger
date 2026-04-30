---
layer: Infrastructure
title: Plugin and skill discovery
updated: 2026-04-29
code_refs: [src/Forge.Pipeline/PluginPaths.cs, src/Forge.Agent/SkillScanner.cs, src/Forge.Agent/SkillCatalog.cs, src/Forge.Core/Filesystem/SkillDirectoryPaths.cs]
related: [Infrastructure/project-context.md, Domain/agents.md]
---

# Plugin and skill discovery

## Agent discovery order

`PluginPaths.SearchRoots` yields, in order:

1. `<cwd>/.forge/`
2. `<UserProfile>/.config/forge/`
3. `<forgeHome>` (default `~/.forge/`)

`FindAgent(name)` returns the first existing `<root>/agents/<name>.agent.yaml`. **First match wins** — later roots never shadow earlier ones.

Forge no longer discovers pipelines: there is no user-facing pipeline surface, so no `pipelines/<name>.pipeline.yaml` is searched.

## Skill discovery (Agent Skills)

`SkillScanner.Discover` scans four roots in precedence order (later entries **overwrite** earlier ones on name collision):

1. `~/.agents/skills/`
2. `~/.claude/skills/`
3. `<cwd>/.agents/skills/`
4. `<cwd>/.claude/skills/`

For each subdirectory with a `SKILL.md`, the frontmatter `name` + `description` are parsed via `YamlFront`. On parse failure the skill is skipped and a `skill_frontmatter_invalid` trace event is emitted.

## Skills catalog injection

If `agent.inject_skills_catalog` is true (default), `SkillCatalog.Build` formats the discovered entries into a system-prompt appendix containing `name` + `description` only — not full `SKILL.md` bodies. Agents that need the full body invoke `bash` against a host path that includes the skill directory.

## Adding a new plugin root

Edit `PluginPaths.SearchRoots` — **before** existing entries to shadow, **after** to act as fallback. The order IS the contract; don't reorder casually.
