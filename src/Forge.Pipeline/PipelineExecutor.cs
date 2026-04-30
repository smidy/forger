using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Exceptions;
using System.Collections.Generic;
using System.Linq;
using Forge.Core.Llm;
using Forge.Core.Refs;
using Forge.Core.Schema;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools.Docker;
using Forge.Llm;
using Forge.Tools;
using Json.Schema;
using Microsoft.Extensions.Logging;

namespace Forge.Pipeline;

public sealed class PipelineResult
{
  public required JsonNode Result { get; init; }
}

public static class PipelineExecutor
{
  public static async Task<PipelineResult> RunAsync(
    PipelineConfig pipeline,
    JsonNode pipelineInput,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ILoggerFactory logFactory,
    CancellationToken ct,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var runId = RunIdGenerator.Generate(pipeline.Name);
    var runRoot = WorkspacePaths.RunRoot(forgeHome, runId);
    Directory.CreateDirectory(runRoot);
    var tracePath = WorkspacePaths.TracePath(runRoot);
    await using var trace = new TraceSink(tracePath);
    var log = logFactory.CreateLogger("forge.pipeline");

    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.InputPath(runRoot), pipelineInput, ct).ConfigureAwait(false);
    await WritePlanJsonAsync(pipeline, pipelineInput, runRoot, forgeHome, tools, trace, ct).ConfigureAwait(false);
    await WriteStatusAsync(runRoot, "running", pipeline.Name, runId, null, ct).ConfigureAwait(false);

    var state = new RunState
    {
      Input = pipelineInput,
      Runtime = new RuntimeContext { RunId = runId, Workspace = runRoot, StageDir = runRoot }
    };

