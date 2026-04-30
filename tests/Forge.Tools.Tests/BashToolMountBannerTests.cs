using FluentAssertions;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests;

/// <summary>
/// Unit coverage for the first-call mount-table banner
/// (<c>docs/plans/bash-tool-friction-v2.md</c>). The banner teaches the agent
/// the container-path → host-path mapping up-front, eliminating the 3-4
/// iteration discovery tax seen in the F-1 re-eval.
/// </summary>
public class BashToolMountBannerTests
{
  [Fact]
  public void BuildMountBanner_lists_repo_and_run_with_default_cwd_and_scratch_hint()
  {
    var plan = new BashMountPlan(
      Mounts: new[]
      {
        new BashMountEntry(HostPath: "/src/forge", ContainerPath: "/repo", ReadOnly: false),
        new BashMountEntry(HostPath: "/forge/runs/abc", ContainerPath: "/run", ReadOnly: false)
      },
      DockerArgs: Array.Empty<string>(),
      DefaultCwd: new BashMountEntry(HostPath: "/src/forge", ContainerPath: "/repo", ReadOnly: false));

    var banner = BashTool.BuildMountBanner(plan);

    banner.Should().Contain("=== Bash container ready (first call) ===");
    banner.Should().Contain("/repo");
    banner.Should().Contain("/src/forge");
    banner.Should().Contain("/run");
    banner.Should().Contain("/forge/runs/abc");
    banner.Should().Contain("(rw)");
    banner.Should().Contain("Default cwd: /repo");
    banner.Should().Contain("cd /repo && dotnet build",
      "the banner should show the simplified build recipe so the agent doesn't relitigate cp/find/rm patterns");
  }

  [Fact]
  public void BuildMountBanner_surfaces_inputs_entries_with_ro_flag()
  {
    var plan = new BashMountPlan(
      Mounts: new[]
      {
        new BashMountEntry(HostPath: "/src/forge", ContainerPath: "/repo", ReadOnly: false),
        new BashMountEntry(HostPath: "/forge/runs/abc", ContainerPath: "/run", ReadOnly: false),
        new BashMountEntry(HostPath: "/external/docs", ContainerPath: "/inputs/0", ReadOnly: true)
      },
      DockerArgs: Array.Empty<string>(),
      DefaultCwd: new BashMountEntry(HostPath: "/src/forge", ContainerPath: "/repo", ReadOnly: false));

    var banner = BashTool.BuildMountBanner(plan);

    banner.Should().Contain("/inputs/0");
    banner.Should().Contain("(ro)");
    banner.Should().Contain("/external/docs");
  }

}
