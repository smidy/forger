using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Filesystem;
using System.Linq;
using System.IO;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Refs;
using Forge.Core.Schema;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Forge.Tools.Docker;
using Forge.Agent.Compaction;
using Forge.Llm;
using Json.Schema;
using Microsoft.Extensions.Logging;

namespace Forge.Agent;

public static class AgentRunner
{
  public static async Task<JsonNode> RunAsync(
    AgentConfig config,
    JsonNode input,
    ToolContext ctx,
    ToolRegistry tools,
    string? agentYamlPath,
    CancellationToken ct,
    TimeProvider? clock = null,
    AgentResumeState? resumeState = null,
    int? maxIterationsOverride = null,
    bool isChat = false,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null)
  {
    clock ??= TimeProvider.System;
    var today = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    void ExtendForgeContext(Scriban.Runtime.ScriptObject so)
    {
      var forge = new Scriban.Runtime.ScriptObject
      {
        ["today"] = today
      };
      so["forge"] = forge;
    }

    JsonSchema? outputSchema = config.OutputSchema is null
      ? null
      : JsonSchema.FromText(config.OutputSchema.ToJsonString());

    if (ctx.Llm is LiteLlmClient llc)
    {
      llc.TraceSink = ctx.Trace;
    }

    var ledger = new AgentWriteLedger();

    var isResume = resumeState is not null;
    var startIter = 0;
    var maxIter = maxIterationsOverride ?? config.MaxIterations;
    if (isResume)
    {
      foreach (var entry in resumeState!.LedgerEntries)
      {
        ledger.Record(entry);
      }
      startIter = resumeState.StartingIter;
    }
    else
    {
      var inputSchema = JsonSchema.FromText(config.InputSchema.ToJsonString());
      Validator.Validate(input, inputSchema);
    }
    
    ctx = ctx with { WriteLedger = ledger };

    var state = new RunState
    {
      Input = input,
      Runtime = new RuntimeContext
      {
        RunId = ctx.RunId,
        Workspace = ctx.RunWorkspace,
        StageDir = ctx.StageDir
      }
    };
    var resolver = new Resolver();
    var systemParts = new List<string>();
    if (config.InjectProjectContext)
    {
      var roots = ProjectContextPaths.GetOrderedRoots(agentYamlPath, config.ProjectContextRoots);
      var block = ProjectMarkdownLoader.LoadOrderedRoots(
        roots,
        ProjectMarkdownLoader.DefaultMaxBytesPerFile,
        ctx.Trace);
      if (!string.IsNullOrWhiteSpace(block))
      {
        systemParts.Add(block.Trim());
      }
    }

    systemParts.Add(resolver.ResolveTemplate(config.SystemPrompt, state, ExtendForgeContext));

    if (config.InjectSkillsCatalog)
    {
      var catalog = SkillCatalog.Build(ctx.Trace);
      if (!string.IsNullOrWhiteSpace(catalog))
      {
        systemParts.Add(catalog.Trim());
      }
    }

    var system = string.Join("\n\n", systemParts);
    var user = resolver.ResolveTemplate(config.UserPrompt, state, ExtendForgeContext);

    List<JsonNode> messages;
    bool nudged;
    if (isResume)
    {
      messages = new List<JsonNode>(resumeState!.Messages);
      nudged = resumeState.Nudged;
    }
    else
    {
      messages = new List<JsonNode>
      {
        new JsonObject { ["role"] = "system", ["content"] = system },
        new JsonObject { ["role"] = "user", ["content"] = user }
      };
      nudged = false;
    }

    var toolSpecs = BuildToolSpecs(config, tools, outputSchema);

    int? lastActualPromptTokens = null;
    int? lastEstimatedTokens = null;
    bool compactionFallbackWarned = false;

    var bashStarted = false;

    var callerIoBudget = config.CallerIo is not null
      ? new CallerIoBudget()
      : null;

    if (config.Bash is not null)
    {
      if (bashLifecycle is null)
      {
        throw new ConfigException(
          "Agent declares a `bash:` block but no IBashContainerLifecycle was supplied to AgentRunner.RunAsync. Pass the DI-resolved instance from the caller (AgentCommand / PipelineExecutor).");
      }

      await bashLifecycle.StartForRunAsync(
        ctx.RunId,
        ctx.StageDir,
        ctx.RunWorkspace,
        config.Bash,
        ctx.Trace,
        ct).ConfigureAwait(false);
      bashStarted = true;
    }

    try
    {
    for (var iter = startIter; iter < maxIter; iter++)
    {
      ct.ThrowIfCancellationRequested();
      var interjection = isChat ? ctx.CallerIo?.TryTakeUserInterjection() : null;
      if (interjection is not null)
      {
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = interjection });
        ctx.Trace.Trace(new UserInterjectionEvent { Iteration = iter, Length = interjection.Length });
      }
      ctx.Trace.Trace(new AgentIterationEvent { Index = iter });

      if (config.Compaction?.Enabled == true)
      {
        try
        {
          var effectiveLlmConfig = llmConfig ?? new LiteLlmConfig();
          var shouldCompact = CompactionTrigger.ShouldCompact(
            messages, config.Compaction, config.Model, effectiveLlmConfig,
            lastActualPromptTokens, lastEstimatedTokens, out var fallbackThreshold);

          if (fallbackThreshold is not null && !compactionFallbackWarned)
          {
            compactionFallbackWarned = true;
            ctx.Trace.Trace(new CompactionFallbackWarningEvent
            {
              Model = config.Model,
              FallbackThreshold = fallbackThreshold.Value
            });
            ctx.Logger.LogWarning(
              "Model '{Model}' not found in model_context; compaction threshold defaulting to {Threshold} tokens.",
              config.Model, fallbackThreshold.Value);
          }

          if (shouldCompact)
          {
            var compactionResult = await ContextCompactor.CompactAsync(
              messages, config.Compaction, ctx, iter, ct).ConfigureAwait(false);
            PairingInvariant.Check(compactionResult.Messages);
            messages = compactionResult.Messages;
            lastEstimatedTokens = compactionResult.EstimatedTokensAfter;
            var compEvent = ContextCompactor.BuildEvent(compactionResult, iter, config.Compaction.Strategy);
            ctx.Trace.Trace(compEvent);
          }
        }
        catch (AgentCompactionInvariantException ex)
        {
          ctx.Logger.LogWarning(ex, "Compaction invariant violated; skipping compaction this iteration.");
          ctx.Trace.Trace(new CompactionSkippedEvent { Iteration = iter, Reason = "invariant_violation" });
        }
      }

      var req = new CompletionRequest
      {
        Model = config.Model,
        MaxTokens = 8192,
        Messages = messages,
        Tools = toolSpecs,
        ToolChoice = "auto",
        ReasoningEffort = config.Reasoning?.Effort,
        ThinkingBudgetTokens = config.Reasoning?.ThinkingBudgetTokens
      };

      var llmSw = Stopwatch.StartNew();
      var resp = await ctx.Llm.CompleteAsync(req, ct).ConfigureAwait(false);
      llmSw.Stop();
      var choice = resp.Choices.FirstOrDefault();
      var msg = choice?.Message;
      var reasoningText = JsonNodeHelpers.NullableStr(msg?.ReasoningContent);
      var hasReasoningContent = !string.IsNullOrEmpty(reasoningText);
      var hasThinkingBlocks = msg?.ThinkingBlocks is { Count: > 0 };
      ctx.Trace.Trace(new LlmCallEvent
      {
        Iteration = iter,
        DurationMs = llmSw.ElapsedMilliseconds,
        FinishReason = choice?.FinishReason,
        PromptTokens = resp.Usage?.PromptTokens,
        CompletionTokens = resp.Usage?.CompletionTokens,
        PromptCacheHitTokens = resp.Usage?.PromptCacheHitTokens,
        PromptCacheCreationTokens = resp.Usage?.PromptCacheCreationTokens,
        ReasoningTokens = resp.Usage?.ReasoningTokens,
        ReasoningContentPresent = hasReasoningContent,
        ThinkingBlocksPresent = hasThinkingBlocks
      });

      lastActualPromptTokens = resp.Usage?.PromptTokens;

      if (msg is null)
      {
        throw new AgentException("LLM returned no choices.");
      }

      if (hasReasoningContent || hasThinkingBlocks)
      {
        var iterationDir = WorkspacePaths.IterationDir(ctx.StageDir, iter);
        var (artifactPath, bytes) = await WorkspaceIo.WriteReasoningArtifactAsync(
          iterationDir,
          reasoningText,
          msg.ThinkingBlocks,
          ct).ConfigureAwait(false);
        ctx.Trace.Trace(new ReasoningPersistedEvent
        {
          Iteration = iter,
          ArtifactPath = artifactPath,
          Bytes = bytes,
          HasThinkingBlocks = hasThinkingBlocks
        });
      }

      var assistantJson = BuildAssistantMessage(msg);
      messages.Add(assistantJson);

      var calls = msg.ToolCalls;
      if (calls is null || calls.Count == 0)
      {
        if (outputSchema is null && isChat && ctx.CallerIo is not null)
        {
          var assistantText = JsonNodeHelpers.NullableStr(msg.Content);
          if (!string.IsNullOrWhiteSpace(assistantText))
          {
            await ctx.CallerIo.WriteAssistantTextAsync(assistantText, ct).ConfigureAwait(false);
          }

          var prompt = new CallerPrompt { Question = "" };
          var reply = await ctx.CallerIo.PromptAsync(prompt, ct).ConfigureAwait(false);
          if (string.Equals(reply.Response?.Trim(), ChatExitException.ExitCommand, StringComparison.OrdinalIgnoreCase))
          {
            ctx.Trace.Trace(new ChatExitEvent { Reason = "user_exit", Iteration = iter });
            return BuildChatExitPayload(messages);
          }

          messages.Add(new JsonObject { ["role"] = "user", ["content"] = reply.Response });
          continue;
        }

        if (!nudged)
        {
          nudged = true;
          messages.Add(new JsonObject
          {
            ["role"] = "user",
            ["content"] = "You must call tools. Finish by calling submit_final with JSON arguments matching the output schema."
          });
          continue;
        }

        throw new AgentException("Model ended without tool calls and without submit_final.");
      }

      nudged = false;

      foreach (var call in calls)
      {
        if (call.Function.Name == "submit_final")
        {
          JsonNode argsNode;
          try
          {
            argsNode = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments)!;
          }
          catch (JsonException ex)
          {
            messages.Add(ToolErrorMessage(call.Id, $"Invalid JSON for submit_final: {ex.Message}"));
            continue;
          }

          try
          {
            Validator.Validate(argsNode, outputSchema!);
          }
          catch (ValidationException vex)
          {
            messages.Add(ToolErrorMessage(call.Id, vex.Message));
            continue;
          }

          await WriteStateSnapshotAsync(iter, messages, nudged, ledger, ctx, ct).ConfigureAwait(false);
          VerifyDiffOrThrow(config, ctx, ledger, argsNode);
          return argsNode;
        }

        ITool tool;
        try
        {
          tool = tools.Require(call.Function.Name);
        }
        catch (ConfigException)
        {
          messages.Add(ToolErrorMessage(call.Id, $"Unknown tool: {call.Function.Name}"));
          continue;
        }

        JsonNode toolInput;
        try
        {
          toolInput = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments)!;
        }
        catch (JsonException ex)
        {
          messages.Add(ToolErrorMessage(call.Id, $"Invalid tool arguments JSON: {ex.Message}"));
          continue;
        }

