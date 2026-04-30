namespace Forge.Core.Filesystem;

/// <summary>
/// Ordered roots for <c>AGENTS.md</c> / <c>CLAUDE.md</c>: cwd, then agent YAML directory (if distinct), then configured extras.
/// </summary>
public static class ProjectContextPaths
{
  private static readonly StringComparison PathComparison =
    OperatingSystem.IsWindows()
      ? StringComparison.OrdinalIgnoreCase
      : StringComparison.Ordinal;

  private static string NormalizeCanonical(string path) =>
    path.Replace('\\', '/').TrimEnd('/');

  public static IReadOnlyList<string> GetOrderedRoots(
    string? agentYamlPath,
    IReadOnlyList<string> additionalRoots)
  {
    var cwd = NormalizeCanonical(RuntimePaths.ProcessStartedDirectory);
    var list = new List<string> { cwd };

    if (!string.IsNullOrWhiteSpace(agentYamlPath))
    {
      var fullAgent = Path.GetFullPath(agentYamlPath);
      var dir = Path.GetDirectoryName(fullAgent);
      if (!string.IsNullOrEmpty(dir))
      {
        var canonicalDir = NormalizeCanonical(dir);
        if (!PathsEqual(canonicalDir, cwd))
        {
          list.Add(canonicalDir);
        }
      }
    }

    foreach (var r in additionalRoots)
    {
      if (string.IsNullOrWhiteSpace(r))
      {
        continue;
      }

      var trimmed = r.Trim();
      var full = Path.IsPathRooted(trimmed)
        ? Path.GetFullPath(trimmed)
        : Path.GetFullPath(trimmed, cwd);
      var canonical = NormalizeCanonical(full);
      if (list.Any(p => PathsEqual(p, canonical)))
      {
        continue;
      }

      list.Add(canonical);
    }

    return list;
  }

  private static bool PathsEqual(string a, string b) =>
    string.Equals(
      a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
      b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
      PathComparison);
}
