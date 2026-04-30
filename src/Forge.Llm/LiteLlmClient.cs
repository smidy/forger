using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Microsoft.Extensions.Logging;

namespace Forge.Llm;

public sealed class LiteLlmClient : ILlmClient
{
  private readonly HttpClient _http;
  private readonly ILogger<LiteLlmClient> _log;
  private readonly RateLimitConfig _rateLimit;

  /// <summary>
  /// Optional per-run trace sink. Set by the caller (AgentRunner / PipelineExecutor)
  /// before the first LLM call so retry events land in the run's trace stream.
  /// </summary>
  public ITraceSink? TraceSink { get; set; }

  public LiteLlmClient(HttpClient http, ILogger<LiteLlmClient> log, LiteLlmConfig? config = null)
  {
    _http = http;
    _log = log;
    _rateLimit = config?.RateLimit ?? new RateLimitConfig();
  }

  public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
  {
    var root = BuildRequestObject(request);
    var json = root.ToJsonString();
    using var content = new StringContent(json, Encoding.UTF8, "application/json");

    var maxRetries = _rateLimit.MaxRetries;

    for (var attempt = 0; attempt <= maxRetries; attempt++)
    {
      using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = content };
      using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
      var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

      if (resp.IsSuccessStatusCode)
      {
        return ParseCompletionResponse(body);
      }

      // Non-429 errors — no retry, throw immediately
      if (resp.StatusCode != (HttpStatusCode)429)
      {
        throw new ProviderException($"LiteLLM error {(int)resp.StatusCode}: {body}", (int)resp.StatusCode);
      }

      // 429 — classify
      var retryAfterHeader = resp.Headers.TryGetValues("Retry-After", out var values)
        ? values.FirstOrDefault()
        : null;

      var classified = ProviderErrorClassifier.Classify(
        retryAfterHeader,
        body,
        _rateLimit.MaxRetryAfterSeconds);

      // Quota exhausted — throw immediately, no retry
      if (classified is QuotaExhaustedException qe)
      {
        throw qe;
      }

      var rl = (RateLimitedException)classified;

      // Retry-After > max — surface without retry
      if (rl.RetryAfter is { TotalSeconds: > 0 } ts && ts.TotalSeconds > _rateLimit.MaxRetryAfterSeconds)
      {
        throw rl;
      }

      // Exhausted retries?
      if (attempt >= maxRetries)
      {
        throw rl;
      }

      // Compute delay
      var delayMs = ComputeBackoffMs(attempt, rl.RetryAfter);
      _log.LogDebug("LLM 429 rate-limit on attempt {Attempt}/{MaxRetries}; retrying after {DelayMs}ms",
        attempt + 1, maxRetries, delayMs);

      TraceSink?.Trace(new LlmRetryEvent
      {
        Attempt = attempt + 1,
        MaxAttempts = maxRetries,
        DelayMs = delayMs,
        StatusCode = 429,
        RetryAfterHeader = retryAfterHeader
      });

      await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

      // Recreate content for retry (StringContent can only be read once)
      content.Dispose();
    }

