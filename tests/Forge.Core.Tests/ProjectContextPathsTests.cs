using FluentAssertions;
using Forge.Core.Filesystem;

namespace Forge.Core.Tests;

[Collection("RuntimePaths-Static")]
public class ProjectContextPathsTests
{
  private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

  [Fact]
  public void Ordered_roots_cwd_then_agent_dir_then_extras()
  {
    var workspace = Path.Combine(Path.GetTempPath(), "forge-pcp-" + Guid.NewGuid());
    var agentsFolder = Path.Combine(workspace, "agents");
    Directory.CreateDirectory(agentsFolder);
    var agentFile = Path.Combine(agentsFolder, "x.agent.yaml");
    File.WriteAllText(agentFile, "{}");
    var extraDir = Path.Combine(workspace, "extra");
    Directory.CreateDirectory(extraDir);

    var prev = RuntimePaths.ProcessStartedDirectory;
    try
    {
      RuntimePaths.ProcessStartedDirectory = workspace;
      var roots = ProjectContextPaths.GetOrderedRoots(agentFile, new[] { "extra" });
      roots.Should().HaveCount(3);
      roots[0].Should().Be(Norm(workspace));
      roots[1].Should().Be(Norm(agentsFolder));
      roots[2].Should().Be(Norm(extraDir));
    }
    finally
    {
      RuntimePaths.ProcessStartedDirectory = prev;
      try
      {
        Directory.Delete(workspace, recursive: true);
      }
      catch
      {
      }
    }
  }

  [Fact]
  public void Agent_dir_skipped_when_same_as_cwd()
  {
    var baseDir = Path.Combine(Path.GetTempPath(), "forge-pcp2-" + Guid.NewGuid());
    Directory.CreateDirectory(baseDir);
    var agentFile = Path.Combine(baseDir, "x.agent.yaml");
    File.WriteAllText(agentFile, "{}");

    var prev = RuntimePaths.ProcessStartedDirectory;
    try
    {
      RuntimePaths.ProcessStartedDirectory = baseDir;
      var roots = ProjectContextPaths.GetOrderedRoots(agentFile, Array.Empty<string>());
      roots.Should().ContainSingle();
      roots[0].Should().Be(Norm(baseDir));
    }
    finally
    {
      RuntimePaths.ProcessStartedDirectory = prev;
      try
      {
        Directory.Delete(baseDir, recursive: true);
      }
      catch
      {
      }
    }
  }
}
