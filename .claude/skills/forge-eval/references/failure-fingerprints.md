# Failure fingerprints

Repeatable patterns that appear in the terminal `state.json` of a Forge agent run that exited without `submit_final`. `scripts/analyse-run.ps1` classifies the last ~5 iterations against these patterns and prints the best match. `scripts/analyse-reasoning.ps1` surfaces the reasoning-trace evidence that distinguishes them. This file is the canonical reference for what each label means and which finding bucket it usually points to.

Fingerprints are **signal, not verdict**. Phase 7 reviewer still owns final attribution. Consistent labels keep findings comparable across runs.

**Living library.** When a Phase 7 review uncovers a pattern that recurs across evals but isn't in this file, add it. Each new entry needs a **Pattern** line (the shape in raw numbers / quotes), a **Signal** line (how `analyse-*.ps1` detects it), a **Canonical mitigation** line (the paste-ready fix), and the usual bucket attribution. The two analyser scripts are both offline + deterministic — new detectors live next to the pattern description rather than in ad-hoc operator lore.

## Window

Default = last 5 completed iterations (snapshots are written at end-of-iter, after tool side-effects land). Shorter runs fall back to whatever iterations exist. If <2 iters exist, the classifier returns `stalled — too few iterations to classify`.

## Patterns

### `read-loop`

**Detection:** ≥80% of tool calls in the window are in `{read_file, read_file_slice, grep, glob, fetch_url, web_search}`; zero writes landed.

**Symptom:** Agent spent the tail of the run re-reading context. No progress on the deliverable.

**Typical buckets (in order):**
1. **Agent-definition issue** — system prompt didn't give a "when have you read enough" signal
2. **Instruction / docs gap** — CLAUDE.md / AGENTS.md don't surface where X lives, so the agent kept hunting

**Ready-made finding text:**
> Agent stalled in a read-loop at iter N (window: R reads, 0 writes). Terminal context referenced [paths]. Root cause: [prompt didn't bound discovery | docs didn't surface Y]. Proposed: [concrete agent.yaml edit | concrete CLAUDE.md line].

### `patch-reject-loop`

**Detection:** ≥3 `apply_patch` calls in the window; ≥2 `role: tool` messages carry `"context did not match"` text.

**Symptom:** Agent keeps applying a patch whose expected context no longer matches the file. Usually the agent cached a stale view and didn't `read_file` between attempts.

**Typical buckets:**
1. **Forge feature gap** — `apply_patch` doesn't expose the mismatched hunk precisely enough for self-recovery
2. **Agent-definition issue** — system prompt doesn't mandate `read_file` after `apply_patch` failure

**Ready-made finding text:**
> apply_patch rejected N× at iter M..N with "context did not match" — agent did not re-read the target between attempts. Proposed: [apply_patch returns the diverging hunk | agent.yaml system-prompt rule: "after apply_patch failure, read_file the target before retrying"].

### `schema-fight`

**Detection:** ≥2 `submit_final` calls in the window whose arguments produced error `role: tool` messages (schema validation rejection).

**Symptom:** Agent knows the work is done but can't shape its output to match `output_schema`.

**Typical bucket:** **Agent-definition issue** — schema too strict, ambiguous, or under-documented in the system prompt.

**Ready-made finding text:**
> submit_final rejected N× by output_schema validation at iter M..N. Rejected field(s): [extract from error messages]. Proposed: [clarify schema example in user_prompt | relax field from required to optional | add JSON Schema `description` to the failing property].

### `tool-error-spiral`

**Detection:** Error tool-messages per iter rise across the window (first iter's count < last iter's count, last ≥2).

**Symptom:** Every new call compounds the problem. Agent isn't recovering.

**Typical buckets:**
1. **Forge bug** if the same tool with the same args keeps failing (tool itself is broken)
2. **Agent-definition issue** if the agent keeps trying the same approach — missing "stop and try something else" rule

**Ready-made finding text:**
> Tool-error count rose across final K iters (iter N: X errors → iter N+K: Y errors). Failing tool: [name]. Last error: [verbatim]. Proposed: [investigate tool / raise as Forge bug | add recovery rule to agent.yaml].

### `stalled`

**Detection:** None of the above match; run hit `max_iterations` or terminated without classifiable pattern.

**Symptom:** Agent ran out of iteration budget doing plausible-looking work.

**Typical buckets:**
1. **Agent-definition issue** — `max_iterations` too low for this plan class
2. **Forge feature gap** — if the conversation was still growing usefully, the real issue is context exhaustion (see `agent-message-pruning` when it lands)

**Ready-made finding text:**
> Agent ran to max_iterations=N without pathological pattern. Final ledger: L writes across K files. Effective progress at throw: [one-line read of last assistant.content]. Proposed: [raise max_iterations to M | block on agent-message-pruning | split plan into two evals].

### `stale-obj-dotnet-build`

**Pattern:** Agent shells out to `dotnet build` or `dotnet test` from a read-only source tree (e.g. a Forge `FsScope` grant with no write path over `obj/`). MSBuild finds pre-existing `obj/*/…AssemblyAttributes.cs` and CS0579 "duplicate `GlobalNamespace`" cascades. Agent iterates on `-p:BaseIntermediateOutputPath=…`, `--no-incremental`, `rm -rf obj`, etc., without ever succeeding.

**Signal (analyse-reasoning):** stuck-loop window fires on **result-stability alone** (unique command args, near-identical CS0579 / MSBuild error prefix). Reasoning quotes "AssemblyAttributes" or "BaseIntermediateOutputPath" across ≥3 samples.

**Typical buckets (in order):**
1. **Forge feature gap** — ro source + stale-obj is unworkable; the eval host needs a writable overlay for `obj/`, `bin/`, or a `dotnet build --artifacts-path` default
2. **Agent-definition issue** — system prompt should flag "don't shell out to dotnet against a ro root; use `apply_patch` and let the operator build"