    // Unreachable (the exhausted-retries branch inside the loop handles this)
    throw new RateLimitedException("LLM rate limited (429) — retries exhausted.", null);
  }

  private int ComputeBackoffMs(int attempt, TimeSpan? retryAfter)
  {
    // If Retry-After is present and within the cap, use it verbatim
    if (retryAfter is { TotalSeconds: > 0 } ts && ts.TotalSeconds <= _rateLimit.MaxRetryAfterSeconds)
    {
      return (int)(ts.TotalMilliseconds);
    }

    // Exponential backoff: base * 2^attempt, clamped before the shift to
    // avoid overflow on absurd attempt counts (attempt>=31 would overflow int).
    var baseMs = _rateLimit.BaseBackoffMs;
    var capMs = _rateLimit.MaxRetryAfterSeconds * 1000;
    var shift = Math.Min(attempt, 20);
    var exponential = baseMs * (1 << shift);
    var capped = Math.Min(exponential, capMs);
    var jitter = Random.Shared.Next(0, 501); // 0–500ms
    return capped + jitter;
  }

  private static JsonObject BuildRequestObject(CompletionRequest request)
  {
    var root = new JsonObject { ["model"] = request.Model, ["max_tokens"] = request.MaxTokens };

    var messages = new JsonArray();
    foreach (var m in request.Messages)
    {
      if (IsEmptySystemMessage(m))
      {
        continue;
      }

      messages.Add(m.DeepClone());
    }

    root["messages"] = messages;

    if (request.Tools is { Count: > 0 })
    {
      var tools = new JsonArray();
      foreach (var t in request.Tools)
      {
        var fn = new JsonObject
        {
          ["name"] = t.Function.Name,
          ["parameters"] = t.Function.Parameters.DeepClone()
        };
        if (!string.IsNullOrEmpty(t.Function.Description))
        {
          fn["description"] = t.Function.Description;
        }

        tools.Add(new JsonObject { ["type"] = t.Type, ["function"] = fn });
      }

      root["tools"] = tools;
    }

    if (request.ToolChoice is not null)
    {
      root["tool_choice"] = request.ToolChoice switch
      {
        string s => JsonValue.Create(s)!,
        bool b => JsonValue.Create(b),
        JsonNode jn => jn.DeepClone(),
        _ => JsonSerializer.SerializeToNode(request.ToolChoice)
      };
    }

    // Reasoning knobs: emit only when set. Omission keeps the request body
    // byte-for-byte identical to the pre-feature wire shape, which the
    // agent-reasoning plan treats as a hard invariant.
    if (request.ReasoningEffort is { Length: > 0 })
    {
      root["reasoning_effort"] = request.ReasoningEffort;
    }

    if (request.ThinkingBudgetTokens is int budget)
    {
      root["thinking"] = new JsonObject
      {
        ["type"] = "enabled",
        ["budget_tokens"] = budget
      };
    }

    return root;
  }

  private static bool IsEmptySystemMessage(JsonNode m)
  {
    if (m is not JsonObject o)
    {
      return false;
    }

    if (o["role"]?.GetValue<string>() != "system")
    {
      return false;
    }

    var c = o["content"];
    return c switch
    {
      null => true,
      JsonValue v when v.TryGetValue(out string? s) => string.IsNullOrEmpty(s),
      JsonArray a => a.Count == 0,
      _ => false
    };
  }

  private CompletionResponse ParseCompletionResponse(string json)
  {
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var result = new CompletionResponse { Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "" };
    if (root.TryGetProperty("usage", out var usage))
    {
      result.Usage = new UsagePayload
      {
        PromptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
        CompletionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0,
        PromptCacheHitTokens = usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.TryGetProperty("cached_tokens", out var ctk)
          ? ctk.GetInt32()
          : usage.TryGetProperty("cache_read_input_tokens", out var cri) ? cri.GetInt32() : null,
        PromptCacheCreationTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cci) ? cci.GetInt32() : null,
        ReasoningTokens = usage.TryGetProperty("completion_tokens_details", out var ctd) && ctd.TryGetProperty("reasoning_tokens", out var rt)
          ? rt.GetInt32()
          : null
      };
    }

    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
    {
      return result;
    }

    foreach (var ch in choices.EnumerateArray())
    {
      var choice = new CompletionChoice { Index = ch.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0 };
      if (ch.TryGetProperty("finish_reason", out var fr))
      {
        choice.FinishReason = fr.GetString();
      }

      if (!ch.TryGetProperty("message", out var msg))
      {
        continue;
      }

      choice.Message = new ChatMessagePayload
      {
        Role = msg.TryGetProperty("role", out var role) ? role.GetString() ?? "assistant" : "assistant",
        Content = msg.TryGetProperty("content", out var content) ? JsonNode.Parse(content.GetRawText()) : null,
        ReasoningContent = msg.TryGetProperty("reasoning_content", out var rc) ? JsonNode.Parse(rc.GetRawText()) : null,
        ThinkingBlocks = msg.TryGetProperty("thinking_blocks", out var tb) && tb.ValueKind == JsonValueKind.Array
          ? JsonNode.Parse(tb.GetRawText()) as JsonArray
          : null
      };

      if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
      {
        var list = new List<ToolCallPayload>();
        foreach (var el in tc.EnumerateArray())
        {
          try
          {
            var callId = el.GetProperty("id").GetString() ?? "";
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : "function";
            var fn = el.GetProperty("function");
            var name = fn.GetProperty("name").GetString() ?? "";
            var args = "{}";
            if (fn.TryGetProperty("arguments", out var argsEl))
            {
              args = argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() ?? "{}" : argsEl.GetRawText();
            }

            list.Add(new ToolCallPayload { Id = callId, Type = type ?? "function", Function = new FunctionCallPayload { Name = name, Arguments = args } });
          }
          catch (JsonException ex)
          {
            _log.LogWarning(ex, "Skipping malformed tool_call entry");
          }
        }

        choice.Message.ToolCalls = list;
      }

      result.Choices.Add(choice);
    }

    return result;
  }
}
