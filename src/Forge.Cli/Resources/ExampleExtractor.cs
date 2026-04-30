using System.Reflection;

namespace Forge.Cli;

/// <summary>
/// Extracts embedded example YAML files to the filesystem.
/// </summary>
internal static class ExampleExtractor
{
  private const string ResourcePrefix = "Forge.Cli.Resources.examples.";

  /// <summary>
  /// Copies embedded example files to the specified directories.
  /// Skips files that already exist.
  /// </summary>
  /// <param name="agentsDir">Target directory for agent YAMLs.</param>
  /// <param name="pipelinesDir">Target directory for pipeline YAMLs.</param>
  /// <returns>List of copied file names (not full paths).</returns>
  public static IReadOnlyList<string> CopyEmbeddedExamples(string agentsDir, string pipelinesDir)
  {
    var copied = new List<string>();
    var assembly = Assembly.GetExecutingAssembly();
    var resourceNames = assembly.GetManifestResourceNames();

    foreach (var resourceName in resourceNames)
    {
      if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
      {
        continue;
      }

      // Resource name after prefix strip, e.g. "agents\hello.agent.yaml" on Windows
      // (MSBuild preserves the path separator from %(RecursiveDir) in <LogicalName>).
      // Normalise so "agents\…", "agents/…", and "agents.…" all route correctly.
      var relativeResourcePath = resourceName.Substring(ResourcePrefix.Length);
      var normalised = relativeResourcePath.Replace('\\', '/').Replace('/', '.');

      string? targetDir = null;
      string? targetFileName = null;

      if (normalised.StartsWith("agents.", StringComparison.Ordinal))
      {
        targetDir = agentsDir;
        targetFileName = normalised.Substring("agents.".Length);
      }
      else if (normalised.StartsWith("pipelines.", StringComparison.Ordinal))
      {
        targetDir = pipelinesDir;
        targetFileName = normalised.Substring("pipelines.".Length);
      }

      if (targetDir is null || targetFileName is null)
      {
        continue;
      }

      var targetPath = Path.Combine(targetDir, targetFileName);

      // Skip existing files
      if (File.Exists(targetPath))
      {
        continue;
      }

      using var stream = assembly.GetManifestResourceStream(resourceName);
      if (stream is null)
      {
        continue;
      }

      using var reader = new StreamReader(stream);
      var content = reader.ReadToEnd();
      File.WriteAllText(targetPath, content);
      copied.Add(targetFileName);
    }

    return copied;
  }
}