        if (TryEnforceCallerIoBudget(call.Function.Name, config.CallerIo, callerIoBudget, messages, call.Id, iter, call.Function.Arguments ?? string.Empty, ctx))
          continue;

        JsonNode toolOut;
        string? toolError = null;
        var toolSw = Stopwatch.StartNew();
        try
        {
          var raw = await tool.ExecuteAsync(toolInput, ctx, ct).ConfigureAwait(false);
          toolOut = await ToolResultCapper.CapAsync(raw, ctx, ct).ConfigureAwait(false);
        }
        catch (ChatExitException) when (isChat)
        {
          toolSw.Stop();
          ctx.Trace.Trace(new ChatExitEvent { Reason = "user_exit", Iteration = iter });
          return BuildChatExitPayload(messages);
        }
        catch (Exception ex)
        {
          toolError = ex.Message;
          ctx.Logger.LogWarning(ex, "Tool {Tool} failed", call.Function.Name);
          toolOut = new JsonObject { ["error"] = ex.Message };
        }
        finally
        {
          toolSw.Stop();
          ctx.Trace.Trace(new ToolCallEvent
          {
            Iteration = iter,
            CallId = call.Id,
            ToolName = call.Function.Name,
            ArgsHash = ShortHash(call.Function.Arguments ?? string.Empty),
            DurationMs = toolSw.ElapsedMilliseconds,
            Error = toolError
          });
        }

