using System.Text.Json.Nodes;
using Forge.Agent;
using System.Collections.Generic;
using System.Linq;
using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;

namespace Forge.Pipeline;

public static class Resumer
{
  /// <summary>
  /// Loads plan.json, validates schema hashes, and returns a <see cref="RunState"/> pre-populated
  /// with outputs from completed stages. Emits <see cref="PlanDriftEvent"/> when prompt hashes differ.
  /// Throws <see cref="ConfigException"/> if plan.json is missing or if schema hashes mismatch
  /// (unless <paramref name="force"/> is true).
  /// </summary>
  public static async Task<RunState> HydrateAsync(
    string runRoot,
    PipelineConfig pipeline,
    ToolRegistry tools,
    string forgeHome,
    bool force,
    CancellationToken ct,
    int? maxIterationsOverride = null,
    IEnumerable<string>? restartStages = null)
  {
    var planPath = WorkspacePaths.PlanPath(runRoot);
    if (!File.Exists(planPath))
    {
      throw new ConfigException($"Cannot resume: plan.json not found at '{planPath}'. Run was not started with Forge or predates plan.json support.");
    }

    var planText = await File.ReadAllTextAsync(planPath, ct).ConfigureAwait(false);
    var plan = JsonNode.Parse(planText) as JsonObject
      ?? throw new ConfigException("plan.json is malformed (expected JSON object).");

    var agentsInPlan = IndexBy(plan["agents"] as JsonArray, "name");
    var changedAgents = new List<string>();

    foreach (var stage in pipeline.Stages)
    {
      if (string.IsNullOrEmpty(stage.Agent))
      {
        continue;
      }

      var agentPath = PluginPaths.FindAgent(forgeHome, stage.Agent);
      if (agentPath is null)
      {
        throw new ConfigException($"Cannot resume: agent '{stage.Agent}' not found.");
      }

      var cfg = AgentConfig.LoadFromYamlFile(agentPath);
      var schemaPayload = new JsonObject
      {
        ["name"] = cfg.Name,
        ["inputSchema"] = cfg.InputSchema.DeepClone(),
        ["outputSchema"] = cfg.OutputSchema?.DeepClone()
      };
      var currentSchemaHash = PipelineExecutor.ComputeHash(schemaPayload);

      if (!agentsInPlan.TryGetValue(cfg.Name, out var planEntry))
      {
        continue; // new stage added — will run fresh
      }

      var storedSchemaHash = planEntry["schemaHash"]?.GetValue<string>() ?? "";
      if (!string.Equals(storedSchemaHash, currentSchemaHash, StringComparison.Ordinal))
      {
        if (!force)
        {
          throw new ConfigException(
            $"Schema hash mismatch for agent '{cfg.Name}' (stored: {storedSchemaHash[..8]}…, current: {currentSchemaHash[..8]}…). " +
            "Run with --force to override.");
        }
      }

      var promptPayload = new JsonObject
      {
        ["systemPrompt"] = cfg.SystemPrompt,
        ["userPrompt"] = cfg.UserPrompt,
        ["model"] = cfg.Model,
        ["toolNames"] = new JsonArray(cfg.Tools.Select(t => JsonValue.Create(t)).Cast<JsonNode?>().ToArray()),
        ["maxIterations"] = cfg.MaxIterations
      };
      var currentPromptHash = PipelineExecutor.ComputeHash(promptPayload);
      var storedPromptHash = planEntry["promptHash"]?.GetValue<string>() ?? "";
      if (!string.Equals(storedPromptHash, currentPromptHash, StringComparison.Ordinal))
      {
        changedAgents.Add(cfg.Name);
      }
    }

    var tracePath = WorkspacePaths.TracePath(runRoot);
    await using var trace = new TraceSink(tracePath);
    if (changedAgents.Count > 0)
    {
      trace.Trace(new PlanDriftEvent { ChangedAgents = changedAgents });
    }

    var stagesInPlan = IndexBy(plan["stages"] as JsonArray, "id");
    var state = new RunState
    {
      Input = await LoadInputAsync(runRoot, ct).ConfigureAwait(false),
      Runtime = new RuntimeContext { RunId = Path.GetFileName(runRoot), Workspace = runRoot, StageDir = runRoot }
    };

    var loadTasks = pipeline.Stages
      .Where(stage =>
      {
        if (string.IsNullOrWhiteSpace(stage.FanOut)) return true;
        if (!stagesInPlan.TryGetValue(stage.Id, out var planStage)) return true;
        var storedExpr = planStage["fanOutExpr"]?.GetValue<string>() ?? "";
        return string.Equals(storedExpr, stage.FanOut.Trim(), StringComparison.Ordinal);
      })
      .Select(async stage =>
      {
        var outputPath = WorkspacePaths.StageOutputPath(WorkspacePaths.StageDir(runRoot, stage.Id));
        if (!File.Exists(outputPath)) return (stage.Id, null);
        try
        {
          var outputText = await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false);
          return (stage.Id, JsonNode.Parse(outputText));
        }
        catch
        {
          return (stage.Id, null);
        }
      });

    foreach (var (stageId, output) in await Task.WhenAll(loadTasks).ConfigureAwait(false))
    {
      if (output is not null)
      {
        state.Stages[stageId] = new StageResult { Output = output };
      }
    }

    // Determine which stages should be restarted (skip snapshots)
    var restartSet = restartStages?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

    // Emit restart events for any stage the caller forced to restart.
    foreach (var stageId in restartSet)
    {
      trace.Trace(new StageRestartedByFlagEvent { StageId = stageId });
      // Drop any previously-hydrated completed output so the stage truly re-runs.
      state.Stages.Remove(stageId);
    }

    // Scan for agent snapshots in incomplete stages
    foreach (var stage in pipeline.Stages)
    {
      if (string.IsNullOrEmpty(stage.Agent)) continue; // non-agent stage
      if (state.Stages.ContainsKey(stage.Id)) continue; // already completed
      if (restartSet.Contains(stage.Id)) continue; // forced restart
      if (!string.IsNullOrWhiteSpace(stage.FanOut)) continue; // fan-out stages not yet supported

      var stageDir = WorkspacePaths.StageDir(runRoot, stage.Id);
      var resumeState = AgentSnapshotLoader.TryLoadLatest(stageDir);
      if (resumeState is not null)
      {
        state.PendingAgentResume[stage.Id] = resumeState;
        trace.Trace(new StageResumedFromIterEvent
        {
          StageId = stage.Id,
          FromIter = resumeState.StartingIter,
          SourcePath = WorkspacePaths.StageDir(runRoot, stage.Id)
        });
      }
    }

    return state;
  }

  private static Dictionary<string, JsonObject> IndexBy(JsonArray? arr, string key)
  {
    var dict = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    if (arr is null) return dict;
    foreach (var item in arr)
    {
      if (item is JsonObject obj && obj[key]?.GetValue<string>() is { } k)
      {
        dict[k] = obj;
      }
    }

    return dict;
  }

  private static async Task<JsonNode> LoadInputAsync(string runRoot, CancellationToken ct)
  {
    var inputPath = WorkspacePaths.InputPath(runRoot);
    if (!File.Exists(inputPath))
    {
      return new JsonObject();
    }

    var text = await File.ReadAllTextAsync(inputPath, ct).ConfigureAwait(false);
    return JsonNode.Parse(text) ?? new JsonObject();
  }
}
