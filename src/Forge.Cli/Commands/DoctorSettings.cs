using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

internal sealed class DoctorSettings : CommandSettings
{
  [CommandOption("--probe")]
  public bool Probe { get; init; }

  [CommandOption("--human")]
  public bool Human { get; init; }
}
