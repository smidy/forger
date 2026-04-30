using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Tools;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class ListCommand
{
  public static int Execute(IServiceProvider sp, ListSettings settings)
  {
    var app = sp.GetRequiredService<ForgeAppState>();
    var tools = sp.GetRequiredService<ToolRegistry>();
    var kind = string.IsNullOrWhiteSpace(settings.Kind) ? "all" : settings.Kind.Trim().ToLowerInvariant();
    if (kind is not ("all" or "agents" or "tools"))
    {
      Console.Error.WriteLine("Kind must be agents, tools, or all.");
      return 1;
    }

    var agents = new JsonArray();
    if (kind is "all" or "agents")
    {
      foreach (var (name, root) in PluginDiscovery.ListAgents(app.ForgeHome))
      {
        agents.Add(new JsonObject { ["name"] = name, ["root"] = root });
      }
    }

    var toolEntries = new JsonArray();
    if (kind is "all" or "tools")
    {
      foreach (var t in tools.List().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
      {
        toolEntries.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description });
      }
    }

    if (settings.Human)
    {
      RenderHuman(kind, agents, toolEntries);
      return 0;
    }

    var doc = new JsonObject();
    if (kind is "all" or "agents")
    {
      doc["agents"] = agents;
    }

    if (kind is "all" or "tools")
    {
      doc["tools"] = toolEntries;
    }

    Console.WriteLine(doc.ToJsonString(JsonSerializationDefaults.Indented));
    return 0;
  }

  private static void RenderHuman(string kind, JsonArray agents, JsonArray tools)
  {
    if (kind is "all" or "agents")
    {
      AnsiConsole.MarkupLine("[bold]Agents[/]");
      foreach (var a in agents.OfType<JsonObject>())
      {
        AnsiConsole.MarkupLine($"  [cyan]{a["name"]}[/]  ({a["root"]})");
      }

      if (kind is "all")
      {
        AnsiConsole.WriteLine();
      }
    }

    if (kind is "all" or "tools")
    {
      AnsiConsole.MarkupLine("[bold]Tools[/]");
      foreach (var t in tools.OfType<JsonObject>())
      {
        AnsiConsole.MarkupLine($"  [cyan]{t["name"]}[/] — {t["description"]}");
      }
    }
  }
}

internal sealed class ListSettings : CommandSettings
{
  [CommandArgument(0, "[kind]")]
  public string? Kind { get; init; }

  [CommandOption("--human|-H")]
  public bool Human { get; init; }
}
