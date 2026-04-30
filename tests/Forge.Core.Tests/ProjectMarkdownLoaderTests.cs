using FluentAssertions;
using Forge.Core.Filesystem;

namespace Forge.Core.Tests;

public class ProjectMarkdownLoaderTests
{
  [Fact]
  public void Loads_agents_then_claude_in_one_root()
  {
    var root = Path.Combine(Path.GetTempPath(), "forge-pm-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
      File.WriteAllText(Path.Combine(root, "AGENTS.md"), "alpha");
      File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "beta");

      var text = ProjectMarkdownLoader.LoadOrderedRoots(new[] { root }, 1024, null);
      text.Should().Contain("alpha");
      text.Should().Contain("beta");
      text.IndexOf("alpha", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("beta", StringComparison.Ordinal));
    }
    finally
    {
      try
      {
        Directory.Delete(root, recursive: true);
      }
      catch
      {
      }
    }
  }

  [Fact]
  public void Missing_files_yield_empty()
  {
    var root = Path.Combine(Path.GetTempPath(), "forge-pm2-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
      ProjectMarkdownLoader.LoadOrderedRoots(new[] { root }, 1024, null).Should().BeEmpty();
    }
    finally
    {
      try
      {
        Directory.Delete(root, recursive: true);
      }
      catch
      {
      }
    }
  }
}
