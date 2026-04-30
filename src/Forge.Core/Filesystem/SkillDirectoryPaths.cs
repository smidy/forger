namespace Forge.Core.Filesystem;

/// <summary>
/// Default skill directory roots.
/// </summary>
public static class SkillDirectoryPaths
{
  public static IReadOnlyList<string> GetDefaultSkillRoots()
  {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var cwd = RuntimePaths.ProcessStartedDirectory;
    return new[]
    {
      Path.Combine(home, ".claude", "skills"),
      Path.Combine(home, ".agents", "skills"),
      Path.Combine(cwd, ".claude", "skills"),
      Path.Combine(cwd, ".agents", "skills")
    };
  }
}
