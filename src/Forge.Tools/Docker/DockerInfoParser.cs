using System.Text.Json;

namespace Forge.Tools.Docker;

/// <summary>
/// Snapshot of the active Docker daemon's identity and security posture as
/// reported by <c>docker info</c>. Plan:
/// <c>docs/plans/bash-tool-rootless-docker.md</c>.
/// </summary>
public sealed record DockerDaemonInfo(
  bool Rootless,
  string OsType,
  string Architecture,
  string ServerVersion);

/// <summary>
/// Parses the composite output of
/// <c>docker info --format '{{.OSType}}/{{.Architecture}}/{{.ServerVersion}}/{{json .SecurityOptions}}'</c>
/// into a <see cref="DockerDaemonInfo"/>. Pure — no I/O, no Docker process.
/// Shared by <see cref="DockerProcessCli.GetDaemonInfoAsync"/> and the
/// <c>forge doctor</c> bash docker probe so the two call sites cannot drift
/// on parsing semantics. Plan:
/// <c>docs/plans/bash-tool-rootless-docker.md</c>.
/// </summary>
public static class DockerInfoParser
{
  /// <summary>
  /// The Go-template format string passed to <c>docker info --format ...</c>.
  /// Both call sites (DockerProcessCli, DoctorRunner) use this constant so a
  /// future format change touches one place.
  /// </summary>
  public const string FormatString = "{{.OSType}}/{{.Architecture}}/{{.ServerVersion}}/{{json .SecurityOptions}}";

  /// <summary>
  /// Parse the composite <c>docker info</c> output. Expected layout:
  /// <c>OSType/Architecture/ServerVersion/&lt;JSON-array of security option strings&gt;</c>.
  /// </summary>
  /// <exception cref="ArgumentNullException">When <paramref name="raw"/> is null.</exception>
  /// <exception cref="FormatException">When the input does not contain four <c>/</c>-separated fields, when any leading field is empty, or when the trailing field is not a JSON array.</exception>
  public static DockerDaemonInfo Parse(string raw)
  {
    ArgumentNullException.ThrowIfNull(raw);
    var trimmed = raw.Trim();
    if (trimmed.Length == 0)
    {
      throw new FormatException("docker info output is empty.");
    }

    var parts = trimmed.Split('/', 4);
    if (parts.Length < 4)
    {
      throw new FormatException(
        $"docker info output did not contain four `/`-separated fields. Got: `{trimmed}`.");
    }

    var osType = parts[0].Trim();
    var architecture = parts[1].Trim();
    var serverVersion = parts[2].Trim();
    var securityOptionsJson = parts[3].Trim();

    if (osType.Length == 0)
    {
      throw new FormatException($"docker info OSType is empty. Raw: `{trimmed}`.");
    }
    if (architecture.Length == 0)
    {
      throw new FormatException($"docker info Architecture is empty. Raw: `{trimmed}`.");
    }
    if (serverVersion.Length == 0)
    {
      throw new FormatException($"docker info ServerVersion is empty. Raw: `{trimmed}`.");
    }

    var rootless = ContainsRootless(securityOptionsJson);

    return new DockerDaemonInfo(
      Rootless: rootless,
      OsType: osType,
      Architecture: architecture,
      ServerVersion: serverVersion);
  }

  private static bool ContainsRootless(string securityOptionsJson)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(securityOptionsJson);
    }
    catch (JsonException ex)
    {
      throw new FormatException(
        $"docker info SecurityOptions is not valid JSON: `{securityOptionsJson}`.", ex);
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        throw new FormatException(
          $"docker info SecurityOptions is not a JSON array. Got: `{securityOptionsJson}`.");
      }

      foreach (var element in doc.RootElement.EnumerateArray())
      {
        if (element.ValueKind != JsonValueKind.String)
        {
          continue;
        }

        var s = element.GetString();
        if (string.IsNullOrEmpty(s))
        {
          continue;
        }

        var tokens = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
          if (token.Equals("rootless", StringComparison.Ordinal) ||
              token.Equals("name=rootless", StringComparison.Ordinal))
          {
            return true;
          }
        }
      }
    }

    return false;
  }
}
