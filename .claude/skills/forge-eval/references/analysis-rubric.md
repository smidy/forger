# Post-mortem rubric

Checklist for Phases 6, 6.5, and 7 of `forge-eval`. Run through every row — if a row does not apply, write "n/a" rather than skipping silently.

Most metrics below come from `scripts/analyse-run.ps1 <run-id>`. Copy its output into section A verbatim rather than retyping.

## A. Run shape

- Run id(s):
- Plan targeted:
- Agent under test:
- Attempt count (1–3):
- Exit code(s):
- Wall time:
- Event total (from trace.jsonl):
- Event kinds (name → count):
- `result.json` written? (yes/no — i.e. `submit_final` called):

## B. What trace + snapshots still cannot tell us

The earlier "no tool-call events / no LLM-call events / no iteration markers" gaps closed in the 2026-04-21 forge-init sweep. The `agent-resume-from-max-iterations` landing (2026-04-22) added per-iter `state.json` with full `messages` + `ledger`. Remaining blind spots to **always** record as findings when they block a Phase 7 question:

- [ ] Semantic tool-output quality (did `grep` return the useful hits or just noise?)
- [ ] Cost attribution per plan-acceptance-criterion (LLM tokens are per iter, not per sub-goal)
- [ ] Reasoning-vs-action drift (assistant claimed "next I'll X" then didn't; heuristic only)
- [ ] Cross-run regression without a stored digest (`compare-runs.ps1` is P1, not yet in the skill)

If any of the above would have answered a Phase 7 question, log it in the appropriate bucket (usually **Forge feature gap** or **Agent-definition issue**).

## B.2 State-snapshot findings

`scripts/analyse-run.ps1` reports, when `state.json` snapshots exist:

- Per-iter tool-call sequence (e.g. `iter 12: read_file,grep,grep,apply_patch`)
- Ledger growth curve (cumulative writes, flagged at the iter each new file first appeared)
- **Failure fingerprint** (see `references/failure-fingerprints.md`): `read-loop`, `patch-reject-loop`, `schema-fight`, `tool-error-spiral`, `stalled`

Record the fingerprint verbatim in Section A. When it matches a catalogued pattern, copy the pattern's "Ready-made finding text" into Section H with the specifics filled in. The bucket-mapping table in `failure-fingerprints.md` is a starting point, not a verdict — Section F attribution still wins.

## B.3 Qualitative signal → bucket

`scripts/analyse-reasoning.ps1` surfaces assistant reasoning text and bucket hints straight out of `state.json`. Every hint maps to exactly one bucket via the rules below. When reasoning evidence contradicts a quantitative fingerprint, the reasoning wins — it's closer to intent.

| Qualitative signal (from reasoning / tool_calls) | Primary bucket | Fallback / rationale |
|---|---|---|
| Reasoning literally says "I don't know where X is", "Let me check what's in /work/…", or cycles `ls` / `glob` over multiple roots | **Instruction / docs gap** | System prompt / tool description never told the agent where X lives. Propose a `CLAUDE.md` or agent-system-prompt line. |
| Reasoning cycles on the same subproblem > 20 iters, same tool, same kind of failure — but each attempt is subtly different | **Agent-definition issue** | Prompt lacks "stop and try something else" guidance. Escalate to **Forge feature gap** only if the subproblem is provably unsolvable in the current tool affordances. |
| Reasoning shows the agent using `bash` for a task Forge has a structured tool for (e.g. `bash("cat foo")` instead of `read_file`, `bash("grep …")` instead of `grep`) | **Agent-definition issue** | Tool-preference guidance missing. Escalate to **Tool-description issue** (a sub-bucket of agent-def) if the structured tool's `description` under-advertises its purpose. |
| Reasoning quotes a concrete Forge error message the agent doesn't recognise ("context did not match", "fs_denied", etc.) and then guesses a workaround | **Instruction / docs gap** | Error message needs a "suggestion-next-action" line. File the finding against the tool that emits the message. |
| Stuck-loop window fires on **arg-stability alone** (same `argsHash` repeated) | **Agent-definition issue** | Agent is re-sending the identical call — loop-break guidance missing. |
| Stuck-loop window fires on **result-stability alone** (unique args, identical normalized result) | **Forge feature gap** OR **Agent-definition issue** | "Different attempts, same failure" — either the underlying capability is broken (→ Forge bug / feature gap) or the agent cannot read the error well enough to pivot (→ agent-def). Attribute by reading the tool messages. |
| Prompt-token growth curve flags an iter with delta > 2× median | **Forge feature gap** | Context is accreting faster than the work justifies — usually a tool-result cap that's too permissive or a tail-pruning feature that hasn't landed. Cross-check against `context-auto-compaction` plan. |
| Reasoning prefaces bash commands with `cd /work/…` in > 30% of calls | **Instruction / docs gap** | Default cwd contract not advertised to the agent. Propose adding cwd to the bash tool's system-prompt line. |
| Reasoning mentions the model context window directly ("I'm running out of context", "let me summarise") | **Forge feature gap** | Agent-side compaction is not Forge's job yet — track as input to `context-auto-compaction`. |

**How to use:** after running `analyse-reasoning.ps1`, walk the bucket-hints block. For each hint, find the row above, paste the bucket into Section F, and quote the iter-N reasoning line into Section H as evidence. Every Phase 7 finding sourced from reasoning must cite at least one `[iter N]` sample — the reasoning is the evidence.

## C. Failures observed

For each failure found in `trace.jsonl`, stderr log, or process exit:

| # | Source | Event / message | Likely cause (feature-gap / bug / agent-def / docs) |
|---|--------|------------------|------------------------------------------------------|

Specifically look for:

- `tool_result_truncated` — cap too aggressive? payload too large? did the preview leave the agent misinformed?
- `fs_denied` — sandbox blocked a legitimate read? scope too narrow for this plan?
- `plan_drift` — state divergence between plan and actual run
- Raw exceptions / non-zero exit codes — map to project: `Forge.Cli`, `Forge.Agent`, etc.
- Loops inferred from repeated truncation artifacts or a non-decreasing `result.json` (same stale draft)
- Schema validation failure on `submit_final` — agent output shape vs `output_schema`

## D. Output quality (only if `submit_final` fired)

- Does `summary` accurately reflect the plan goal?
- Does `files_modified` cover every entry in the plan's File Changelist? List missing.
- Does `files_modified` match what `git diff --name-only` actually reports? Any divergence means the agent lied about its work.
- Are `open_questions` legitimate or answerable from supplied context? (answerable → prompt/agent issue; genuinely unanswerable → feature gap)

## E. Phase 6.5 — in-place verification

Filled in only if Phase 6.5 ran. Agent changes are already in the working tree; verify without rolling back.

| Check | Result | Notes |
|---|---|---|
| `git diff --name-only` matches agent's declared `files_modified` | | Divergence → agent-def finding |
| All changed files are under paths the plan authorised | | `src/`, `tests/`, `examples/`, etc. — flag anything outside |
| `dotnet build Forge.sln` exit 0 (warnings-as-errors) | | Top 3 diagnostics if fail |
| `dotnet test Forge.sln` exit 0 (when the plan required tests) | | Scope to touched projects if slow |
| No contamination in `artifacts/` | | Agent should not write its own findings |
| Rollback recipe recorded | | `git restore -s HEAD -- <path>` — don't execute without user approval |

A proposal that fails any of these is **never** silently dismissed — the failure is the finding.

## F. Root-cause attribution

Every finding gets exactly one bucket. Decision order:

1. Reproduces with a canned fake LLM response? → **Forge bug**
2. Requires a capability not in the current tool list / CLI surface / trace schema? → **Forge feature gap**
3. Fixable by editing `forge-dev.agent.yaml` (prompt, schema, tool list)? → **Agent-definition issue**
4. Fixable by a line in `CLAUDE.md`, `AGENTS.md`, or a doc the agent loads via `inject_project_context`? → **Instruction / docs gap**

If a finding fits more than one bucket, pick the **earliest** — fix the underlying cause, not the symptom.

## G. Dedup against prior evals

Before adding a finding:

1. Check conversation history for similar recurring entries.
2. If a match exists, increment its counter instead of duplicating.
3. A finding that appears in three consecutive evals is a candidate for a dedicated fix — surface it to the user.

## H. Report template

```markdown
# Forge-eval report — <plan name> — <YYYY-MM-DD>

**Run ids:** <id1>, <id2>, <id3>
**Verdict:** pass | partial | fail
**Build verified:** yes | no | n/a

## Metrics
<paste section A + the analyser output>

## Findings

### F1. <short title>  — [feature-gap | bug | agent-def | docs]
- What happened:
- Evidence (trace excerpt / file):
- Proposal:
- Size estimate: S / M / L
- Recurrence count (across this session's evals): N

<repeat per finding>

## Non-findings (checked, clean)
- …

## Recommended actions (ranked)
1. …
```
