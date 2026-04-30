using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Core.Workspace;
using Spectre.Console;

namespace Forge.Cli.Commands;

internal static class InitCommand
{
  private const string DefaultBaseUrl = "http://localhost:4001/v1";
  private const string DefaultApiKey = "sk-local";

  public static async Task<int> ExecuteAsync(InitSettings settings, string forgeHome)
  {
    Directory.CreateDirectory(forgeHome);

    var llmJsonPath = Path.Combine(forgeHome, "llm.json");

    // Check for existing llm.json
    if (File.Exists(llmJsonPath) && !settings.Force)
    {
      EmitError($"llm.json already exists at {llmJsonPath}; pass --force to overwrite");
      return 1;
    }

    // Determine values (flags, then prompts, then defaults)
    string baseUrl;
    string apiKey;
    string? model;

    if (settings.NonInteractive)
    {
      // In non-interactive mode, required flags must be present
      if (string.IsNullOrWhiteSpace(settings.BaseUrl))
      {
        EmitError("Missing required flag: --base-url");
        return 1;
      }

      if (string.IsNullOrWhiteSpace(settings.ApiKey))
      {
        EmitError("Missing required flag: --api-key");
        return 1;
      }

      if (string.IsNullOrWhiteSpace(settings.Model))
      {
        EmitError("Missing required flag: --model");
        return 1;
      }

      baseUrl = settings.BaseUrl.Trim();
      apiKey = settings.ApiKey.Trim();
      model = settings.Model.Trim();
    }
    else if (IsInteractive())
    {
      // Interactive prompts
      AnsiConsole.MarkupLine("[bold]Forge first-run setup[/]");

      baseUrl = AnsiConsole.Prompt(
        new TextPrompt<string>("Base URL (include /v1):")
          .DefaultValue(settings.BaseUrl ?? DefaultBaseUrl)
          .AllowEmpty());

      if (string.IsNullOrWhiteSpace(baseUrl))
      {
        baseUrl = settings.BaseUrl ?? DefaultBaseUrl;
      }

      apiKey = AnsiConsole.Prompt(
        new TextPrompt<string>("API key (or ${ENV_VAR}):")
          .DefaultValue(settings.ApiKey ?? DefaultApiKey)
          .AllowEmpty());

      if (string.IsNullOrWhiteSpace(apiKey))
      {
        apiKey = settings.ApiKey ?? DefaultApiKey;
      }

      // Model is required, no default
      var promptText = settings.Model is not null
        ? $"Default model id [grey]({settings.Model}):[/]"
        : "Default model id:";

      model = AnsiConsole.Prompt(
        new TextPrompt<string?>(promptText)
          .AllowEmpty());

      if (string.IsNullOrWhiteSpace(model))
      {
        model = settings.Model;
      }

      if (string.IsNullOrWhiteSpace(model))
      {
        EmitError("Model is required.");
        return 1;
      }

      // Prompt for copy-examples if not specified
      if (!settings.CopyExamples)
      {
        var copyExamples = AnsiConsole.Confirm("Copy example agents/pipelines?", true);
        settings = new InitSettings
        {
          BaseUrl = baseUrl,
          ApiKey = apiKey,
          Model = model,
          CopyExamples = copyExamples,
          Force = settings.Force,
          NonInteractive = settings.NonInteractive
        };
      }
    }
    else
    {
      // Non-TTY with no flags
      EmitError("Missing required flags in non-interactive mode: --base-url, --api-key, --model");
      return 1;
    }

    // Validate baseUrl ends with /v1
    if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
    {
      EmitError("baseUrl must end with /v1");
      return 1;
    }

    // Build config with model_context defaults so common model aliases
    // work out of the box for context compaction.
    var config = new JsonObject
    {
      ["baseUrl"] = baseUrl,
      ["apiKey"] = apiKey,
      ["defaultModel"] = model,
      ["modelContext"] = new JsonObject
      {
        ["claude-3-5-sonnet"] = 200000,
        ["claude-4-7-sonnet"] = 200000,
        ["gpt-4o"] = 128000,
        ["gemini-2-5-pro"] = 1048576
      }
    };

    // Atomic write via the project's standard helper (see CLAUDE.md conventions).
    try
    {
      await WorkspaceIo.WriteJsonAtomicAsync(llmJsonPath, config).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      EmitError($"Failed to write config: {ex.Message}");
      return 1;
    }

    // Copy examples if requested
    var exampleAgents = new JsonArray();
    var examplePipelines = new JsonArray();

    if (settings.CopyExamples)
    {
      var agentsDir = Path.Combine(forgeHome, "agents");
      var pipelinesDir = Path.Combine(forgeHome, "pipelines");
      Directory.CreateDirectory(agentsDir);
      Directory.CreateDirectory(pipelinesDir);

      var extracted = ExampleExtractor.CopyEmbeddedExamples(agentsDir, pipelinesDir);

      foreach (var agent in extracted.Where(x => x.EndsWith(".agent.yaml", StringComparison.OrdinalIgnoreCase)))
      {
        exampleAgents.Add(Path.GetFileName(agent));
      }

      foreach (var pipeline in extracted.Where(x => x.EndsWith(".pipeline.yaml", StringComparison.OrdinalIgnoreCase)))
      {
        examplePipelines.Add(Path.GetFileName(pipeline));
      }
    }

    // Output result
    var result = new JsonObject
    {
      ["forgeHome"] = forgeHome,
      ["llmConfig"] = llmJsonPath,
      ["exampleAgents"] = exampleAgents,
      ["examplePipelines"] = examplePipelines
    };

    Console.WriteLine(result.ToJsonString(JsonSerializationDefaults.Indented));
    return 0;
  }

  private static bool IsInteractive()
  {
    try
    {
      return Console.IsInputRedirected == false && Console.IsOutputRedirected == false;
    }
    catch
    {
      return false;
    }
  }

  private static void EmitError(string message)
  {
    var payload = new JsonObject
    {
      ["status"] = "error",
      ["error"] = message
    };
    Console.Error.WriteLine(payload.ToJsonString(JsonSerializationDefaults.General));
  }
}
