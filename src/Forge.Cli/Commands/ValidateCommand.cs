using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Json.Schema;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal static class ValidateCommand
{
  public static int Execute(ValidateSettings settings)
  {
    var path = Path.GetFullPath(settings.Path);
    if (!File.Exists(path))
    {
      EmitError($"File not found: {path}");
      return 1;
    }

    try
    {
      string kind;
      var lower = path.ToLowerInvariant();
      if (lower.EndsWith(".agent.yaml", StringComparison.Ordinal))
      {
        ValidateAgent(path);
        kind = "agent";
      }
      else
      {
        throw new ConfigException("Use a file named *.agent.yaml (or pass a path with that suffix).");
      }

      var result = new JsonObject
      {
        ["status"] = "ok",
        ["kind"] = kind,
        ["path"] = path
      };
      Console.WriteLine(result.ToJsonString(JsonSerializationDefaults.Indented));
      return 0;
    }
    catch (Exception ex)
    {
      EmitError(ex.Message, ex.InnerException?.Message);
      return 1;
    }
  }

  private static void EmitError(string message, string? inner = null)
  {
    var payload = new JsonObject
    {
      ["status"] = "error",
      ["error"] = message
    };
    if (inner is not null)
    {
      payload["cause"] = inner;
    }

    Console.Error.WriteLine(payload.ToJsonString(JsonSerializationDefaults.General));
  }

  private static void ValidateAgent(string path)
  {
    var cfg = AgentConfig.LoadFromYamlFile(path);
    _ = JsonSchema.FromText(cfg.InputSchema.ToJsonString());
    if (cfg.OutputSchema is not null) _ = JsonSchema.FromText(cfg.OutputSchema.ToJsonString());
  }
}

internal sealed class ValidateSettings : CommandSettings
{
  [CommandArgument(0, "<path>")]
  public string Path { get; init; } = "";
}
