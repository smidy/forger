using FluentAssertions;
using Forge.Core.Config;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Pins the auto-scratch wiring in <see cref="DockerArgv.BuildRunArgs"/>. When a
/// <see cref="BashScratchMount"/> is supplied, the run argv must bind-mount the
/// host scratch dir at the configured container root and set HOME, TMPDIR, and
/// NUGET_PACKAGES to its subdirs. Without these the agent runs into
/// <c>dotnet restore</c> ENOSPC on the default <c>/tmp</c> tmpfs.
/// BaseIntermediateOutputPath / BaseOutputPath are deliberately NOT redirected
/// — the v2 landing tried this and it broke multi-project restores by
/// collapsing every project's <c>project.assets.json</c> into one flat path
/// (last writer wins). Host vs container obj/ contamination is now handled by
/// the agent running <c>dotnet clean</c> before <c>dotnet build</c>.
/// See <c>docs/plans/eval-context-auto-compaction-fixes.md</c> F-L (auto-scratch)
/// and <c>docs/plans/bash-tool-friction-v2.md</c> §Build-isolation (revised).
/// </summary>
public class DockerArgvScratchTests
{
  private static BashConfig MinimalConfig() => new()
  {
    Image = "docker.io/library/debian@sha256:" + new string('c', 64),
    TimeoutSec = 30
  };

  private static DockerRunSpec BaseSpec(BashScratchMount? scratch = null) => new(
    ContainerName: "forge-bash-test",
    RunIdLabelValue: "run-0",
    ImageRef: "sha256:" + new string('a', 64),
    Config: MinimalConfig(),
    Mounts: new BashMountPlan(Array.Empty<BashMountEntry>(), Array.Empty<string>(), DefaultCwd: null),
    Scratch: scratch);

  [Fact]
  public void Scratch_null_leaves_run_argv_without_scratch_mount_or_env_vars()
  {
    var argv = DockerArgv.BuildRunArgs(BaseSpec(scratch: null));

    argv.Should().NotContain(a => a.Contains("/forge-scratch", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("HOME=", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("TMPDIR=", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("NUGET_PACKAGES=", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("BaseIntermediateOutputPath=", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("BaseOutputPath=", StringComparison.Ordinal));
  }

  [Fact]
  public void Scratch_set_injects_bind_mount_and_env_vars()
  {
    var hostRoot = "/tmp/forge-run-42/bash-scratch";
    var scratch = new BashScratchMount(HostRoot: hostRoot, ContainerRoot: "/forge-scratch");

    var argv = DockerArgv.BuildRunArgs(BaseSpec(scratch)).ToList();

    // Bind mount: -v <host>:<container>:rw
    var lastV = -1;
    for (var i = 0; i < argv.Count; i++)
    {
      if (argv[i] == "-v") lastV = i;
    }
    lastV.Should().BeGreaterOrEqualTo(0, "scratch should add a `-v` flag");
    argv[lastV + 1].Should().Be($"{hostRoot}:/forge-scratch:rw");

    // HOME/TMPDIR/NUGET_PACKAGES redirect package caches and scratch writes.
    argv.Should().ContainInConsecutiveOrder(new[] { "-e", "HOME=/forge-scratch/home" });
    argv.Should().ContainInConsecutiveOrder(new[] { "-e", "TMPDIR=/forge-scratch/tmp" });
    argv.Should().ContainInConsecutiveOrder(new[] { "-e", "NUGET_PACKAGES=/forge-scratch/nuget" });

    // BaseIntermediateOutputPath / BaseOutputPath are deliberately NOT set.
    // Setting them to a single flat container path via env var collapses
    // every project's project.assets.json onto one file (last-writer-wins)
    // which breaks multi-project restores. Leaving them unset keeps each
    // csproj's relative obj/bin, preserving MSBuild's per-project isolation.
    argv.Should().NotContain(a => a.StartsWith("BaseIntermediateOutputPath=", StringComparison.Ordinal));
    argv.Should().NotContain(a => a.StartsWith("BaseOutputPath=", StringComparison.Ordinal));

    // Image + command come after the scratch args, not before.
    var imageIndex = argv.IndexOf("sha256:" + new string('a', 64));
    imageIndex.Should().BeGreaterThan(lastV, "mounts must appear before the image ref");
  }
}