    return await ExecuteCoreAsync(pipeline, state, runId, runRoot, forgeHome, llm, tools, trace, log, ct,
      bashLifecycle: bashLifecycle, llmConfig: llmConfig, createCallerIo: createCallerIo).ConfigureAwait(false);
  }

  public static async Task<PipelineResult> ResumeAsync(
    string runId,
    string runRoot,
    PipelineConfig pipeline,
    RunState hydratedState,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ILoggerFactory logFactory,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var tracePath = WorkspacePaths.TracePath(runRoot);
    await using var trace = new TraceSink(tracePath);
    var log = logFactory.CreateLogger("forge.pipeline");

    await WriteStatusAsync(runRoot, "running", pipeline.Name, runId, null, ct).ConfigureAwait(false);

    return await ExecuteCoreAsync(
      pipeline, hydratedState, runId, runRoot, forgeHome, llm, tools, trace, log, ct,
      maxIterationsOverride, restartStages, bashLifecycle, llmConfig: llmConfig, createCallerIo: createCallerIo).ConfigureAwait(false);
  }

  private static async Task<PipelineResult> ExecuteCoreAsync(
    PipelineConfig pipeline,
    RunState state,
    string runId,
    string runRoot,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ITraceSink trace,
    ILogger log,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var resolver = new Resolver();
    var dag = DagResolver.Resolve(pipeline);
    var partialStages = new ConcurrentBag<string>();

    string? failureMessage = null;
    bool needsCaller = false;
    try
    {
      foreach (var group in dag.Groups)
      {
        ct.ThrowIfCancellationRequested();
        var bag = new ConcurrentDictionary<string, StageResult>(StringComparer.Ordinal);

        // Pre-seed bag with already-completed stages from hydrated state
        foreach (var stage in group)
        {
          if (state.Stages.TryGetValue(stage.Id, out var existing))
          {
            bag[stage.Id] = existing;
          }
        }

        await Parallel.ForEachAsync(
            group,
            new ParallelOptions { MaxDegreeOfParallelism = -1, CancellationToken = ct },
            async (stage, token) =>
            {
              if (bag.ContainsKey(stage.Id))
              {
                return; // already completed
              }

              try
              {
                var stageOut = await ExecuteStageAsync(
                  pipeline,
                  stage,
                  state,
                  runId,
                  runRoot,
                  forgeHome,
                  llm,
                  tools,
                  trace,
                  log,
                  resolver,
                  token,
                  maxIterationsOverride,
                  restartStages,
                  bashLifecycle,
                  llmConfig,
                  createCallerIo).ConfigureAwait(false);
                bag[stage.Id] = new StageResult { Output = stageOut };

                if (stage.ContinueOnError && !string.IsNullOrWhiteSpace(stage.FanOut))
                {
                  if (stageOut is JsonObject obj && obj["errors"] is JsonArray errArr && errArr.Count > 0)
                  {
                    partialStages.Add(stage.Id);
                  }
                }
              }
              catch (CallerDeferredException cde)
              {
                // pending_question.json already written by HeadlessCallerIo
                await WriteStatusAsync(runRoot, "needs_caller", pipeline.Name, runId,
                  "Agent is waiting for caller input. Resume with: forge resume <run-id> --answer '<json>'", CancellationToken.None).ConfigureAwait(false);
                trace.Trace(new StageNeedsCallerEvent
                {
                  StageId = stage.Id,
                  QuestionPath = WorkspacePaths.PendingQuestionPath(WorkspacePaths.StageDir(runRoot, stage.Id))
                });
                needsCaller = true;
                failureMessage = cde.Message;
                // Re-throw to be caught by the outer handler which exits with code 7
                throw;
              }
            })
          .ConfigureAwait(false);

        foreach (var stage in group)
        {
          if (bag.TryGetValue(stage.Id, out var sr))
          {
            state.Stages[stage.Id] = sr;
          }
        }
      }

      if (needsCaller)
      {
        // Should have been re-thrown above
        throw new CallerDeferredException("{}", runRoot, null);
      }

      JsonNode resultNode;
      if (pipeline.Outputs is not null)
      {
        resultNode = resolver.ResolveDeep(pipeline.Outputs, state);
      }
      else
      {
        resultNode = new JsonObject();
      }

      if (pipeline.OutputSchema is not null)
      {
        var schema = JsonSchema.FromText(pipeline.OutputSchema.ToJsonString());
        Validator.Validate(resultNode, schema);
      }

      var resultPath = WorkspacePaths.ResultPath(runRoot);
      await WorkspaceIo.WriteJsonAtomicAsync(resultPath, resultNode, ct).ConfigureAwait(false);
      await WriteStatusAsync(runRoot, "completed", pipeline.Name, runId, null, ct).ConfigureAwait(false);

      if (!partialStages.IsEmpty)
      {
        throw new PartialFailureException(
          $"Pipeline completed with errors in {partialStages.Count} stage(s): {string.Join(", ", partialStages)}");
      }

      return new PipelineResult { Result = resultNode };
    }
    catch (CallerDeferredException)
    {
      throw;
    }
    catch (PartialFailureException)
    {
      throw;
    }
    catch (Exception ex)
    {
      failureMessage = ex.Message;
      throw;
    }
    finally
    {
      if (failureMessage is not null)
      {
        try
        {
          await WriteStatusAsync(runRoot, needsCaller ? "needs_caller" : "failed", pipeline.Name, runId, failureMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
          // best-effort
        }
      }
    }
  }

  public static async Task WritePlanJsonAsync(
    PipelineConfig pipeline,
    JsonNode pipelineInput,
    string runRoot,
    string forgeHome,
    ToolRegistry tools,
    ITraceSink trace,
    CancellationToken ct)
  {
    var agentsArr = new JsonArray();
    var toolsArr = new JsonArray();
    var stagesArr = new JsonArray();
    var agentCache = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
    var seenAgents = new HashSet<string>(StringComparer.Ordinal);
    var seenTools = new HashSet<string>(StringComparer.Ordinal);

    foreach (var stage in pipeline.Stages)
    {
      var stageObj = new JsonObject
      {
        ["id"] = stage.Id,
        ["dependencies"] = new JsonArray(stage.DependsOn.Select(d => JsonValue.Create(d)).Cast<JsonNode?>().ToArray())
      };
      stagesArr.Add(stageObj);

      if (!string.IsNullOrEmpty(stage.Agent))
      {
        var agentPath = PluginPaths.FindAgent(forgeHome, stage.Agent);
        if (agentPath is not null)
        {
          if (!agentCache.TryGetValue(agentPath, out var cfg))
          {
            cfg = AgentConfig.LoadFromYamlFile(agentPath);
            agentCache[agentPath] = cfg;
          }

          var schemaPayload = new JsonObject
          {
            ["name"] = cfg.Name,
            ["inputSchema"] = cfg.InputSchema.DeepClone(),
            ["outputSchema"] = cfg.OutputSchema?.DeepClone()
          };
          var promptPayload = new JsonObject
          {
            ["systemPrompt"] = cfg.SystemPrompt,
            ["userPrompt"] = cfg.UserPrompt,
            ["model"] = cfg.Model,
            ["toolNames"] = new JsonArray(cfg.Tools.Select(t => JsonValue.Create(t)).Cast<JsonNode?>().ToArray()),
            ["maxIterations"] = cfg.MaxIterations
          };
          if (seenAgents.Add(cfg.Name))
          {
            agentsArr.Add(new JsonObject
            {
              ["name"] = cfg.Name,
              ["resolvedPath"] = agentPath,
              ["schemaHash"] = ComputeHash(schemaPayload),
              ["promptHash"] = ComputeHash(promptPayload)
            });
          }
        }
      }
      else if (!string.IsNullOrEmpty(stage.Tool))
      {
        var tool = tools.Get(stage.Tool);
        if (tool is not null)
        {
          var schemaPayload = new JsonObject
          {
            ["name"] = tool.Name,
            ["inputSchema"] = tool.InputSchema.ToJsonNode(),
            ["outputSchema"] = tool.OutputSchema.ToJsonNode()
          };
          if (seenTools.Add(tool.Name))
          {
            toolsArr.Add(new JsonObject
            {
              ["name"] = tool.Name,
              ["schemaHash"] = ComputeHash(schemaPayload)
            });
          }
        }
      }
    }

    var pipelineSchemaPayload = new JsonObject
    {
      ["name"] = pipeline.Name,
      ["inputSchema"] = new JsonObject(),
      ["outputSchema"] = pipeline.OutputSchema?.DeepClone() ?? new JsonObject()
    };
    var pipelinePromptPayload = new JsonObject
    {
      ["name"] = pipeline.Name,
      ["version"] = pipeline.Version,
      ["stageIds"] = new JsonArray(pipeline.Stages.Select(s => JsonValue.Create(s.Id)).Cast<JsonNode?>().ToArray())
    };

    var plan = new JsonObject
    {
      ["pipeline"] = new JsonObject
      {
        ["name"] = pipeline.Name,
        ["version"] = pipeline.Version,
        ["schemaHash"] = ComputeHash(pipelineSchemaPayload),
        ["promptHash"] = ComputeHash(pipelinePromptPayload)
      },
      ["agents"] = agentsArr,
      ["tools"] = toolsArr,
      ["stages"] = stagesArr,
      ["resolvedInputs"] = pipelineInput?.DeepClone() ?? new JsonObject()
    };

    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.PlanPath(runRoot), plan, ct).ConfigureAwait(false);
    trace.Trace(new PlanWrittenEvent());
  }

  public static string ComputeHash(JsonNode node)
  {
    return CanonicalHasher.Hash(node);
  }

  private static async Task WriteStatusAsync(
    string runRoot,
    string status,
    string pipelineName,
    string runId,
    string? error,
    CancellationToken ct)
  {
    var o = new JsonObject
    {
      ["status"] = status,
      ["pipeline"] = pipelineName,
      ["run_id"] = runId
    };
    if (error is not null)
    {
      o["error"] = error;
    }

    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StatusPath(runRoot), o, ct).ConfigureAwait(false);
  }

  private static async Task<JsonNode> ExecuteStageAsync(
    PipelineConfig pipeline,
    StageConfig stage,
    RunState state,
    string runId,
    string runRoot,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ITraceSink trace,
    ILogger log,
    Resolver resolver,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var stageDir = WorkspacePaths.StageDir(runRoot, stage.Id);
    Directory.CreateDirectory(stageDir);

    if (string.IsNullOrWhiteSpace(stage.FanOut))
    {
      var rt = new RuntimeContext
      {
        RunId = runId,
        Workspace = runRoot,
        StageDir = stageDir,
        FanOutItem = null,
        FanOutIndex = null
      };
      var localState = WithRuntime(state, rt);
      var output = await RunStageBodyAsync(
        pipeline,
        stage,
        localState,
        runId,
        runRoot,
        stageDir,
        forgeHome,
        llm,
        tools,
        trace,
        log,
        resolver,
        ct, maxIterationsOverride, restartStages, bashLifecycle, llmConfig, createCallerIo).ConfigureAwait(false);
      await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StageOutputPath(stageDir), output, ct).ConfigureAwait(false);
      return output;
    }

    var fanExpr = stage.FanOut!.Trim();
    if (!fanExpr.StartsWith('$'))
    {
      throw new ConfigException($"Stage '{stage.Id}' fan_out must be a JSONPath reference starting with '$.' or '$item'.");
    }

    var fanNode = resolver.ResolveRef(fanExpr, state);
    if (fanNode is null)
    {
      throw new ConfigException($"Stage '{stage.Id}' fan_out resolved to null.");
    }

    if (fanNode is not JsonArray arr)
    {
      throw new ConfigException($"Stage '{stage.Id}' fan_out must resolve to a JSON array (got {fanNode.GetType().Name}).");
    }

    if (arr.Count == 0)
    {
      var emptyOut = new JsonObject { ["ok"] = new JsonArray() };
      await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StageOutputPath(stageDir), emptyOut, ct).ConfigureAwait(false);
      return emptyOut;
    }

    var maxConcurrency = stage.Concurrency is > 0 ? stage.Concurrency.Value : 4;

    if (stage.ContinueOnError)
    {
      var continueOut = await ExecuteFanOutContinueAsync(
        pipeline, stage, state, arr, maxConcurrency, runId, runRoot, stageDir, forgeHome,
        llm, tools, trace, log, resolver, ct, maxIterationsOverride, restartStages, bashLifecycle, llmConfig, createCallerIo).ConfigureAwait(false);
      await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StageOutputPath(stageDir), continueOut, ct).ConfigureAwait(false);
      return continueOut;
    }

    var results = new JsonNode?[arr.Count];
    await Parallel.ForEachAsync(
        Enumerable.Range(0, arr.Count),
        new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
        async (i, token) =>
        {
          var item = arr[i]!;
          var iterDir = WorkspacePaths.IterationDir(stageDir, i);
          Directory.CreateDirectory(iterDir);
          var rt = new RuntimeContext
          {
            RunId = runId,
            Workspace = runRoot,
            StageDir = iterDir,
            FanOutItem = item.DeepClone(),
            FanOutIndex = i
          };
          var localState = WithRuntime(state, rt);
          results[i] = await RunStageBodyAsync(
            pipeline,
            stage,
            localState,
            runId,
            runRoot,
            iterDir,
            forgeHome,
            llm,
            tools,
            trace,
            log,
            resolver,
            token, maxIterationsOverride, restartStages, bashLifecycle, llmConfig, createCallerIo).ConfigureAwait(false);
          var fanIterOutputPath = Path.Combine(iterDir, "output.json");
          await WorkspaceIo.WriteJsonAtomicAsync(fanIterOutputPath, results[i]!, token).ConfigureAwait(false);
        })
      .ConfigureAwait(false);
    var ok = new JsonArray();
    foreach (var r in results)
    {
      ok.Add(r!.DeepClone());
    }

    var fanOut = new JsonObject { ["ok"] = ok };
    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StageOutputPath(stageDir), fanOut, ct).ConfigureAwait(false);
    return fanOut;
  }

  private static async Task<JsonNode> ExecuteFanOutContinueAsync(
    PipelineConfig pipeline,
    StageConfig stage,
    RunState state,
    JsonArray arr,
    int maxConcurrency,
    string runId,
    string runRoot,
    string stageDir,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ITraceSink trace,
    ILogger log,
    Resolver resolver,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var results = new (JsonNode? Output, Exception? Error)[arr.Count];
    await Parallel.ForEachAsync(
        Enumerable.Range(0, arr.Count),
        new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
        async (i, token) =>
        {
          var item = arr[i]!;
          var iterDir = WorkspacePaths.IterationDir(stageDir, i);
          Directory.CreateDirectory(iterDir);
          var rt = new RuntimeContext
          {
            RunId = runId,
            Workspace = runRoot,
            StageDir = iterDir,
            FanOutItem = item.DeepClone(),
            FanOutIndex = i
          };
          var localState = WithRuntime(state, rt);
          try
          {
            var output = await RunStageBodyAsync(
              pipeline,
              stage,
              localState,
              runId,
              runRoot,
              iterDir,
              forgeHome,
              llm,
              tools,
              trace,
              log,
              resolver,
              token, maxIterationsOverride, restartStages, bashLifecycle, llmConfig, createCallerIo).ConfigureAwait(false);
            results[i] = (output, null);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
            results[i] = (null, ex);
          }
        })
      .ConfigureAwait(false);

    var ok = new JsonArray();
    var errors = new JsonArray();
    foreach (var (i, (output, ex)) in results.Select((r, i) => (i, r)))
    {
      if (ex is not null)
      {
        errors.Add(new JsonObject { ["index"] = i, ["message"] = ex.Message });
      }
      else
      {
        ok.Add(output!);
      }
    }

    return new JsonObject { ["ok"] = ok, ["errors"] = errors };
  }

  private static RunState WithRuntime(RunState src, RuntimeContext rt)
  {
    var s = new RunState { Input = src.Input, Runtime = rt };
    foreach (var kv in src.Stages)
    {
      s.Stages[kv.Key] = kv.Value;
    }
    foreach (var kv in src.PendingAgentResume)
    {
      s.PendingAgentResume[kv.Key] = kv.Value;
    }

    return s;
  }

  private static async Task<JsonNode> RunStageBodyAsync(
    PipelineConfig pipeline,
    StageConfig stage,
    RunState state,
    string runId,
    string runRoot,
    string effectiveStageDir,
    string forgeHome,
    ILlmClient llm,
    ToolRegistry tools,
    ITraceSink trace,
    ILogger log,
    Resolver resolver,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null,
    IBashContainerLifecycle? bashLifecycle = null,
    LiteLlmConfig? llmConfig = null,
    Func<string, ICallerIo>? createCallerIo = null)
  {
    var toolIdx = 0;
    if (!string.IsNullOrEmpty(stage.Agent))
    {
      var agentPath = PluginPaths.FindAgent(forgeHome, stage.Agent);
      if (agentPath is null)
      {
        throw new ConfigException($"Agent not found: {stage.Agent}");
      }

      var cfg = AgentConfig.LoadFromYamlFile(agentPath);
      var stageInput = stage.Input is null ? new JsonObject() : resolver.ResolveDeep(stage.Input, state);
      var ctx = new ToolContext(runId, runRoot, effectiveStageDir, stage.Id, state.Runtime.FanOutIndex, llm, trace, log, ct, () => ++toolIdx)
      {
        CallerIo = createCallerIo?.Invoke(effectiveStageDir)
      };
      state.PendingAgentResume.TryGetValue(stage.Id, out var resumeState);
      return await AgentRunner.RunAsync(
        cfg, stageInput, ctx, tools, agentPath, ct,
        resumeState: resumeState, maxIterationsOverride: maxIterationsOverride,
        bashLifecycle: bashLifecycle, llmConfig: llmConfig).ConfigureAwait(false);
    }

    if (!string.IsNullOrEmpty(stage.Tool))
    {
      var stageInput = stage.Input is null ? new JsonObject() : resolver.ResolveDeep(stage.Input, state);
      var t = tools.Require(stage.Tool);
      var ctx = new ToolContext(runId, runRoot, effectiveStageDir, stage.Id, state.Runtime.FanOutIndex, llm, trace, log, ct, () => ++toolIdx)
      {
        CallerIo = createCallerIo?.Invoke(effectiveStageDir)
      };
      return await t.ExecuteAsync(stageInput, ctx, ct).ConfigureAwait(false);
    }

    throw new ConfigException($"Stage '{stage.Id}' has neither agent nor tool.");
  }
}
