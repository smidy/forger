using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Pipeline;
using Forge.Llm;
using Forge.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class AgentCommand
{
  public static async Task<int> ExecuteAsync(IServiceProvider sp, AgentSettings settings)
  {
    var app = sp.GetRequiredService<ForgeAppState>();
    var ct = app.CancellationToken;
    var path = PluginPaths.FindAgent(app.ForgeHome, settings.Name);
    if (path is null)
    {
      AnsiConsole.MarkupLine($"[red]Agent '{settings.Name}' not found under search paths (see `forge list agents`).[/]");
      return 1;
    }

    var cfg = AgentConfig.LoadFromYamlFile(path);

    var isChat = settings.Chat;

    if (isChat)
    {
      if (Console.IsInputRedirected)
      {
        AnsiConsole.MarkupLine("[red]`--chat` requires a TTY (stdin must not be redirected).[/]");
        return 1;
      }

      var policy = CallerPolicyParser.Resolve(settings.Callers);
      if (policy is not null)
      {
        if (policy.OnPrompt == PromptBehavior.FailFast ||
            policy.OnPrompt == PromptBehavior.Defer)
        {
          AnsiConsole.MarkupLine("[red]`--chat` conflicts with a deferral/fail-fast caller policy. Use `--callers auto-allow` or omit `--callers`.[/]");
          return 1;
        }

        if (policy.OnApproval == ApprovalBehavior.AutoDeny && policy.OnPrompt == PromptBehavior.SilentEmpty)
        {
          AnsiConsole.MarkupLine("[red]`--chat` conflicts with a caller policy that blocks all caller interaction. Use `--callers auto-allow` or omit `--callers`.[/]");
          return 1;
        }
      }

      if (cfg.OutputSchema is not null)
      {
        AnsiConsole.MarkupLine("[grey]Note: agent has an output_schema — chat mode will run with submit_final (structured agent).[/]");
      }
    }

    string inputJson;
    if (isChat)
    {
      var firstMessage = AnsiConsole.Prompt(
        new TextPrompt<string>($"[green]{cfg.Name}>[/]")
          .AllowEmpty());
      inputJson = JsonSerializer.Serialize(new { message = firstMessage }, JsonSerializationDefaults.General);
    }
    else
    {
      inputJson = await InputReader.ReadInputJsonAsync(settings.Input).ConfigureAwait(false);
    }

    var input = JsonNode.Parse(inputJson)!;
    var runId = RunIdGenerator.Generate(cfg.Name);
    var runRoot = WorkspacePaths.RunRoot(app.ForgeHome, runId);
    Directory.CreateDirectory(runRoot);
    var stageDir = WorkspacePaths.StageDir(runRoot, "agent");
    Directory.CreateDirectory(stageDir);

    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.InputPath(runRoot), input, ct).ConfigureAwait(false);
    await WriteStatusAsync(runRoot, "running", cfg.Name, runId, error: null, ct).ConfigureAwait(false);

    string? failureMessage = null;
    await using var trace = new TraceSink(WorkspacePaths.TracePath(runRoot));
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("forge.agent");

    var syntheticPipeline = SyntheticAgentPlan.BuildSyntheticPipeline(cfg.Name);
    await PipelineExecutor.WritePlanJsonAsync(
      syntheticPipeline,
      input,
      runRoot,
      app.ForgeHome,
      sp.GetRequiredService<ToolRegistry>(),
      trace,
      ct).ConfigureAwait(false);

    var toolIdx = 0;
    var ctx = new ToolContext(
      runId,
      runRoot,
      stageDir,
      "agent",
      null,
      sp.GetRequiredService<ILlmClient>(),
      trace,
      log,
      ct,
      () => ++toolIdx);

    ctx = WireCallerIo(ctx, settings, trace, log, stageDir, isChat);

    if (isChat && ctx.CallerIo is ConsoleCallerIo cci)
    {
      RegisterCtrlCHandler(cci, ct);
    }

    var sw = Stopwatch.StartNew();
    try
    {
      var result = await AgentRunner.RunAsync(
        cfg,
        input,
        ctx,
        sp.GetRequiredService<ToolRegistry>(),
        path,
        ct,
        llmConfig: sp.GetRequiredService<LiteLlmConfig>(),
        bashLifecycle: sp.GetService<Forge.Tools.Docker.IBashContainerLifecycle>(),
        isChat: isChat).ConfigureAwait(false);

      if (isChat && IsChatExitPayload(result))
      {
        await WriteStatusAsync(runRoot, "exited", cfg.Name, runId, error: null, ct).ConfigureAwait(false);
        sw.Stop();
        if (settings.Human)
        {
          AnsiConsole.MarkupLine($"[green]Chat exited (run {runId}).[/]");
        }
        else
        {
          Console.WriteLine(result.ToJsonString());
        }
        return 0;
      }

      await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.ResultPath(runRoot), result, ct).ConfigureAwait(false);
      await WriteStatusAsync(runRoot, "completed", cfg.Name, runId, error: null, ct).ConfigureAwait(false);
      sw.Stop();
      if (settings.Human)
      {
        HumanRenderer.RenderAgentResult(result, cfg.Name, runId, sw.Elapsed);
      }
      else
      {
        Console.WriteLine(result.ToJsonString());
      }
      return 0;
    }
    catch (OperationCanceledException) when (isChat)
    {
      failureMessage = "cancelled";
      await WriteStatusAsync(runRoot, "cancelled", cfg.Name, runId, failureMessage, ct).ConfigureAwait(false);
      trace.Trace(new ChatExitEvent { Reason = "cancelled", Iteration = -1 });
      return 130;
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
          var status = failureMessage == "cancelled" ? "cancelled" : "failed";
          await WriteStatusAsync(runRoot, status, cfg.Name, runId, failureMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
      }
    }
  }

  private static void RegisterCtrlCHandler(ConsoleCallerIo cci, CancellationToken ct)
  {
    Console.CancelKeyPress += (_, e) =>
    {
      e.Cancel = true;
      var line = AnsiConsole.Prompt(
        new TextPrompt<string>("[interrupt] Enter a nudge for the agent (or press Ctrl-C again within 5s to exit):")
          .AllowEmpty());
      cci.EnqueueNudge(line);
    };
  }

  internal static bool IsChatExitPayload(JsonNode result)
  {
    return result is JsonObject o && o.ContainsKey("exited") && o["exited"]?.GetValue<bool>() == true;
  }

  internal static ToolContext WireCallerIo(
    ToolContext ctx, AgentSettings settings, ITraceSink trace, ILogger log, string stageDir, bool isChat = false)
  {
    if (isChat)
    {
      ctx = ctx with { CallerIo = new ConsoleCallerIo() };
      return ctx;
    }

    var policy = CallerPolicyParser.Resolve(settings.Callers);
    ICallerIo? callerIo;

    if (policy is null)
    {
      if (Console.IsInputRedirected)
      {
        callerIo = null;
      }
      else
      {
        callerIo = new ConsoleCallerIo();
      }
    }
    else
    {
      callerIo = new HeadlessCallerIo(policy, stageDir);
    }

    ctx = ctx with { CallerIo = callerIo };
    return ctx;
  }

  internal static async Task WriteStatusAsync(
    string runRoot,
    string status,
    string agentName,
    string runId,
    string? error,
    CancellationToken ct)
  {
    var o = new JsonObject
    {
      ["status"] = status,
      ["agent"] = agentName,
      ["run_id"] = runId
    };
    if (error is not null)
    {
      o["error"] = error;
    }

    await WorkspaceIo.WriteJsonAtomicAsync(WorkspacePaths.StatusPath(runRoot), o, ct).ConfigureAwait(false);
  }

  internal static class HumanRenderer
  {
    public static void RenderAgentResult(JsonNode result, string name, string runId, TimeSpan elapsed)
    {
      AnsiConsole.MarkupLine($"[green]Agent {name} completed in {elapsed.TotalSeconds:F1}s (run {runId}).[/]");
      AnsiConsole.WriteLine(result.ToJsonString(new() { WriteIndented = true }));
    }
  }
}

internal sealed class AgentSettings : CommandSettings
{
  [CommandArgument(0, "<name>")]
  public string Name { get; init; } = "";

  [CommandOption("-i|--input <JSON_OR_FILE>")]
  public string? Input { get; init; }

  [CommandOption("--callers <MODE>")]
  public string? Callers { get; init; }

  [CommandOption("-H|--human")]
  public bool Human { get; init; }

  [CommandOption("--chat")]
  public bool Chat { get; init; }
}
