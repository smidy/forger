using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Core.Types;
using System.Collections.Generic;
using Forge.Core.Workspace;
using Forge.Pipeline;
using Forge.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class ResumeCommand
{
  public static async Task<int> ExecuteAsync(IServiceProvider sp, ResumeSettings settings)
  {
    var app = sp.GetRequiredService<ForgeAppState>();
    var ct = app.CancellationToken;
    var runId = settings.RunId.Trim();
    if (string.IsNullOrEmpty(runId))
    {
      AnsiConsole.MarkupLine("[red]run-id is required.[/]");
      return 1;
    }

    var runRoot = WorkspacePaths.RunRoot(app.ForgeHome, runId);
    if (!Directory.Exists(runRoot))
    {
      AnsiConsole.MarkupLine($"[red]Run directory not found: {runRoot}[/]");
      return 1;
    }

    // Check if the run is in needs_caller state
    var statusPath = WorkspacePaths.StatusPath(runRoot);
    string? runStatus = null;
    if (File.Exists(statusPath))
    {
      try
      {
        var statusJson = JsonNode.Parse(await File.ReadAllTextAsync(statusPath, ct).ConfigureAwait(false));
        runStatus = statusJson?["status"]?.GetValue<string>();
      }
      catch { }
    }

    // Validate --answer flag
    if (runStatus == "needs_caller")
    {
      if (string.IsNullOrWhiteSpace(settings.Answer))
      {
        AnsiConsole.MarkupLine("[red]Run is waiting on caller input — supply --answer '<json>'.[/]");
        return 1;
      }
    }
    else
    {
      if (!string.IsNullOrWhiteSpace(settings.Answer))
      {
        AnsiConsole.MarkupLine("[red]Run is not waiting on caller input (status is not 'needs_caller').[/]");
        return 1;
      }
    }

    // Build caller-IO for answer-resume
    Func<string, ICallerIo>? createCallerIo = null;
    if (!string.IsNullOrWhiteSpace(settings.Answer) && runStatus == "needs_caller")
    {
      var answerJson = settings.Answer!.Trim();
      // Create a factory that wraps HeadlessCallerIo with PreAnsweredCallerIo
      var capturedAnswer = answerJson;
      createCallerIo = stageDir => CreateAnswerCallerIo(capturedAnswer, stageDir);
    }

    var planPath = WorkspacePaths.PlanPath(runRoot);
    if (!File.Exists(planPath))
    {
      AnsiConsole.MarkupLine($"[red]plan.json not found in run directory. Cannot resume.[/]");
      return 1;
    }

    string pipelineName;
    try
    {
      var planText = await File.ReadAllTextAsync(planPath).ConfigureAwait(false);
      var plan = JsonNode.Parse(planText) as JsonObject;
      pipelineName = plan?["pipeline"]?["name"]?.GetValue<string>() ?? "";
    }
    catch
    {
      AnsiConsole.MarkupLine("[red]plan.json is unreadable.[/]");
      return 1;
    }

    if (string.IsNullOrEmpty(pipelineName))
    {
      AnsiConsole.MarkupLine("[red]plan.json does not contain a pipeline name.[/]");
      return 1;
    }

    // `forge agent` runs encode their origin as `pipeline.name = "agent:<name>"`.
    var agentName = SyntheticAgentPlan.TryExtractAgentName(pipelineName);
    if (agentName is null)
    {
      AnsiConsole.MarkupLine($"[red]plan.json references '{pipelineName}', which is not an agent run. Only agent runs can be resumed.[/]");
      return 1;
    }

    var agentPath = PluginPaths.FindAgent(app.ForgeHome, agentName);
    if (agentPath is null)
    {
      AnsiConsole.MarkupLine($"[red]Agent '{agentName}' (referenced by run plan.json) not found (see `forge list agents`).[/]");
      return 1;
    }

    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(agentName);

    var tools = sp.GetRequiredService<ToolRegistry>();

    RunState hydratedState;
    try
    {
      hydratedState = await Resumer.HydrateAsync(
        runRoot,
        pipeline,
        tools,
        app.ForgeHome,
        settings.Force,
        ct,
        settings.MaxIterations,
        settings.RestartStages).ConfigureAwait(false);
    }
    catch (Core.Exceptions.ConfigException ex)
    {
      AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
      return 1;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await PipelineExecutor.ResumeAsync(
      runId,
      runRoot,
      pipeline,
      hydratedState,
      app.ForgeHome,
      sp.GetRequiredService<Core.Llm.ILlmClient>(),
      tools,
      sp.GetRequiredService<ILoggerFactory>(),
      ct,
      settings.MaxIterations,
      settings.RestartStages,
      bashLifecycle: sp.GetService<Forge.Tools.Docker.IBashContainerLifecycle>(),
      createCallerIo: createCallerIo).ConfigureAwait(false);
    sw.Stop();

    result = await ProjectAgentStageOutputAsync(result, agentName, runRoot, ct).ConfigureAwait(false);

    if (settings.Human)
    {
      HumanRenderer.RenderResumeResult(result, pipeline.Name, runId, sw.Elapsed);
    }
    else
    {
      Console.WriteLine(result.Result.ToJsonString());
    }
    return 0;
  }

  /// <summary>
  /// For synthetic-agent runs (<c>pipeline.name = "agent:&lt;name&gt;"</c>), the
  /// synthetic pipeline declares no <c>outputs:</c>, so
  /// <see cref="PipelineExecutor.ResumeAsync"/> wrote <c>{}</c> to
  /// <c>result.json</c> and returned <c>{}</c> as <see cref="PipelineResult.Result"/>.
  /// Mirror the symmetric <c>forge agent</c> path: surface the stage's
  /// <c>submit_final</c> output (already on disk at
  /// <c>stages/agent/output.json</c>) as the run result, and overwrite
  /// <c>result.json</c> to match. Pipelines (non-agent) are returned untouched.
  /// </summary>
  internal static async Task<PipelineResult> ProjectAgentStageOutputAsync(
    PipelineResult original, string? agentName, string runRoot, CancellationToken ct)
  {
    if (agentName is null)
    {
      return original;
    }

    var stageOutputPath = WorkspacePaths.StageOutputPath(
      WorkspacePaths.StageDir(runRoot, SyntheticAgentPlan.StageId));
    if (!File.Exists(stageOutputPath))
    {
      return original;
    }

    var stageJson = await File.ReadAllTextAsync(stageOutputPath, ct).ConfigureAwait(false);
    var stageOutput = JsonNode.Parse(stageJson);
    if (stageOutput is null)
    {
      return original;
    }

    await WorkspaceIo.WriteJsonAtomicAsync(
      WorkspacePaths.ResultPath(runRoot), stageOutput, ct).ConfigureAwait(false);
    return new PipelineResult { Result = stageOutput };
  }

  private static ICallerIo CreateAnswerCallerIo(string answerJson, string stageDir)
  {
    // Try to parse as prompt response first
    try
    {
      var node = JsonNode.Parse(answerJson);
      if (node is JsonObject obj)
      {
        // Check for approval answer shape: { "allowed": bool, "reason"?: string }
        if (obj["allowed"] is JsonValue av && (av.TryGetValue(out bool allowed)))
        {
          var reason = obj["reason"]?.GetValue<string>() ?? "resume-answer";
          var decision = new ApprovalDecision { Allowed = allowed, Reason = reason };
          var baseIo = new HeadlessCallerIo(CallerPolicy.Default, stageDir);
          return new PreAnsweredCallerIo(baseIo, decision);
        }

        // Check for prompt answer shape: { "response": string }
        if (obj["response"] is JsonValue rv && rv.TryGetValue(out string? resp))
        {
          var baseIo = new HeadlessCallerIo(CallerPolicy.Default, stageDir);
          return new PreAnsweredCallerIo(baseIo, resp);
        }
      }
    }
    catch (JsonException)
    {
      // Not valid JSON; treat as raw string response
    }

    // Treat as raw response string
    var defaultIo = new HeadlessCallerIo(CallerPolicy.Default, stageDir);
    return new PreAnsweredCallerIo(defaultIo, answerJson);
  }
}

internal sealed class ResumeSettings : CommandSettings
{
  [CommandArgument(0, "<run-id>")]
  public string RunId { get; init; } = "";

  [CommandOption("--force|-f")]
  public bool Force { get; init; }

  /// <summary>
  /// JSON answer for a run in <c>needs_caller</c> status.
  /// For <c>ask_caller</c>: <c>{"response":"..."}</c>.
  /// For <c>request_approval</c>: <c>{"allowed":true,"reason":"..."}</c>.
  /// </summary>
  [CommandOption("--answer")]
  public string? Answer { get; init; }

  [CommandOption("-H|--human")]
  public bool Human { get; init; }

  [CommandOption("--max-iterations")]
  public int? MaxIterations { get; init; }

  [CommandOption("--restart-stage")]
  public string[]? RestartStages { get; init; }
}
