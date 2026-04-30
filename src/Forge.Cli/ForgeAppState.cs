namespace Forge.Cli;

/// <summary>Holds paths and host context resolved before the Spectre command tree runs.</summary>
internal sealed class ForgeAppState
{
  public required string ForgeHome { get; init; }
  public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
}
