---
name: forge-eval
description: Eval harness for the Forge CLI. Runs a pending implementation plan through a Forge agent (typically `forge-dev`), measures the run with a repeatable rubric, verifies whether the agent's proposal actually applies and builds, and produces bucketed findings — Forge feature gaps, bugs, agent-definition issues, and docs gaps. Use when the user says "eval forge", "dogfood forge", "run the forge eval harness", "test forge on a plan", "stress-test forge", or wants quantitative signal on how well `forge-dev` handles a real backlog item. Also triggers when regressions across forge versions need to be measured.
license: MIT
metadata:
  author: local
  version: "2.0.0"
---

# forge-eval

Eval harness: drive a real plan through a Forge agent, measure the run, verify the output is buildable, and turn what you learn into ranked improvements.

## Framing

This is an **eval**, not exploration. Each invocation:

1. Targets a specific plan (the task)
2. Runs a specific agent (the subject under test)
3. Produces measurable artifacts (the metrics)
4. Emits ranked findings (the deliverable)

Runs accumulate. The same plan re-evaluated after a Forge change should yield different metrics — that delta is the point.

## When to use

- User asks to eval, dogfood, stress-test, or exercise the Forge CLI.
- User wants to know where Forge breaks under a realistic backlog item.
- You just landed a Forge change and want to confirm it didn't regress the agent experience.

Do **not** use this to ship a plan. `forge-dev` proposes; this skill measures. Applying proposals requires separate user approval in Phase 6.5.

## Workflow

### Phase 0 — Precheck

**Step 0a — Pack + install the CLI (unless user said "skip install" or "use dotnet run").**

Eval runs must exercise the *current source tree*, not a stale global install. Before any precheck, rebuild + reinstall the global tool so `forge` on PATH matches HEAD:

```powershell
dotnet pack src/Forge.Cli/Forge.Cli.csproj -c Release -o nupkg
dotnet tool uninstall -g link.forge.cli
dotnet tool install -g link.forge.cli --add-source ./nupkg --version 0.1.0
forge --version   # confirm
```

Why this matters — two failure modes this prevents:

1. **Stale binary.** An earlier install masks changes the user just committed; the eval measures an old Forge and the findings are wrong.
2. **Locked build outputs.** Running the CLI via `dotnet run --project src/Forge.Cli` holds `src/Forge.Cli/bin/Debug/net9.0/*.dll` open on Windows. When the bash tool then tries to `dotnet build Forge.sln` inside the container, the bind-mounted bin/ is uneditable and the container build fails with `Input/output error` on `CopyFilesToOutputDirectory`. Using the installed global tool puts the running process outside `src/Forge.Cli/bin/`, so the container is free to overwrite those files.

Skip this step only if:
- The user explicitly said "use dotnet run" / "don't reinstall" / "skip install" — record *why* in the findings (debuggability, testing uncommitted changes, etc.).
- The eval target is *not* a build-verification task and the user's current install is known-fresh.

Fail fast on environment before burning tokens. Run each check; record the result.

| Check | How | Fail mode |
|---|---|---|
| `forge` on PATH | `forge --version` → exit 0 | Stop. Tell user to build + install. |
| LLM config present | `Test-Path ~/.forge/llm.json` | Stop. Tell user to create it. |
| LLM config has real model | read `~/.forge/llm.json`; `$.model` not placeholder | Stop. Record as UX gap if no clear error from forge itself. |
| Agent YAML `model:` resolved | read `.forge/agents/forge-dev.agent.yaml`; `model:` not `replace-with-your-model-id` | Stop. Record as docs/onboarding gap. |
| Repo clean (or user confirmed dirty) | `git status --porcelain` empty, or explicit OK | Stop and confirm with user. Dirty worktree contaminates Phase 6.5. |
| `.forge/agents/forge-dev.agent.yaml` present | `Test-Path` | Stop. Eval target missing. |

Any check failing that is **not** an environment mistake (e.g. CLI crashes on `--version`) is itself a Phase 7 finding — write it down even though the workflow halts.

### Phase 1 — Task definition

