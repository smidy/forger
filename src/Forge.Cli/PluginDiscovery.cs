using Forge.Pipeline;

namespace Forge.Cli;

internal static class PluginDiscovery
{
  public static IReadOnlyList<(string Name, string Root)> ListAgents(string forgeHome) =>
    ListBySuffix(forgeHome, "agents", ".agent.yaml");

  private static List<(string Name, string Root)> ListBySuffix(string forgeHome, string sub, string suffix)
  {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var list = new List<(string, string)>();
    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, sub);
      if (!Directory.Exists(dir))
      {
        continue;
      }

      foreach (var path in Directory.EnumerateFiles(dir, "*" + suffix, SearchOption.TopDirectoryOnly))
      {
        var name = Path.GetFileName(path)[..^suffix.Length];
        if (seen.Add(name))
        {
          list.Add((name, root));
        }
      }
    }

    list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
    return list;
  }
}
