using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Core.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class RunsSubcommands
{
  public static Task<int> ListAsync(IServiceProvider sp, RunsListSettings settings)
  {
    var app = sp.GetRequiredService<ForgeAppState>();
    var runsDir = Path.Combine(app.ForgeHome, "runs");
    if (!Directory.Exists(runsDir))
    {
      if (settings.Human)
      {
        AnsiConsole.MarkupLine("[grey]No runs.[/]");
      }
      else
      {
        AnsiConsole.WriteLine("[]");
      }
      return Task.FromResult(0);
    }

    var rows = new JsonArray();
    foreach (var dir in Directory.GetDirectories(runsDir).OrderByDescending(Directory.GetLastWriteTimeUtc))
    {
      var runId = Path.GetFileName(dir);
      var row = new JsonObject { ["run_id"] = runId, ["path"] = dir };
      var statusPath = WorkspacePaths.StatusPath(dir);
      if (File.Exists(statusPath))
      {
        try
        {
          var st = JsonNode.Parse(File.ReadAllText(statusPath));
          row["status"] = st;
        }
        catch
        {
          row["status"] = "unreadable";
        }
      }
      else
      {
        row["status"] = null;
      }

      rows.Add(row);
    }

    if (settings.Human)
    {
      HumanRenderer.RenderRunsList(rows);
    }
    else
    {
      Console.Write(rows.ToJsonString(JsonSerializationDefaults.Indented));
    }
    return Task.FromResult(0);
  }

  public static async Task<int> ShowAsync(IServiceProvider sp, RunsShowSettings settings)
  {
    var app = sp.GetRequiredService<ForgeAppState>();
    var runId = settings.RunId.Trim();
    if (string.IsNullOrEmpty(runId))
    {
      AnsiConsole.MarkupLine("[red]run-id is required.[/]");
      return 1;
    }

    var root = WorkspacePaths.RunRoot(app.ForgeHome, runId);
    if (!Directory.Exists(root))
    {
      AnsiConsole.MarkupLine($"[red]Run directory not found: {root}[/]");
      return 1;
    }

    var doc = new JsonObject
    {
      ["run_id"] = runId,
      ["path"] = root
    };

    var statusPath = WorkspacePaths.StatusPath(root);
    if (File.Exists(statusPath))
    {
      try { doc["status"] = JsonNode.Parse(await File.ReadAllTextAsync(statusPath, app.CancellationToken).ConfigureAwait(false)); }
      catch { doc["status"] = "unreadable"; }
    }

    var inputPath = WorkspacePaths.InputPath(root);
    if (File.Exists(inputPath))
    {
      try { doc["input"] = JsonNode.Parse(await File.ReadAllTextAsync(inputPath, app.CancellationToken).ConfigureAwait(false)); }
      catch { doc["input"] = "unreadable"; }
    }

    var resultPath = WorkspacePaths.ResultPath(root);
    if (File.Exists(resultPath))
    {
      try { doc["result"] = JsonNode.Parse(await File.ReadAllTextAsync(resultPath, app.CancellationToken).ConfigureAwait(false)); }
      catch { doc["result"] = "unreadable"; }
    }

    if (settings.Trace)
    {
      var tracePath = WorkspacePaths.TracePath(root);
      doc["trace_path"] = tracePath;
      if (File.Exists(tracePath))
      {
        var lines = await File.ReadAllLinesAsync(tracePath, app.CancellationToken).ConfigureAwait(false);
        var tail = lines.Length <= 200 ? lines : lines[^200..];
        doc["trace_tail"] = string.Join(Environment.NewLine, tail);
      }
    }

    if (settings.Human)
    {
      HumanRenderer.RenderRunDetail(doc);
    }
    else
    {
      Console.Write(doc.ToJsonString(JsonSerializationDefaults.Indented));
    }
    return 0;
  }
}

internal class RunsBranchSettings : CommandSettings;

internal sealed class RunsListSettings : RunsBranchSettings
{
  [CommandOption("-H|--human")]
  public bool Human { get; init; }
}

internal sealed class RunsShowSettings : RunsBranchSettings
{
  [CommandArgument(0, "<run-id>")]
  public string RunId { get; init; } = "";

  [CommandOption("--trace|-t")]
  public bool Trace { get; init; }

  [CommandOption("-H|--human")]
  public bool Human { get; init; }
}
