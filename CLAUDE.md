# Forge — guidance for Claude

Forge is a .NET 9 CLI running tool-using LLM agents against an
OpenAI-compatible (LiteLLM) endpoint. **This file is deliberately
minimal** — topic-specific guidance (architecture, contracts, conventions,
commands, recipes) lives in `docs/`, not here. Read the docs every turn;
see the Context protocol below.

## Audience — AI agents, not humans

All project documentation (`docs/**`, `README.md`, learnings, recipes)
**and in-session responses in this project** are consumed primarily by
AI agents. Optimize every artifact for agent consumption:

- **Dense, factual, verifiable.** Skip narrative framing, hedging, and
  throat-clearing ("in this section we will…", "it's worth noting…"). Start
  with the payload.
- **Structured over prose.** Tables, bullets, headings, code blocks — agents
  scan, they don't read linearly.
- **Name precisely.** `LiteLlmConfig.Load`, `src/Forge.Tools/GrepTool.cs`,
  `~/.forge/llm.json`, `AgentRunner.WriteStateSnapshotAsync` — never "the
  config loader" or "the tool file".
- **Anti-pattern tells.** `NEVER do X` / `PREFER Y over Z` / `MUST run …`
  lands harder on an agent than "try to be concise".
- **Every line earns its tokens.** If a sentence doesn't change agent
  behavior or rule out a wrong path, cut it.
- **Cite, don't gesture.** Link by path + line or symbol, not by description.

Match this standard in new docs you write **and** in responses in this
session — terse, structured, verifiable, no filler.

## Documentation — favor docs over comments, favor docs over this file

- **`CLAUDE.md` is for universal per-turn rules only.** All topic-specific
  guidance (architecture, contracts, conventions, commands, recipes,
  subsystem behavior) **belongs in `docs/`** — update a doc or create one.
  **Never expand this file** with subsystem detail.
- **No inline code comments.** Never add `//`, `/* */`, or region comments in
  C# method bodies. If explanation is needed, put it in `docs/`.
- **`///` XML doc comments exist only for `TreatWarningsAsErrors` compliance**
  on public API. Keep them to one terse line; richer explanation goes in
  `docs/`.
- **Prefer updating existing docs over creating new ones.** Consult
  `docs/index.md` and `docs/.manifest.json` before writing a new file.
  Create a new doc only when no existing file owns the topic.
- **`CLAUDE.md` stays under 150 lines; other Markdown docs stay under 300.**
  If an edit would push any `*.md` file past its ceiling, **stop and surface
  the split decision to the user** — present candidate split points and
  wait for direction. Never split unilaterally; never cram to stay just
  under.

## Context protocol (read every user turn) — **CRITICAL**

This file is deliberately minimal. **Authoritative guidance for every topic
lives in `docs/`, not here.** Before acting on any user message that asks
for non-trivial work, you **MUST** refresh context by consulting:

1. **`docs/index.md`** — prose entry point. Use the **Agent fast-path** at
   the top before scanning layer directories.
2. **`docs/.manifest.json`** — JSON reverse index of `code_refs → docs`.
   When the task names a source file, grep the manifest for its path and
   open every doc that claims it.
3. **`docs/recipes/`** — task archetypes (new tool, new CLI command, new
   trace event, fix agent loop bug). If the task matches an archetype,
   the recipe is authoritative.

This protocol runs **every turn, not just at session start.** The docs
evolve during the session; so must your reading. **If `docs/` doesn't cover
something needed, fix that by updating `docs/` — do not grow this file.**

## IMPORTANT

- **NEVER add topic-specific content to `CLAUDE.md`.** Put it in `docs/`.
- **NEVER add inline code comments.** Explanation goes in `docs/`.
- **NEVER chain PowerShell commands with `&&`.** Use separate commands or `;`.
- **YOU MUST ask before any `*.md` doc crosses its ceiling** (150 for
  `CLAUDE.md`, 300 for others). Present candidate split points; never split
  unilaterally, never cram to stay just under.
- **YOU MUST run `dotnet build Forge.sln` after any C# edit** and fix all
  warnings before stopping (`TreatWarningsAsErrors=true`).
- **Stdout = JSON results, stderr = logs.** Details:
  `docs/Infrastructure/logging.md`.
- **Multi-session work uses the `planning-with-files` skill.** Plans live at
  `docs/plans/<YYYYMMDD>-<slug>/` (Manus triad — `task_plan.md` +
  `findings.md` + `progress.md`). For single-session changes, no plan needed.
- **For everything else** — architecture, conventions, package management,
  command registration, agent-loop contracts, runs/traces, self-hosted
  configs — **read the relevant doc**. Entry point: `docs/index.md`.
