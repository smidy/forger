---
layer: Infrastructure
title: LLM client
updated: 2026-04-21
code_refs: [src/Forge.Llm/LiteLlmClient.cs, src/Forge.Llm/LiteLlmConfig.cs, src/Forge.Llm/ServiceCollectionExtensions.cs, src/Forge.Core/Llm/CompletionModels.cs]
related: [adr/003-litellm-single-endpoint.md, Application/AgentRunner.md]
---

# LLM client

Forge posts to exactly one HTTP target: a LiteLLM (OpenAI-compatible) proxy. `LiteLlmClient : ILlmClient` is the only implementation; there is no provider-specific code.

## Config (`~/.forge/llm.json`)

```json
{
  "baseUrl": "http://localhost:4001/v1",
  "apiKey": "…",
  "defaultModel": "anthropic/claude-sonnet-4-6"
}
```

- `baseUrl` **MUST include `/v1`** — the client posts to `{baseUrl}/chat/completions`.
- `${ENV_VAR}` inside string values is substituted from process env at load time (`LiteLlmConfig.EnvSubstitute`).
- Process env overrides the file: `FORGE_LLM_BASE_URL`, `FORGE_LLM_API_KEY`, `FORGE_LLM_DEFAULT_MODEL`.

## Request shape

`LiteLlmClient.BuildRequestObject` emits the OpenAI `chat/completions` JSON:

- `model`, `max_tokens`, `messages`
- `tools` (if any) — each `{ type: "function", function: { name, description?, parameters } }`
- `tool_choice` — `"auto"` or explicit selector
- `reasoning_effort` (string, optional) — emitted only when `CompletionRequest.ReasoningEffort` is non-null. Forwarded verbatim; LiteLLM drops it for providers that don't support it.
- `thinking` (object, optional) — `{type: "enabled", budget_tokens: N}`. Emitted only when `CompletionRequest.ThinkingBudgetTokens` is non-null (Anthropic extended-thinking; LiteLLM drops elsewhere).

Empty system messages are dropped before send.

## Response shape

- `usage.prompt_tokens` / `usage.completion_tokens` → `UsagePayload.PromptTokens` / `CompletionTokens`.
- `usage.prompt_tokens_details.cached_tokens` (OpenAI) or `usage.cache_read_input_tokens` (Anthropic) → `UsagePayload.PromptCacheHitTokens`. Null when not reported.
- `usage.cache_creation_input_tokens` (Anthropic) → `UsagePayload.PromptCacheCreationTokens`. Null elsewhere.
- `usage.completion_tokens_details.reasoning_tokens` → `UsagePayload.ReasoningTokens`. Null when provider omits. A reported zero stays distinct from absent.
- `choices[0].message.reasoning_content` / `thinking_blocks` → `ChatMessagePayload.ReasoningContent` / `ThinkingBlocks`. `AgentRunner` persists these per iteration to `reasoning.txt` — see [Data/workspace.md](../Data/workspace.md).

## Error mapping

Non-2xx → `ProviderException(statusCode, body)`. Retries, rate limiting, caching, and provider auth are LiteLLM's job — not Forge's.

## DI wire-up

`services.AddLiteLlm(Action<LiteLlmConfig>? configure)` registers:
- `LiteLlmConfig` (configured + env-overridden)
- A typed `HttpClient` with the `baseUrl` pre-applied
- `LiteLlmClient` as `ILlmClient`