**Canonical mitigation:**
> Agent-definition change: add a bash-tool pre-condition in `forge-dev.agent.yaml` — *"If `dotnet build` fails on CS0579/AssemblyAttributes inside a read-only mount, stop; propose the edit via `apply_patch` and ask the operator to build."* If the eval host is meant to support local builds, file against Forge as a feature gap on the write-overlay story.

### `mount-index-discovery`

**Pattern:** Agent runs repeated `ls /work/…`, `bash("pwd")`, `cat /etc/mtab`, or variants in the first 5–10 iters, trying to locate the repo root inside a containerised bash tool. Reasoning says "let me check what's in /work/read/0" or similar. Burns iterations rediscovering a mount layout the system prompt could have stated in one line.

**Signal (analyse-reasoning):** bucket hints includes "bash commands prefaced with `cd /work/…` in >30% of calls". Reasoning samples from the opening decile contain the literal mount path as an unknown being probed.

**Typical buckets (in order):**
1. **Instruction / docs gap** — bash tool description / system prompt doesn't advertise the mount table
2. **Forge feature gap** — consider making mount layout a first-class system-prompt injection (like `inject_project_context`)

**Canonical mitigation:**
> Add to the bash tool's `description` (or the agent system prompt): *"You execute inside a Linux container. Mounts: `/work/read/0` = repo source (read-only), `/work/write/1` = repo writes, `/work/run` = run workspace. CWD is `/work/read/0`."* One-line paste eliminates the discovery loop.

### `bash-outcompetes-structured-tools`

**Pattern:** Across the run, `bash` share exceeds 50% while `read_file`, `glob`, `grep`, `apply_patch`, `write_repo_file` together are <20%, despite the work obviously calling for structured writes. `submit_final` never fires. Agent uses `bash("cat foo")` instead of `read_file`, `bash("sed -i …")` instead of `apply_patch`.

**Signal (analyse-run + analyse-reasoning):** whole-run histogram flags `bash share >50% with zero structured writes`. Reasoning samples show `bash` being chosen for tasks with a structured equivalent.

**Typical buckets (in order):**
1. **Agent-definition issue** — tool-preference guidance missing from `forge-dev.agent.yaml`
2. **Tool-description issue** (sub-bucket of agent-def) — structured tools' descriptions don't state "prefer over bash for X"

**Canonical mitigation:**
> Agent-definition change: add a tool-preference block to the system prompt — *"Prefer structured tools over bash: use `read_file` not `bash cat`, `grep`/`glob` not `bash grep`/`bash find`, `apply_patch` not `bash sed`. Reserve bash for build/test/shell operations that have no structured equivalent."* Re-run the eval; expect bash share to drop under 30%.

### `context-window-exhaustion`

**Pattern:** `promptTokens` on the final `llm_call` exceeds 90% of the model's context limit. Reasoning late in the run mentions "running out of context", "let me summarise", or the agent's replies become visibly terser / less coherent. The run terminates at max_iterations or mid-thought, not on `submit_final`.

**Signal (analyse-reasoning):** prompt-token growth curve shows the final iter above 90% of the documented limit; growth-delta flagged iters exist before the end of the run. Reasoning samples from the tail quote "context" / "summarise" / "out of memory" explicitly.

**Typical buckets (in order):**
1. **Forge feature gap** — context-auto-compaction / message-history pruning is missing
2. **Agent-definition issue** — `max_iterations` and tool-result caps should be tuned for the model's context; not always the real fix but sometimes buys enough headroom

**Canonical mitigation:**
> File against Forge as input to the `context-auto-compaction` plan. If no compaction plan is active: propose a stricter `tool_result_cap` in `forge-dev.agent.yaml` as a stopgap (e.g. cap `read_file` results at 32 KiB, `bash` at 16 KiB) — record that this is a bandage, not a fix.

### `default-cwd-not-known`

**Pattern:** Agent prefaces `bash` commands with `cd /work/…` in >30% of calls. Reasoning treats the working directory as unknown or variable across calls. Often co-occurs with `mount-index-discovery` but persists through the run, not just the opening.

**Signal (analyse-reasoning):** bucket hints explicitly flags "bash commands prefaced with `cd /work/…` in N% of calls". Reasoning samples across multiple deciles include the `cd` preface.

**Typical buckets:** **Instruction / docs gap** (first) — cwd contract is undocumented. Escalate to **Forge feature gap** only if the bash tool's cwd is genuinely non-deterministic across calls.

**Canonical mitigation:**
> One-line addition to the bash tool `description`: *"CWD is `/work/read/0` (repo source) on every call — do not preface commands with `cd`. Shell state does not persist between calls."*

## Bucket mapping table

| Fingerprint | First bucket | Second |
|---|---|---|
| read-loop | agent-def | docs |
| patch-reject-loop | feature-gap | agent-def |
| schema-fight | agent-def | — |
| tool-error-spiral | forge-bug | agent-def |
| stalled | agent-def | feature-gap |
| stale-obj-dotnet-build | feature-gap | agent-def |
| mount-index-discovery | docs | feature-gap |
| bash-outcompetes-structured-tools | agent-def | — |
| context-window-exhaustion | feature-gap | agent-def |
| default-cwd-not-known | docs | feature-gap |

## Using fingerprints in Phase 7

1. Record the fingerprint verbatim in rubric Section A ("Run shape").
2. If it matches a pattern here, open this file, copy the **Ready-made finding text** template, and fill in specifics from the trace + state.json.
3. If the pattern keeps recurring across evals (≥3 runs), surface it to the user as a candidate for a dedicated fix.
