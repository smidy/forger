using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Json;
using Forge.Core.Schema;
using Forge.Pipeline;
using Forge.Tools;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class DescribeCommand
{
  public static int Execute(IServiceProvider sp, DescribeSettings settings)
  {
    try
    {
      var app = sp.GetRequiredService<ForgeAppState>();
      var kind = settings.Kind.Trim().ToLowerInvariant();
      var name = settings.Name.Trim();

      JsonNode doc = kind switch
      {
        "agent" => DescribeAgent(app.ForgeHome, name),
        "tool" => DescribeTool(sp, name),
        _ => throw new InvalidOperationException("Kind must be agent or tool.")
      };

      Console.Write(doc.ToJsonString(JsonSerializationDefaults.Indented));
      return 0;
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
      return 1;
    }
  }

  private static JsonObject DescribeAgent(string forgeHome, string name)
  {
    var path = PluginPaths.FindAgent(forgeHome, name);
    if (path is null)
    {
      throw new FileNotFoundException($"Agent '{name}' not found in search paths.");
    }

    var cfg = AgentConfig.LoadFromYamlFile(path);
    return new JsonObject
    {
      ["kind"] = "agent",
      ["name"] = cfg.Name,
      ["resolved_path"] = path,
      ["model"] = cfg.Model,
      ["max_iterations"] = cfg.MaxIterations,
      ["tools"] = ToJsonStringArray(cfg.Tools),
      ["system_prompt"] = cfg.SystemPrompt,
      ["user_prompt"] = cfg.UserPrompt,
      ["input_schema"] = cfg.InputSchema.DeepClone(),
      ["output_schema"] = cfg.OutputSchema?.DeepClone()
    };
  }

  private static JsonArray ToJsonStringArray(IEnumerable<string> items)
  {
    var a = new JsonArray();
    foreach (var x in items)
    {
      a.Add(JsonValue.Create(x));
    }

    return a;
  }

  private static JsonObject DescribeTool(IServiceProvider sp, string name)
  {
    ITool tool;
    try
    {
      tool = sp.GetRequiredService<ToolRegistry>().Require(name);
    }
    catch (Forge.Core.Exceptions.ConfigException)
    {
      throw new FileNotFoundException($"Tool '{name}' is not registered.");
    }

    return new JsonObject
    {
      ["kind"] = "tool",
      ["name"] = tool.Name,
      ["description"] = tool.Description,
      ["input_schema"] = tool.InputSchema.ToJsonNode(),
      ["output_schema"] = tool.OutputSchema.ToJsonNode()
    };
  }
}

internal sealed class DescribeSettings : CommandSettings
{
  [CommandArgument(0, "<agent|tool>")]
  public string Kind { get; init; } = "";

  [CommandArgument(1, "<name>")]
  public string Name { get; init; } = "";
}