        if (isChat && ctx.CallerIo is not null)
        {
          var summary = tool.TrySummarize(toolInput, toolOut, toolError);
          await ctx.CallerIo.WriteToolCallSummaryAsync(iter, call.Function.Name, summary, toolError is not null, ct).ConfigureAwait(false);
        }

        messages.Add(new JsonObject
        {
          ["role"] = "tool",
          ["tool_call_id"] = call.Id,
          ["content"] = toolOut.ToJsonString(JsonSerializationDefaults.CamelCaseTool)
        });
      }

      if (callerIoBudget is not null)
      {
        foreach (var call in calls)
        {
          IncrementCallerIoBudget(call.Function.Name, callerIoBudget);
        }
      }

      await WriteStateSnapshotAsync(iter, messages, nudged, ledger, ctx, ct).ConfigureAwait(false);
    }

    var effectiveMax = maxIterationsOverride ?? config.MaxIterations;
    throw new AgentException($"MaxIterations ({effectiveMax}) exceeded without submit_final.");
    }
    finally
    {
      if (bashStarted && bashLifecycle is not null)
      {
        await bashLifecycle.StopForRunAsync(ctx.RunId, "agent_run_ended", ctx.Trace, CancellationToken.None).ConfigureAwait(false);
      }
    }
  }

  private static void VerifyDiffOrThrow(
    AgentConfig config,
    ToolContext ctx,
    AgentWriteLedger ledger,
    JsonNode outputRoot)
  {
    var cfg = config.DiffVerification ?? new AgentDiffVerificationConfig();
    if (!cfg.Enabled)
    {
      ctx.Trace.Trace(new AgentDiffVerificationEvent
      {
        Declared = Array.Empty<string>(),
        ActuallyWritten = Array.Empty<string>(),
        Missing = Array.Empty<string>(),
        Extra = Array.Empty<string>(),
        Verdict = "skipped"
      });
      return;
    }

    var declared = CollectDeclaredFilesModified(outputRoot);

    var realWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in ledger.Entries)
    {
      if (entry.WasNoOp)
      {
        continue;
      }

      realWrites.Add(NormalizeDeclaredRelative(entry.RequestedPath));
    }

    if (cfg.AllowRunspaceOnly && realWrites.Count == 0)
    {
      ctx.Trace.Trace(new AgentDiffVerificationEvent
      {
        Declared = declared.ToArray(),
        ActuallyWritten = Array.Empty<string>(),
        Missing = Array.Empty<string>(),
        Extra = Array.Empty<string>(),
        Verdict = "pass"
      });
      return;
    }

    var missing = declared.Except(realWrites, StringComparer.OrdinalIgnoreCase).ToArray();
    var extra = realWrites.Except(declared, StringComparer.OrdinalIgnoreCase).ToArray();

    if (missing.Length > 0 || extra.Length > 0)
    {
      ctx.Trace.Trace(new AgentDiffVerificationEvent
      {
        Declared = declared.ToArray(),
        ActuallyWritten = realWrites.ToArray(),
        Missing = missing,
        Extra = extra,
        Verdict = "reject"
      });
      throw new AgentDiffMismatchException(missing, extra);
    }

    ctx.Trace.Trace(new AgentDiffVerificationEvent
    {
      Declared = declared.ToArray(),
      ActuallyWritten = realWrites.ToArray(),
      Missing = Array.Empty<string>(),
      Extra = Array.Empty<string>(),
      Verdict = "pass"
    });
  }

  private static async Task WriteStateSnapshotAsync(
    int iteration,
    List<JsonNode> messages,
    bool nudged,
    AgentWriteLedger ledger,
    ToolContext ctx,
    CancellationToken ct)
  {
    var iterationDir = WorkspacePaths.IterationDir(ctx.StageDir, iteration);
    Directory.CreateDirectory(iterationDir);
    var snapshotPath = Path.Combine(iterationDir, "state.json");

    var snapshot = new JsonObject
    {
      ["iter"] = iteration,
      ["nudged"] = nudged,
      ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray()),
      ["ledger"] = new JsonArray(ledger.Entries.Select(e => JsonSerializer.SerializeToNode(e, JsonSerializationDefaults.General)).ToArray())
    };

    var json = snapshot.ToJsonString(JsonSerializationDefaults.Indented);
    var bytes = Encoding.UTF8.GetByteCount(json);
    await WorkspaceIo.WriteJsonAtomicAsync(snapshotPath, snapshot, ct).ConfigureAwait(false);

    ctx.Trace.Trace(new AgentStateSnapshotEvent
    {
      Iteration = iteration,
      Path = snapshotPath,
      Bytes = bytes
    });
  }

  private static HashSet<string> CollectDeclaredFilesModified(JsonNode outputRoot)
  {
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (outputRoot is not JsonObject obj)
    {
      return set;
    }

    if (obj["files_modified"] is not JsonArray arr)
    {
      return set;
    }

    foreach (var node in arr)
    {
      if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
      {
        set.Add(NormalizeDeclaredRelative(s));
      }
    }

    return set;
  }

  private static string NormalizeDeclaredRelative(string s) =>
    s.Replace('\\', '/').TrimStart('/', '.');

  private static JsonObject ToolErrorMessage(string toolCallId, string err) =>
    new()
    {
      ["role"] = "tool",
      ["tool_call_id"] = toolCallId,
      ["content"] = JsonSerializer.Serialize(new { error = err }, JsonSerializationDefaults.CamelCaseTool)
    };

  private static string ShortHash(string input)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
  }

  private static JsonObject BuildAssistantMessage(ChatMessagePayload msg)
  {
    var o = new JsonObject { ["role"] = "assistant" };
    if (msg.Content is not null)
    {
      o["content"] = msg.Content.DeepClone();
    }

    if (msg.ReasoningContent is not null)
    {
      o["reasoning_content"] = msg.ReasoningContent.DeepClone();
    }

    if (msg.ThinkingBlocks is not null)
    {
      o["thinking_blocks"] = msg.ThinkingBlocks.DeepClone();
    }

    if (msg.ToolCalls is { Count: > 0 })
    {
      var arr = new JsonArray();
      foreach (var tc in msg.ToolCalls)
      {
        arr.Add(new JsonObject
        {
          ["id"] = tc.Id,
          ["type"] = tc.Type,
          ["function"] = new JsonObject
          {
            ["name"] = tc.Function.Name,
            ["arguments"] = tc.Function.Arguments
          }
        });
      }

      o["tool_calls"] = arr;
    }

    return o;
  }

  private static List<ToolSpec> BuildToolSpecs(AgentConfig config, ToolRegistry registry, JsonSchema? outputSchema)
  {
    var list = new List<ToolSpec>();
    foreach (var name in config.Tools)
    {
      var t = registry.Require(name);
      list.Add(new ToolSpec
      {
        Type = "function",
        Function = new FunctionToolSpec
        {
          Name = t.Name,
          Description = t.Description,
          Parameters = t.InputSchema.ToJsonNode()
        }
      });
    }

    if (outputSchema is not null)
    {
      list.Add(new ToolSpec
      {
        Type = "function",
        Function = new FunctionToolSpec
        {
          Name = "submit_final",
          Description = "Submit the final structured result for this agent.",
          Parameters = outputSchema.ToJsonNode()
        }
      });
    }

    return list;
  }

  private static bool TryEnforceCallerIoBudget(
    string toolName,
    CallerIoConfig? config,
    CallerIoBudget? budget,
    List<JsonNode> messages,
    string callId,
    int iter,
    string argsJson,
    ToolContext ctx)
  {
    if (config is null || budget is null) return false;

    int used, max;
    string label;
    switch (toolName)
    {
      case "ask_caller":
        used = budget.PromptsUsed; max = config.MaxPrompts; label = "prompts";
        break;
      case "notify_caller":
        used = budget.NotificationsUsed; max = config.MaxNotifications; label = "notifications";
        break;
      case "request_approval":
        used = budget.ApprovalsUsed; max = config.MaxApprovals; label = "approvals";
        break;
      default:
        return false;
    }

    if (used >= max)
    {
      if (string.Equals(config.OnBudgetExceeded, "silent", StringComparison.OrdinalIgnoreCase))
        return false;

      var err = $"caller_io budget exceeded: max_{label}={max} already used.";
      messages.Add(ToolErrorMessage(callId, err));
      ctx.Trace.Trace(new ToolCallEvent
      {
        Iteration = iter,
        CallId = callId,
        ToolName = toolName,
        ArgsHash = ShortHash(argsJson),
        DurationMs = 0,
        Error = err
      });
      return true;
    }

    return false;
  }

  private static void IncrementCallerIoBudget(string toolName, CallerIoBudget budget)
  {
    if (toolName == "ask_caller") budget.PromptsUsed++;
    else if (toolName == "notify_caller") budget.NotificationsUsed++;
    else if (toolName == "request_approval") budget.ApprovalsUsed++;
  }

  private static JsonObject BuildChatExitPayload(List<JsonNode> messages)
  {
    return new JsonObject
    {
      ["exited"] = true,
      ["message_count"] = messages.Count
    };
  }
}
