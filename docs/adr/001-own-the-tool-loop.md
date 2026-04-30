---
layer: adr
title: "001 Own the tool loop, no Semantic Kernel"
updated: 2026-04-21
code_refs: [src/Forge.Agent/AgentRunner.cs, src/Forge.Llm/LiteLlmClient.cs]
related: [Application/AgentRunner.md, Infrastructure/llm-client.md]
---

# 001 Own the tool loop, no Semantic Kernel

## Status
Accepted

## Context
AI orchestration frameworks (Semantic Kernel, LangChain, etc.) bundle model clients, tool loops, execution environments, and workflow logic into one runtime. This makes retries, caching, model routing, and timeout behavior opaque and hard to override. Forge needs full control over the tool-call contract, schema validation, termination, and failure modes — including how `submit_final` is enforced and how large tool results are capped.

## Decision
Forge implements its own tool loop in `AgentRunner.RunAsync`. The only HTTP target is a LiteLLM proxy that speaks OpenAI-compatible `chat/completions`. Retries, rate limiting, caching, and model routing live in the proxy config, not in Forge.

## Consequences
- Forge stays thin: no framework upgrades to chase, no hidden behavior in vendor code.
- Tool-call semantics (schema validation, synthetic `submit_final`, cap-and-artifact on large results) are under our control.
- We do not benefit from framework-provided planners or higher-level patterns; those are out of scope.
- All provider concerns funnel through one proxy config — swapping models or adding routing happens there, not in code.