1. The user names the task to eval (an issue, a commit, a file to change, a feature description). If unclear, ask — never pick silently.
2. Capture:
   - The concrete change being requested (one sentence)
   - The acceptance criteria (build green, tests pass, specific file edits, etc.)
   - Whether the task is **Tactical** (clear scope, target files identifiable) or **Design** (architecture judgement needed first). Refuse Design tasks unless the user insists; record "needs decomposition first" as the finding.

### Phase 2 — Input assembly

`forge-dev` has `${cwd}` recursive read access (see its `filesystem.read` grant) and discovers source via `bash`. **The default input is just `{ "task": "<one sentence>" }`.** Pasting files into `input.files[]` is the exception, not the rule — reserve it for synthetic context that is *not* in the repo (e.g. a bug report, an external log the agent otherwise couldn't see).

1. Compose the input schema from `forge describe agent forge-dev` (today: `task` required, `context` optional — no `files` field on this agent; if a different agent is under test, adapt).
2. Decide how much to say in `task`. Two modes:
   - **Minimal (recommended for docs/agent-def stress tests):** a single sentence referencing the change in user terms — *"Implement the change described in this issue / commit / file."* Forces the agent to discover everything via `CLAUDE.md` injection + tool use. Surfaces doc/convention gaps clearly.
   - **Directed (for measuring code-synthesis quality, not discoverability):** paraphrase the goal + acceptance criteria in `task` / `context`. Use when you want the agent's implementation quality as the signal, holding discovery constant.
3. Write `./artifacts/eval-<task>-<timestamp>-input.json`. Keep the file round-trippable (pure JSON, UTF-8, LF, trailing newline) so `forge agent … --input "@…"` reads it cleanly.
4. First time the skill runs in a repo, ensure `artifacts/` is in `.gitignore`. If not, add it and commit that change separately before running.

### Phase 3 — Dry-run validation

```powershell
forge validate .forge/agents/forge-dev.agent.yaml
forge describe agent forge-dev
forge list tools
```

Any failure here = finding. Stop and report.

### Phase 4 — Execute

```powershell
forge agent forge-dev --input "@./artifacts/eval-<plan>-<ts>-input.json" `
  > ./artifacts/eval-<plan>-<ts>-result.json `
  2> ./artifacts/eval-<plan>-<ts>-stderr.log
```

- stdout → result JSON (single document)
- stderr → logs, including run id on startup
- Extract run id by diffing `forge runs list` before/after (more robust than parsing stderr)
- If wall time >5min with no new events, cancel (Ctrl-C → exit 130)

### Phase 5 — Live debug loop

Only if the run failed or produced a bad proposal. One change per iteration.

1. Run the analyser: `.claude/skills/forge-eval/scripts/analyse-run.ps1 <run-id>`. Note the **fingerprint** line (see [`references/failure-fingerprints.md`](references/failure-fingerprints.md)) — it narrows the hypothesis before you open any file.
2. Read `~/.forge/runs/<run-id>/status.json`, `result.json`, `trace.jsonl`. For max-iter failures, also read the terminal snapshot `~/.forge/runs/<run-id>/stages/<stage-id>/iterations/<last>/state.json` — full conversation + write ledger at the throw point.
3. Form one hypothesis. Pick ONE fix:
   - Sparse input → add specific files
   - Schema mismatch → record + adjust input
   - Tool missing → record as feature gap, do not retry
   - Model refusal → record prompt weakness, sharpen user prompt locally and retry
4. Rerun. Archive every attempt's artifacts under the same timestamp prefix.
5. Cap retries at 3. Beyond that, the failure IS the finding.

### Phase 6 — Post-mortem metrics

Run `.claude/skills/forge-eval/scripts/analyse-run.ps1 <run-id>` on the final attempt. It reports:

- Wall time (first / last event timestamps in `trace.jsonl`)
- Event kind histogram — today's schema includes:
  - `agent_iteration` — one per LLM turn of the tool loop
  - `llm_call` — each `chat/completions` call with `durationMs`, `finishReason`, `promptTokens`, `completionTokens`
  - `tool_call` — each tool invocation with `toolName`, `argsHash` (SHA-256 prefix, for redundancy detection), `durationMs`, `error`
- Truncation count + total bytes trimmed
- Fan-out iteration count per stage (pipelines only)
- Whether `result.json` was written (= `submit_final` was called and the output matched `output_schema`)
- **State snapshots** (when `stages/<id>/iterations/NNN/state.json` is present — requires the `agent-resume-from-max-iterations` landing) — per-iter tool-call sequence, ledger growth curve, and a **failure fingerprint** classifying the tail of the run against the patterns in [`references/failure-fingerprints.md`](references/failure-fingerprints.md). Absent when the run predates the snapshot landing; the analyser prints a clear note in that case.

The analyser surfaces these as per-tool histograms, per-LLM-call token/duration rollups, redundancy detection via repeated `argsHash` values, and the state-snapshot section. All event kinds above have shipped — the older skill text claiming they were unavailable is stale and was removed in the 2026-04-21 `forge-init` eval sweep.

### Phase 6.3 — Qualitative analysis

`analyse-run.ps1` answers *what happened*. This phase answers *why the agent struggled* — by sampling the assistant's own `reasoning_content` / `thinking` / `content` straight out of per-iter `state.json` and joining with `trace.jsonl` for `argsHash`-backed stuck-loop detection.

```powershell
.claude/skills/forge-eval/scripts/analyse-reasoning.ps1 `
    -RunId <run-id> `
    [-StageId agent] `
    [-SampleStrategy quick|full] `
    [-StuckLoopThreshold 20] `
    [-OutFormat text|jsonl|findings-scaffold] `
    [-RedactAbsolutePaths]
```

What the script reports:

- **Message-role histogram** — `system / user / assistant / tool` counts. Sanity-checks the run shape.
- **Adaptive tool histogram** — whole run + windowed slices (10-iter bins if <80 iters, deciles if ≥80). The slice view is where tool-mix *shifts* become visible (e.g. "read-heavy first decile, 90% bash by iter 50").
- **Two-axis stuck-loop detector** — for every 20-iter window, compute (a) **argsHash Jaccard** between halves (arg stability) and (b) **normalized-result-prefix Jaccard** (outcome stability). A window flags if *either* `(Jaccard>0.7 AND stability>0.5)` **or** `stability>0.7` alone — the second branch catches "different attempts, same failure" loops (e.g. MSBuild rabbit holes where each `dotnet build` has unique args but identical error output).
- **Prompt-token growth curve** — per-iter `promptTokens` from `llm_call` events; auto-flags iters where delta > 2× median (the inflection points where context bloat accelerates).
- **Sampled reasoning** — priority-ordered samples (tail → stuck-loop boundaries → per-tool first-use → opening), capped at `SampleCap` (default 25). Each sample shows `REASONING`, `CONTENT`, and `TOOL_CALLS` alongside the iter index so the operator can map evidence back to the trace.
- **Phase 7 bucket hints** — heuristic flags (e.g. "bash share 63% with zero structured writes", "prompt-token growth flagged at iter(s) …") that feed directly into Phase 7 attribution.

Workflow:

1. Run the script on the **final** attempt's run id (post-Phase-5 retry). Optionally re-run on earlier attempts if a finding needs cross-attempt evidence.
2. **Write findings to the scratchpad `findings.md` before Phase 6.5.** The qualitative signal is most perishable — capture verbatim quotes while they're fresh, then move on.
3. The script's output is a **first-class input to Phase 7 bucketing**. Every hint maps to exactly one bucket via the rubric's "Qualitative signal → bucket" section (see [`references/analysis-rubric.md`](references/analysis-rubric.md)) and the catalogued patterns in [`references/failure-fingerprints.md`](references/failure-fingerprints.md).
4. If a stuck-loop window fires, grep [`references/failure-fingerprints.md`](references/failure-fingerprints.md) for a pattern match. A catalogued pattern has a canonical mitigation ready to paste into Phase 7; an uncatalogued pattern is itself a finding — propose a new fingerprint entry in Section H.

The script is deliberately **offline + LLM-free** — the evidence is the raw reasoning text, not a model-generated summary. Interpretation stays with the operator (and Phase 7).

### Phase 6.5 — In-place verification

The current `forge-dev` agent applies changes directly to the working tree via the `bash` tool (the sole write surface in the minimal-forge cut). By the time the run ends, the repo is already modified. Phase 6.5 verifies those edits without rolling them back — and records a rollback recipe in case the reviewer decides they're bad.

```powershell
# 1. What did the agent actually change?
git status
git diff --stat        # scope of changes
git diff               # content — scan for anything surprising

# 2. Does the repo still build?
dotnet build Forge.sln

# 3. (Optional but recommended) Run the test suite.
dotnet test Forge.sln

# 4. If any of the above fail OR the diff is unacceptable, rollback:
#    git restore --source=HEAD --staged --worktree -- <changed-paths>
#    or for the full reset: git restore -s HEAD :/
```

Record for the findings report:

| Check | Pass/Fail | Notes |
|---|---|---|
| Agent-declared `files_modified` match `git diff --name-only` | | List any divergence — the agent lied about which files it touched |
| `dotnet build Forge.sln` exit 0 | | `TreatWarningsAsErrors=true` — warnings count as fail |
| `dotnet test Forge.sln` exit 0 | | Scope to touched projects if the suite is slow |
| Changes match the task's authorised paths | | Flag files outside scope |
| No contamination in `artifacts/` | | Agent should only touch authorised paths |

A build or test failure, or a diff outside the task's authorised paths, is **always** a finding (Forge bug, agent-def, or docs gap — attribute per the rubric). Never silently dismiss.

**Rollback when the agent's output is unusable.** The Phase 5 retry budget is 3; beyond that, treat the failure as the finding. If the review rejects the changes:

```powershell
git restore -s HEAD :/    # revert every working-tree change
# OR scope to a subset:
git restore -s HEAD -- <pathspec>
```

The eval skill itself never rolls back — the user does, after reviewing Phase 7 output.

### Phase 7 — Findings report

Present findings to the user — they decide whether to record them in commit messages, issues, or a fresh log. Do not write findings to a fixed path on disk.

Bucket every finding into exactly one of:

1. **Forge feature gap** — capability needed but absent. Include minimal API proposal.
2. **Forge bug** — reproducible defect. Include trace excerpt and minimal repro.
3. **Agent-definition issue** — fixable by editing `.forge/agents/forge-dev.agent.yaml`. Include concrete diff.
4. **Instruction / docs gap** — fixable by adding a line to `CLAUDE.md` / `AGENTS.md`. Include proposed text and target file.

Attribution rule: if a finding fits multiple buckets, pick the **earliest** — fix causes, not symptoms.

Present the report. Ask which items to action. Do not modify anything until the user approves.

Use the template in `references/analysis-rubric.md`.

## Accumulated findings

Track recurrence in conversation — if the same failure shows up across multiple evals, surface that to the user as a candidate for a dedicated fix.

## Guardrails

- **Never** `git restore` or otherwise roll back the agent's working-tree edits without the user's explicit approval. Phase 6.5 verifies in place; only the user decides whether to keep the diff.
- **Never** mask a failure by retrying with a different input without recording both attempts.
- **Never** edit `.forge/agents/forge-dev.agent.yaml` during a run to "help" it succeed. Record the edit as a finding; retry only after explicit user approval.
- **Never** put eval artifacts inside `src/`, `docs/`, or any tracked source dir. Use `./artifacts/` and ensure it's gitignored.
- If `~/.forge/llm.json` is missing, stop. Do not auto-configure.
- If the repo has uncommitted changes, surface them before starting — they contaminate Phase 6.5.
- The Phase 6.5 `git worktree` must be removed on every exit path, including errors. Wrap in try/finally.

## Reference

- Run layout, trace schema, exit codes → `docs/Presentation/cli.md`
- Forge invariants → `CLAUDE.md`
- Agent under test → `.forge/agents/forge-dev.agent.yaml`
- Quantitative analyser → `.claude/skills/forge-eval/scripts/analyse-run.ps1`
- Qualitative analyser → `.claude/skills/forge-eval/scripts/analyse-reasoning.ps1`
- Rubric / report template → `references/analysis-rubric.md`
- Failure-pattern catalogue → `references/failure-fingerprints.md`
