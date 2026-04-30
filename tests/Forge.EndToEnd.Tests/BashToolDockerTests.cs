using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Config;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Tools;
using Forge.Tools.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the bash tool that actually starts a Docker container
/// and runs a command inside it. Gated by the <c>RequiresDocker</c> trait so
/// CI hosts without a Docker daemon skip it; run manually via:
/// <c>dotnet test --filter "Trait=RequiresDocker"</c>.
/// </summary>
/// <remarks>
/// Uses <c>alpine:latest</c> resolved to its local digest — a minimal, widely
/// available image that ships with <c>sh</c>. The test substitutes <c>sh</c>
/// for <c>bash</c> in the Docker exec argv via a custom <see cref="IDockerCli"/>
/// wrapper? No — alpine actually has <c>bash</c> only if you install it. We
/// instead pull <c>bash</c>-carrying <c>alpine/git</c> or, simplest: use the
/// debian:bookworm-slim image which has bash pre-installed.
/// </remarks>
[Trait("RequiresDocker", "true")]
public class BashToolDockerTests
{
  private const string TestImageRef = "debian:bookworm-slim";

  [Fact]
  public async Task End_to_end_exec_writes_file_visible_on_host_and_in_diffs()
  {
    if (!await DockerAvailableAsync().ConfigureAwait(true))
    {
      // Can't turn this into a Skip without xUnit v3 DynamicSkip extensions in
      // the project; use an Assert.True with a skip message so the test turns
      // red on a developer machine with the trait enabled but no daemon.
      Assert.Fail("Docker daemon is not reachable. Start Docker and retry, or exclude the `RequiresDocker` trait.");
    }

    using var env = new E2EEnv(TestImageRef);
    try
    {
      await env.PullImageAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

      var docker = new DockerProcessCli();
      var lifecycle = new DockerContainerLifecycle(docker);

      var trace = new CapturingTrace();
      var runId = $"bash-e2e-{Guid.NewGuid():N}";

      var handle = await lifecycle.StartForRunAsync(
        runId: runId,
        stageDir: env.StageDir,
        runWorkspaceDir: env.Root,
        config: env.BashConfig,
        trace: trace,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      try
      {
        var tool = new BashTool(lifecycle, docker);
        var ctx = env.NewContext(runId, trace);
        var input = new BashInput { Command = "printf greeting > /repo/hello.txt" };
        var inputNode = JsonSerializer.SerializeToNode(input, JsonSerializationDefaults.CamelCaseTool)!;

        var outputNode = await tool.ExecuteAsync(inputNode, ctx, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var output = outputNode.Deserialize<BashOutput>(JsonSerializationDefaults.CamelCaseTool)!;

        output.ExitCode.Should().Be(0, because: output.Stderr);
        var hostFile = Path.Combine(env.WriteDir, "hello.txt");
        File.Exists(hostFile).Should().BeTrue("the container's write mount is bind-mounted from the host");
        File.ReadAllText(hostFile).Should().Be("greeting");

        output.Diffs.Should().NotBeNull();
        output.Diffs!.Should().ContainSingle(d => d.Path == "hello.txt" && d.Kind == "added");
      }
      finally
      {
        await lifecycle.StopForRunAsync(runId, "test teardown", trace, CancellationToken.None).ConfigureAwait(true);
      }
    }
    finally
    {
      env.Dispose();
    }
  }

  private static async Task<bool> DockerAvailableAsync()
  {
    try
    {
      var psi = new ProcessStartInfo("docker", "info")
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var proc = Process.Start(psi);
      if (proc is null)
      {
        return false;
      }

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      try
      {
        await proc.WaitForExitAsync(cts.Token);
      }
      catch (OperationCanceledException)
      {
        try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        return false;
      }

      return proc.ExitCode == 0;
    }
    catch
    {
      return false;
    }
  }

  internal sealed class E2EEnv : IDisposable
  {
    public string Root { get; }
    public string StageDir { get; }
    public string WriteDir { get; }
    public string ReadDir { get; }
    public BashConfig BashConfig { get; }

    private readonly string _imageRef;

    private readonly string _repoRoot;

    public E2EEnv(string imageRef)
    {
      _imageRef = imageRef;
      Root = Path.Combine(Path.GetTempPath(), "forge-bash-e2e-run-" + Guid.NewGuid().ToString("N"));
      _repoRoot = Path.Combine(Path.GetTempPath(), "forge-bash-e2e-repo-" + Guid.NewGuid().ToString("N"));
      StageDir = Path.Combine(Root, "stage");
      WriteDir = Path.Combine(_repoRoot, "write");
      ReadDir = Path.Combine(_repoRoot, "read");
      Directory.CreateDirectory(StageDir);
      Directory.CreateDirectory(WriteDir);
      Directory.CreateDirectory(ReadDir);

      BashConfig = new BashConfig
      {
        Image = imageRef,
        TimeoutSec = 15,
        User = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
          ? $"{GetHostUidForLinux()}:{GetHostUidForLinux()}"
          : "1000:1000",
        Mounts = new[]
        {
          new BashMount(Path.GetFullPath(WriteDir), "/repo", BashMountMode.ReadWrite),
          new BashMount(Path.GetFullPath(ReadDir), "/inputs/0", BashMountMode.ReadOnly)
        }
      };
    }

    public async Task PullImageAsync(CancellationToken ct)
    {
      // The lifecycle will only inspect — not pull. Make sure the image is local.
      var psi = new ProcessStartInfo("docker", $"pull {_imageRef}")
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var proc = Process.Start(psi) ?? throw new InvalidOperationException("docker failed to start");
      await proc.WaitForExitAsync(ct);
      if (proc.ExitCode != 0)
      {
        var err = await proc.StandardError.ReadToEndAsync(ct);
        throw new InvalidOperationException($"docker pull {_imageRef} failed: {err}");
      }
    }

    public ToolContext NewContext(string runId, ITraceSink trace) => new(
      RunId: runId,
      RunWorkspace: Root,
      StageDir: StageDir,
      StageId: "stage-0",
      IterationIndex: null,
      Llm: NullLlmClient.Instance,
      Trace: trace,
      Logger: NullLogger.Instance,
      CancellationToken: default,
      NextToolOutputIdx: () => 0);

    private static int GetHostUidForLinux()
    {
      // On Linux only — best effort. Returning 1000 is a safe default on
      // most dev machines.
      if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      {
        return 1000;
      }

      try
      {
        var uidText = File.ReadAllText("/proc/self/loginuid").Trim();
        if (int.TryParse(uidText, out var uid) && uid > 0 && uid < 65536)
        {
          return uid;
        }
      }
      catch
      {
      }

      return 1000;
    }

    public void Dispose()
    {
      try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
      try { Directory.Delete(_repoRoot, recursive: true); } catch { /* best-effort */ }
    }
  }

  internal sealed class CapturingTrace : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  internal sealed class NullLlmClient : ILlmClient
  {
    public static NullLlmClient Instance { get; } = new();
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct) =>
      throw new NotSupportedException("LLM not used in bash-tool e2e tests.");
  }
}
