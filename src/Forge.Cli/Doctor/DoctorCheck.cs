using System.Text.Json.Serialization;

namespace Forge.Cli.Doctor;

/// <summary>
/// Result of a single health check performed by <c>forge doctor</c>.
/// </summary>
public sealed class DoctorCheck
{
  /// <summary>Unique check identifier, e.g. <c>home.exists</c>.</summary>
  [JsonPropertyName("id")]
  public required string Id { get; init; }

  /// <summary>Human-readable title.</summary>
  [JsonPropertyName("title")]
  public required string Title { get; init; }

  /// <summary>One of <c>ok</c>, <c>warn</c>, <c>fail</c>, <c>skip</c>.</summary>
  [JsonPropertyName("status")]
  public required string Status { get; init; }

  /// <summary>When true, a <c>fail</c> status causes exit code 1.</summary>
  [JsonPropertyName("required")]
  public bool Required { get; init; }

  /// <summary>Informational detail string (path found, count of items, etc.).</summary>
  [JsonPropertyName("detail")]
  public string? Detail { get; init; }

  /// <summary>Suggested fix when status is not <c>ok</c>.</summary>
  [JsonPropertyName("fixHint")]
  public string? FixHint { get; init; }
}

/// <summary>
/// Aggregated doctor report written to stdout.
/// </summary>
public sealed class DoctorReport
{
  [JsonPropertyName("version")]
  public required string Version { get; init; }

  [JsonPropertyName("forgeHome")]
  public required string ForgeHome { get; init; }

  [JsonPropertyName("hardFailures")]
  public int HardFailures { get; init; }

  [JsonPropertyName("warnings")]
  public int Warnings { get; init; }

  [JsonPropertyName("checks")]
  public required List<DoctorCheck> Checks { get; init; }
}
