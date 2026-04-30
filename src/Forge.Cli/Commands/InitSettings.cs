using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal sealed class InitSettings : CommandSettings
{
  [CommandOption("--base-url <URL>")]
  public string? BaseUrl { get; init; }

  [CommandOption("--api-key <KEY>")]
  public string? ApiKey { get; init; }

  [CommandOption("--model <ID>")]
  public string? Model { get; init; }

  [CommandOption("--copy-examples")]
  public bool CopyExamples { get; init; }

  [CommandOption("--force")]
  public bool Force { get; init; }

  [CommandOption("--non-interactive")]
  public bool NonInteractive { get; init; }
}
