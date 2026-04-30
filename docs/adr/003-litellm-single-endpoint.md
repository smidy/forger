---
layer: adr
title: "003 Single LiteLLM endpoint"
updated: 2026-04-21
code_refs: [src/Forge.Llm/LiteLlmClient.cs, src/Forge.Llm/LiteLlmConfig.cs]
related: [Infrastructure/llm-client.md, adr/001-own-the-tool-loop.md]
---

# 003 Single LiteLLM endpoint

## Status
Accepted

## Context
Model providers each ship their own SDK, auth flow, retry semantics, and rate-limit response. Embedding provider-specific code in Forge would mean shipping and versioning each provider's behavior. Model routing (pick the right model per task) is orthogonal to agent code and changes more often than agent YAML.

## Decision
Forge posts only to `{baseUrl}/chat/completions` via `LiteLlmClient`. `baseUrl` in `~/.forge/llm.json` MUST include `/v1`. A LiteLLM proxy (local or remote) handles provider auth, retries, rate limiting, caching, and model-name routing. Forge speaks the OpenAI-compatible `chat/completions` schema only.

## Consequences
- Any model behind LiteLLM works: Anthropic, OpenAI, Azure, local models (Ollama), etc.
- Provider features not surfaced in `chat/completions` are unavailable.
- A LiteLLM proxy is a hard dependency for anything beyond the default `http://localhost:4001`.
- Cost tracking, rate limits, caching, and routing policy live in proxy config — NOT in agent YAML.
