using FluentAssertions;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="DockerContainerLifecycle"/>'s rootless-mode
/// enforcement and mount-readability probe. Plan:
/// <c>docs/plans/bash-tool-rootless-docker.md</c> §3 + Acceptance.
/// </summary>
public class DockerContainerLifecycleRootlessTests
{
  // ─── EnforceRootlessMode ─────────────────────────────────────────────────

  [Fact]
  public void Auto_against_rootful_daemon_does_not_throw()
  {
    var daemon = Rootful();
    var trace = new CaptureTrace();

    DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Auto, daemon, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public void Auto_against_rootless_daemon_does_not_throw()
  {
    var daemon = Rootless();
    var trace = new CaptureTrace();

    DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Auto, daemon, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public void Required_against_rootful_daemon_throws_validation_exception()
  {
    var daemon = Rootful();
    var trace = new CaptureTrace();

    Action act = () => DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Required, daemon, "run-1", trace);

    act.Should().Throw<ValidationException>()
      .WithMessage("*bash.rootless: required*rootful*");
    trace.Events.Should().ContainSingle().Which.Should().BeOfType<BashConfigErrorEvent>()
      .Which.Reason.Should().Contain("required").And.Contain("rootful");
  }

  [Fact]
  public void Required_against_rootless_daemon_does_not_throw()
  {
    var daemon = Rootless();
    var trace = new CaptureTrace();

    DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Required, daemon, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public void Forbidden_against_rootless_daemon_throws_validation_exception()
  {
    var daemon = Rootless();
    var trace = new CaptureTrace();

    Action act = () => DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Forbidden, daemon, "run-1", trace);

    act.Should().Throw<ValidationException>()
      .WithMessage("*bash.rootless: forbidden*rootless*");
    trace.Events.Should().ContainSingle().Which.Should().BeOfType<BashConfigErrorEvent>()
      .Which.Reason.Should().Contain("forbidden");
  }

  [Fact]
  public void Forbidden_against_rootful_daemon_does_not_throw()
  {
    var daemon = Rootful();
    var trace = new CaptureTrace();

    DockerContainerLifecycle.EnforceRootlessMode(BashRootlessMode.Forbidden, daemon, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  // ─── ProbeMountReadabilityForRootless ────────────────────────────────────

  [Fact]
  public void Mount_probe_succeeds_when_all_paths_exist_and_are_readable()
  {
    using var dir = new TempDir();
    var plan = SinglePathPlan(dir.Path);
    var trace = new CaptureTrace();

    DockerContainerLifecycle.ProbeMountReadabilityForRootless(plan, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  [Fact]
  public void Mount_probe_throws_when_host_path_does_not_exist()
  {
    var bogusPath = Path.Combine(Path.GetTempPath(), "forge-bogus-" + Guid.NewGuid().ToString("N"));
    var plan = SinglePathPlan(bogusPath);
    var trace = new CaptureTrace();

    Action act = () => DockerContainerLifecycle.ProbeMountReadabilityForRootless(plan, "run-1", trace);

    var ex = act.Should().Throw<ValidationException>().Which;
    ex.Message.Should().Contain(bogusPath).And.Contain("does not exist");
    trace.Events.Should().ContainSingle().Which.Should().BeOfType<BashConfigErrorEvent>()
      .Which.Reason.Should().Contain("does not exist");
  }

  [Fact]
  public void Mount_probe_throws_when_one_of_multiple_paths_does_not_exist()
  {
    using var dir = new TempDir();
    var bogusPath = Path.Combine(Path.GetTempPath(), "forge-bogus-" + Guid.NewGuid().ToString("N"));
    var plan = new BashMountPlan(
      Mounts: new[]
      {
        new BashMountEntry(dir.Path, "/repo", ReadOnly: false),
        new BashMountEntry(bogusPath, "/inputs/0", ReadOnly: true)
      },
      DockerArgs: Array.Empty<string>(),
      DefaultCwd: null);
    var trace = new CaptureTrace();

    Action act = () => DockerContainerLifecycle.ProbeMountReadabilityForRootless(plan, "run-1", trace);

    var ex = act.Should().Throw<ValidationException>().Which;
    ex.Message.Should().Contain(bogusPath);
  }

  [Fact]
  public void Mount_probe_succeeds_for_empty_mount_plan()
  {
    var plan = new BashMountPlan(
      Mounts: Array.Empty<BashMountEntry>(),
      DockerArgs: Array.Empty<string>(),
      DefaultCwd: null);
    var trace = new CaptureTrace();

    DockerContainerLifecycle.ProbeMountReadabilityForRootless(plan, "run-1", trace);

    trace.Events.Should().BeEmpty();
  }

  // ─── helpers ─────────────────────────────────────────────────────────────

  private static DockerDaemonInfo Rootful() =>
    new(Rootless: false, OsType: "linux", Architecture: "x86_64", ServerVersion: "27.3.1");

  private static DockerDaemonInfo Rootless() =>
    new(Rootless: true, OsType: "linux", Architecture: "x86_64", ServerVersion: "27.3.1");

  private static BashMountPlan SinglePathPlan(string hostPath) => new(
    Mounts: new[] { new BashMountEntry(hostPath, "/repo", ReadOnly: false) },
    DockerArgs: Array.Empty<string>(),
    DefaultCwd: null);

  private sealed class CaptureTrace : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class TempDir : IDisposable
  {
    public string Path { get; }

    public TempDir()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-rootless-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
      try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
  }
}
