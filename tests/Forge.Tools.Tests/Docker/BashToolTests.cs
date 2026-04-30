using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Tools.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Pins the bash tool's contract above the Docker CLI: input validation, env /
/// cwd / timeout resolution, pre/post diff scan invocation, ledger append with
/// <c>ToolName="bash"</c>, and output shape. The lifecycle and CLI are stubbed
/// so the tool is exercised without a real Docker daemon. Plan:
/// <c>docs/plans/bash-tool.md</c> §Tool contract.
/// </summary>
public class BashToolTests
{
  [Fact]
  public async Task Blank_command_is_rejected_before_any_docker_call()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(), new BashInput { Command = "   " });

    await act.Should().ThrowAsync<ValidationException>()
      .WithMessage("*command*non-empty*");
    docker.ExecCalls.Should().BeEmpty("validation runs before the docker call");
  }

  [Fact]
  public async Task Missing_container_for_run_surfaces_config_exception()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(handle: null); // nothing started for the run
    var tool = new BashTool(lifecycle, new StubDocker());

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(), new BashInput { Command = "echo hi" });

    await act.Should().ThrowAsync<ConfigException>()
      .WithMessage("*No bash container has been started*");
  }

  [Fact]
  public async Task Successful_exec_returns_docker_output_and_invokes_scan()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker
    {
      Result = new DockerExecResult(
        ExitCode: 0,
        Stdout: "ok\n",
        Stderr: string.Empty,
        Truncated: false,
        TimedOut: false,
        HardKilled: false,
        DurationMs: 42)
    };
    var tool = new BashTool(lifecycle, docker);

    var output = await InvokeAsync(tool, env.NewContext(), new BashInput { Command = "echo ok" });

    output.Stdout.Should().Be("ok\n");
    output.ExitCode.Should().Be(0);
    output.DurationMs.Should().Be(42);
    output.Truncated.Should().BeFalse();
    output.Diffs.Should().NotBeNull("write roots are declared so the scan contract returns an empty list, not null");
    output.Diffs!.Should().BeEmpty();
    output.DiffsPartial.Should().BeFalse();
    docker.ExecCalls.Should().HaveCount(1);
  }

  [Fact]
  public async Task First_call_prepends_mount_banner_when_ShowMountTable_is_enabled()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.HandleWithBanner());
    var docker = new StubDocker
    {
      Result = new DockerExecResult(
        ExitCode: 0,
        Stdout: "ok\n",
        Stderr: string.Empty,
        Truncated: false,
        TimedOut: false,
        HardKilled: false,
        DurationMs: 1)
    };
    var tool = new BashTool(lifecycle, docker);

    var first = await InvokeAsync(tool, env.NewContext(), new BashInput { Command = "echo ok" });
    var second = await InvokeAsync(tool, env.NewContext(), new BashInput { Command = "echo ok" });

    first.Stdout.Should().Contain("=== Bash container ready (first call) ===");
    first.Stdout.Should().EndWith("ok\n");
    second.Stdout.Should().Be("ok\n", "banner is one-shot per container — subsequent calls return stdout verbatim");
  }

  [Fact]
  public async Task Exec_spec_carries_default_cwd_and_container_name()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    await InvokeAsync(tool, env.NewContext(), new BashInput { Command = "pwd" });

    var spec = docker.ExecCalls.Single();
    spec.ContainerName.Should().Be(env.Handle.ContainerName);
    spec.WorkDirContainerPath.Should().Be("/repo", "first writable mount is the default cwd");
    spec.TimeoutSec.Should().Be(env.Handle.Config.TimeoutSec, "no per-call override means agent's configured timeout is used");
  }

  [Fact]
  public async Task Cwd_override_under_an_existing_mount_is_accepted()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    await InvokeAsync(tool, env.NewContext(),
      new BashInput { Command = "ls", Cwd = "/inputs/0" });

    docker.ExecCalls.Single().WorkDirContainerPath.Should().Be("/inputs/0");
  }

  [Fact]
  public async Task Cwd_override_outside_any_mount_is_rejected()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(),
      new BashInput { Command = "ls", Cwd = "/etc" });

    await act.Should().ThrowAsync<ValidationException>();
    docker.ExecCalls.Should().BeEmpty();
  }

  [Fact]
  public async Task Env_override_in_allow_list_is_merged()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.HandleWithEnvAllow("FOO", "BAR"));
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    await InvokeAsync(tool, env.NewContext(),
      new BashInput
      {
        Command = "env",
        Env = new Dictionary<string, string> { ["FOO"] = "1" }
      });

    docker.ExecCalls.Single().EffectiveEnv.Should().ContainKey("FOO").WhoseValue.Should().Be("1");
  }

  [Fact]
  public async Task Env_override_outside_allow_list_is_rejected()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.HandleWithEnvAllow("FOO"));
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(),
      new BashInput
      {
        Command = "env",
        Env = new Dictionary<string, string> { ["SECRET"] = "x" }
      });

    await act.Should().ThrowAsync<ValidationException>()
      .WithMessage("*env_allow*");
  }

  [Fact]
  public async Task Env_override_matching_forbidden_pattern_is_rejected_even_if_on_allow_list()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.HandleWithEnvAllow("PATH"));
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(),
      new BashInput
      {
        Command = "env",
        Env = new Dictionary<string, string> { ["PATH"] = "/evil" }
      });

    await act.Should().ThrowAsync<ValidationException>()
      .WithMessage("*forbidden*");
  }

  [Fact]
  public async Task Timeout_override_is_clamped_to_agent_ceiling()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.HandleWithTimeout(30));
    var docker = new StubDocker();
    var tool = new BashTool(lifecycle, docker);

    await InvokeAsync(tool, env.NewContext(), new BashInput { Command = "sleep 1", TimeoutSec = 9_000 });

    docker.ExecCalls.Single().TimeoutSec.Should().Be(30, "agent ceiling is 30s so request for 9000s is clamped");
  }

  [Fact]
  public async Task Wall_clock_timeout_raises_validation_exception()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker
    {
      Result = new DockerExecResult(
        ExitCode: 137,
        Stdout: string.Empty,
        Stderr: "killed",
        Truncated: false,
        TimedOut: true,
        HardKilled: false,
        DurationMs: 30_000)
    };
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(), new BashInput { Command = "sleep 1000" });

    await act.Should().ThrowAsync<ValidationException>()
      .WithMessage("*wall-clock timeout*");
  }

  [Fact]
  public async Task Hard_kill_raises_validation_exception()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var docker = new StubDocker
    {
      Result = new DockerExecResult(
        ExitCode: 137,
        Stdout: "...",
        Stderr: "...",
        Truncated: true,
        TimedOut: false,
        HardKilled: true,
        DurationMs: 5_000)
    };
    var tool = new BashTool(lifecycle, docker);

    Func<Task> act = () => InvokeAsync(tool, env.NewContext(), new BashInput { Command = "yes" });

    await act.Should().ThrowAsync<ValidationException>()
      .WithMessage("*64 MiB*");
  }

  [Fact]
  public async Task Exec_records_ledger_entry_for_file_created_during_call()
  {
    using var env = new BashTestEnv();
    var lifecycle = new StubLifecycle(env.Handle);
    var ledger = new AgentWriteLedger();
    var ctx = env.NewContextWithLedger(ledger);

    var newFile = Path.Combine(env.Write, "created.txt");
    var docker = new StubDocker
    {
      ExecAction = () => File.WriteAllText(newFile, "hello"),
      Result = new DockerExecResult(
        ExitCode: 0,
        Stdout: string.Empty,
        Stderr: string.Empty,
        Truncated: false,
        TimedOut: false,
        HardKilled: false,
        DurationMs: 10)
    };
    var tool = new BashTool(lifecycle, docker);

    var output = await InvokeAsync(tool, ctx, new BashInput { Command = "echo hi > /repo/created.txt" });

    output.Diffs.Should().NotBeNull();
    output.Diffs!.Should().ContainSingle(d => d.Path == "created.txt" && d.Kind == "added");

    ledger.Entries.Should().ContainSingle(e => e.ToolName == "bash");
    var entry = ledger.Entries.Single(e => e.ToolName == "bash");
    entry.RequestedPath.Should().Be("created.txt");
    entry.ResolvedPath.Should().Be(Path.GetFullPath(newFile));
    entry.BytesWritten.Should().Be(5);
    entry.WasNoOp.Should().BeFalse();
  }

  private static async Task<BashOutput> InvokeAsync(BashTool tool, ToolContext ctx, BashInput input)
  {
    var node = JsonSerializer.SerializeToNode(input, JsonSerializationDefaults.CamelCaseTool)!;
    var outputNode = await tool.ExecuteAsync(node, ctx, TestContext.Current.CancellationToken);
    return outputNode.Deserialize<BashOutput>(JsonSerializationDefaults.CamelCaseTool)!;
  }

  private sealed class StubLifecycle : IBashContainerLifecycle
  {
    private readonly BashContainerHandle? _handle;

    public StubLifecycle(BashContainerHandle? handle)
    {
      _handle = handle;
    }

    public Task<BashContainerHandle> StartForRunAsync(
      string runId, string stageDir, string runWorkspaceDir, BashConfig config,
      ITraceSink trace, CancellationToken ct) =>
        throw new NotSupportedException("StartForRunAsync is not exercised by these tests — lifecycle is pre-seeded.");

    public BashContainerHandle? TryGetForRun(string runId) => _handle;

    public Task StopForRunAsync(string runId, string reason, ITraceSink trace, CancellationToken ct) =>
      Task.CompletedTask;

    public Task ReapOrphansAsync(ITraceSink trace, CancellationToken ct) => Task.CompletedTask;
  }

  private sealed class StubDocker : IDockerCli
  {
    public List<DockerExecSpec> ExecCalls { get; } = new();
    public DockerExecResult Result { get; set; } = new(
      ExitCode: 0, Stdout: string.Empty, Stderr: string.Empty,
      Truncated: false, TimedOut: false, HardKilled: false, DurationMs: 0);

    /// <summary>Side effect to run just before returning <see cref="Result"/> (used to simulate a file-creating exec).</summary>
    public Action? ExecAction { get; set; }

    public Task<string> InspectImageDigestAsync(string imageRef, CancellationToken ct) =>
      Task.FromResult("sha256:" + new string('a', 64));

    public Task<string> RunDetachedAsync(DockerRunSpec spec, CancellationToken ct) =>
      Task.FromResult("containerid");

    public Task<DockerExecResult> ExecAsync(DockerExecSpec spec, CancellationToken ct)
    {
      ExecCalls.Add(spec);
      ExecAction?.Invoke();
      return Task.FromResult(Result);
    }

    public Task StopAndRemoveAsync(string containerName, int gracePeriodSec, CancellationToken ct) =>
      Task.CompletedTask;

    public Task<IReadOnlyList<DockerContainerInfo>> ListByLabelAsync(string labelKey, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DockerContainerInfo>>(Array.Empty<DockerContainerInfo>());

    public Task<DockerDaemonInfo> GetDaemonInfoAsync(CancellationToken ct) =>
      Task.FromResult(new DockerDaemonInfo(
        Rootless: false,
        OsType: "linux",
        Architecture: "x86_64",
        ServerVersion: "27.3.1"));
  }

  internal sealed class BashTestEnv : IDisposable
  {
    public string Root { get; }
    public string Run { get; }
    public string Read { get; }
    public string Write { get; }
    public BashContainerHandle Handle { get; }

    public BashTestEnv()
    {
      Root = Path.Combine(Path.GetTempPath(), "forge-bashtool-" + Guid.NewGuid().ToString("N"));
      Run = Path.Combine(Root, "run");
      Read = Path.Combine(Root, "read");
      Write = Path.Combine(Root, "write");
      Directory.CreateDirectory(Run);
      Directory.CreateDirectory(Read);
      Directory.CreateDirectory(Write);

      var userMounts = new[]
      {
        new BashMount(Path.GetFullPath(Write), "/repo", BashMountMode.ReadWrite),
        new BashMount(Path.GetFullPath(Read), "/inputs/0", BashMountMode.ReadOnly)
      };
      var plan = MountComposer.Compose(userMounts, Run);
      var cfg = DefaultBashConfig();
      Handle = new BashContainerHandle(
        RunId: "run-0",
        ContainerName: "forge-bash-run-0",
        ContainerId: "cid",
        ImageRef: cfg.Image,
        ImageDigest: "sha256:" + new string('b', 64),
        Config: cfg,
        Mounts: plan);
    }

    public ToolContext NewContext() => new(
      RunId: Handle.RunId,
      RunWorkspace: Run,
      StageDir: Run,
      StageId: "stage-0",
      IterationIndex: null,
      Llm: NullLlmClient.Instance,
      Trace: new NullTraceSink(),
      Logger: NullLogger.Instance,
      CancellationToken: default,
      NextToolOutputIdx: () => 0);

    public ToolContext NewContextWithLedger(AgentWriteLedger ledger) => NewContext() with { WriteLedger = ledger };

    public BashContainerHandle HandleWithEnvAllow(params string[] keys)
    {
      var cfg = new BashConfig
      {
        Image = Handle.Config.Image,
        EnvAllow = keys,
        TimeoutSec = Handle.Config.TimeoutSec
      };
      return Handle with { Config = cfg };
    }

    public BashContainerHandle HandleWithTimeout(int timeoutSec)
    {
      var cfg = new BashConfig
      {
        Image = Handle.Config.Image,
        TimeoutSec = timeoutSec
      };
      return Handle with { Config = cfg };
    }

    private static BashConfig DefaultBashConfig() => new()
    {
      Image = "docker.io/library/debian@sha256:" + new string('c', 64),
      TimeoutSec = 30,
      // Suppress the first-call mount banner by default so the legacy
      // assertions that compare stdout verbatim keep working. Tests that
      // exercise the banner opt back in via `HandleWithBanner()`.
      ShowMountTable = false
    };

    public BashContainerHandle HandleWithBanner()
    {
      var cfg = Handle.Config with { ShowMountTable = true };
      return Handle with { Config = cfg };
    }

    public void Dispose()
    {
      try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
  }

  private sealed class NullTraceSink : ITraceSink
  {
    public void Trace(TraceEvent e) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class NullLlmClient : ILlmClient
  {
    public static NullLlmClient Instance { get; } = new();

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct) =>
      throw new NotSupportedException("LLM not used in bash-tool tests.");
  }
}
