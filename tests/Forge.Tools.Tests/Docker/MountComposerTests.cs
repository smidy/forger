using System.Runtime.InteropServices;
using FluentAssertions;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Pins behaviour of the bash-tool's mount composer: user mount pass-through,
/// run-workspace always-present at /run, duplicate container-path rejection,
/// Windows long-path/UNC refusal, cwd validation, and default-cwd selection.
/// </summary>
public class MountComposerTests
{
  private static string TempDir(string tag)
  {
    var p = Path.Combine(Path.GetTempPath(), $"forge-mc-{tag}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(p);
    return p;
  }

  private static BashMount Mount(string host, string container, BashMountMode mode = BashMountMode.ReadWrite) =>
    new(host, container, mode);

  [Fact]
  public void Empty_user_mounts_produces_run_only_plan()
  {
    var run = TempDir("run-only");
    try
    {
      var plan = MountComposer.Compose(Array.Empty<BashMount>(), run);
      plan.Mounts.Should().HaveCount(1);
      plan.Mounts[0].ContainerPath.Should().Be("/run");
      plan.Mounts[0].ReadOnly.Should().BeFalse();
      plan.DefaultCwd!.ContainerPath.Should().Be("/run");
    }
    finally
    {
      TryDelete(run);
    }
  }

  [Fact]
  public void User_mount_appears_before_run_workspace()
  {
    var repo = TempDir("repo");
    var run = TempDir("run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);
      plan.Mounts.Should().HaveCount(2);
      plan.Mounts[0].ContainerPath.Should().Be("/repo");
      plan.Mounts[0].ReadOnly.Should().BeFalse();
      plan.Mounts[1].ContainerPath.Should().Be("/run");
      plan.DefaultCwd!.ContainerPath.Should().Be("/repo");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ReadOnly_mount_emits_ro_docker_arg()
  {
    var src = TempDir("ro-src");
    var run = TempDir("ro-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(src, "/inputs/0", BashMountMode.ReadOnly) }, run);
      plan.Mounts[0].ReadOnly.Should().BeTrue();
      plan.DockerArgs.Should().Contain($"{Path.GetFullPath(src)}:/inputs/0:ro");
    }
    finally
    {
      TryDelete(src);
      TryDelete(run);
    }
  }

  [Fact]
  public void ReadWrite_mount_emits_no_ro_suffix()
  {
    var src = TempDir("rw-src");
    var run = TempDir("rw-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(src, "/repo") }, run);
      plan.Mounts[0].ReadOnly.Should().BeFalse();
      plan.DockerArgs.Should().Contain($"{Path.GetFullPath(src)}:/repo");
    }
    finally
    {
      TryDelete(src);
      TryDelete(run);
    }
  }

  [Fact]
  public void Duplicate_container_path_throws()
  {
    var a = TempDir("dup-a");
    var b = TempDir("dup-b");
    var run = TempDir("dup-run");
    try
    {
      var act = () => MountComposer.Compose(
        new[] { Mount(a, "/repo"), Mount(b, "/repo") }, run);
      act.Should().Throw<ValidationException>()
        .WithMessage("*Duplicate*");
    }
    finally
    {
      TryDelete(a);
      TryDelete(b);
      TryDelete(run);
    }
  }

  [Fact]
  public void Default_cwd_is_first_user_mount_when_present()
  {
    var first = TempDir("first");
    var second = TempDir("second");
    var run = TempDir("run");
    try
    {
      var plan = MountComposer.Compose(
        new[] { Mount(first, "/work"), Mount(second, "/inputs/0", BashMountMode.ReadOnly) }, run);
      plan.DefaultCwd!.ContainerPath.Should().Be("/work");
    }
    finally
    {
      TryDelete(first);
      TryDelete(second);
      TryDelete(run);
    }
  }

  [Fact]
  public void Unc_path_rejected_on_windows()
  {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return;
    }

    var act = () => MountComposer.Compose(
      new[] { new BashMount(@"\\server\share\dir", "/repo", BashMountMode.ReadWrite) },
      @"C:\temp\run");
    act.Should().Throw<ValidationException>()
      .WithMessage("*UNC*");
  }

  [Fact]
  public void Extended_length_path_rejected_on_windows()
  {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return;
    }

    var act = () => MountComposer.Compose(
      new[] { new BashMount(@"\\?\C:\Some\Path", "/repo", BashMountMode.ReadWrite) },
      @"C:\temp\run");
    act.Should().Throw<ValidationException>()
      .WithMessage("*extended-length*");
  }

  [Fact]
  public void ResolveContainerCwd_valid_write_prefix_returns_mount()
  {
    var repo = TempDir("cwd-w");
    var run = TempDir("cwd-w-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);
      var match = MountComposer.ResolveContainerCwd(plan, "/repo/src", requireWritable: true);
      match.ContainerPath.Should().Be("/repo");
      match.ReadOnly.Should().BeFalse();
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_exact_root_matches()
  {
    var repo = TempDir("cwd-exact");
    var run = TempDir("cwd-exact-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);
      var match = MountComposer.ResolveContainerCwd(plan, "/repo", requireWritable: true);
      match.ContainerPath.Should().Be("/repo");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_unknown_path_throws()
  {
    var repo = TempDir("cwd-unknown");
    var run = TempDir("cwd-unknown-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);

      var act = () => MountComposer.ResolveContainerCwd(plan, "/some/other/path", requireWritable: false);
      act.Should().Throw<ValidationException>()
        .WithMessage("*does not map to any mount*");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_parent_traversal_rejected()
  {
    var repo = TempDir("cwd-trav");
    var run = TempDir("cwd-trav-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);

      var act = () => MountComposer.ResolveContainerCwd(plan, "/repo/../secret", requireWritable: false);
      act.Should().Throw<ValidationException>()
        .WithMessage("*parent-traversal*");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_requires_writable_rejects_read_only_mount()
  {
    var src = TempDir("cwd-ro-src");
    var run = TempDir("cwd-ro-run");
    try
    {
      var plan = MountComposer.Compose(
        new[] { Mount(src, "/inputs/0", BashMountMode.ReadOnly) }, run);

      var act = () => MountComposer.ResolveContainerCwd(plan, "/inputs/0/sub", requireWritable: true);
      act.Should().Throw<ValidationException>()
        .WithMessage("*read-only*");
    }
    finally
    {
      TryDelete(src);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_requires_writable_false_accepts_read_only()
  {
    var src = TempDir("cwd-ro-ok");
    var run = TempDir("cwd-ro-ok-run");
    try
    {
      var plan = MountComposer.Compose(
        new[] { Mount(src, "/inputs/0", BashMountMode.ReadOnly) }, run);
      var match = MountComposer.ResolveContainerCwd(plan, "/inputs/0", requireWritable: false);
      match.ReadOnly.Should().BeTrue();
    }
    finally
    {
      TryDelete(src);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_empty_throws()
  {
    var repo = TempDir("cwd-empty");
    var run = TempDir("cwd-empty-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);

      var act = () => MountComposer.ResolveContainerCwd(plan, "", requireWritable: false);
      act.Should().Throw<ValidationException>()
        .WithMessage("*non-empty*");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_relative_path_throws()
  {
    var repo = TempDir("cwd-rel");
    var run = TempDir("cwd-rel-run");
    try
    {
      var plan = MountComposer.Compose(new[] { Mount(repo, "/repo") }, run);

      var act = () => MountComposer.ResolveContainerCwd(plan, "repo/src", requireWritable: false);
      act.Should().Throw<ValidationException>()
        .WithMessage("*absolute*");
    }
    finally
    {
      TryDelete(repo);
      TryDelete(run);
    }
  }

  [Fact]
  public void ResolveContainerCwd_run_workspace_resolves()
  {
    var run = TempDir("cwd-run");
    try
    {
      var plan = MountComposer.Compose(Array.Empty<BashMount>(), run);
      var match = MountComposer.ResolveContainerCwd(plan, "/run/output", requireWritable: true);
      match.ContainerPath.Should().Be("/run");
      match.ReadOnly.Should().BeFalse();
    }
    finally
    {
      TryDelete(run);
    }
  }

  private static void TryDelete(string path)
  {
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch
    {
    }
  }
}
