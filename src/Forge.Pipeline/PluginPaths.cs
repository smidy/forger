namespace Forge.Pipeline;

public static class PluginPaths
{
  public static IEnumerable<string> SearchRoots(string forgeHome)
  {
    yield return Path.Combine(Environment.CurrentDirectory, ".forge");
    var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "forge");
    yield return cfg;
    yield return forgeHome;
  }

  public static string? FindAgent(string forgeHome, string name)
  {
    foreach (var root in SearchRoots(forgeHome))
    {
      var p = Path.Combine(root, "agents", name + ".agent.yaml");
      if (File.Exists(p))
      {
        return p;
      }
    }

    return null;
  }

  public static string? FindPipeline(string forgeHome, string name)
  {
    foreach (var root in SearchRoots(forgeHome))
    {
      var p = Path.Combine(root, "pipelines", name + ".pipeline.yaml");
      if (File.Exists(p))
      {
        return p;
      }
    }

    return null;
  }
}
