using System.Diagnostics;
using System.Globalization;
using Forge.Core.Config;
using Forge.Core.Exceptions;

namespace Forge.Tools.Docker;

/// <summary>
/// Specification for a <c>docker run -d</c> that launches a bash-tool
/// container. Every resource-limit and security flag is composed from
/// <paramref name="Config"/>; the caller cannot bypass the defaults set by
/// <see cref="DockerArgv.BuildRunArgs"/>. Plan: <c>docs/plans/bash-tool.md</c>.
/// </summary>
public sealed record DockerRunSpec(
  string ContainerName,
  string RunIdLabelValue,
  string ImageRef,
  BashConfig Config,
  BashMountPlan Mounts,
  BashScratchMount? Scratch = null);

/// <summary>
/// Optional Forge-managed scratch mount injected when
/// <see cref="BashConfig.AutoScratch"/> is true. One bind mount
/// (<paramref name="HostRoot"/> → <paramref name="ContainerRoot"/>, rw)
/// plus the env vars defined by <see cref="Subdirs"/> pointing at its
/// subdirs. Separate from <see cref="BashMountPlan"/> so it does not appear
/// in the FsScope-derived mount table the agent sees — operators get the
/// scratch benefits without the mount banner getting noisier.
/// </summary>
public sealed record BashScratchMount(string HostRoot, string ContainerRoot = BashScratchMount.DefaultContainerRoot)
{
  /// <summary>Container path the host scratch dir is mounted at by default.</summary>
  public const string DefaultContainerRoot = "/forge-scratch";

  /// <summary>
  /// Subdir names relative to <see cref="ContainerRoot"/> that Forge auto-provisions
  /// on the host and exposes to the container via well-known env vars. Shared by
  /// <see cref="DockerContainerLifecycle"/> (host-side mkdir) and
  /// <see cref="DockerArgv.BuildRunArgs"/> (env-var injection) so the two sides
  /// cannot drift. Scoped to package / temp caches only — we deliberately do
  /// NOT redirect <c>BaseIntermediateOutputPath</c> / <c>BaseOutputPath</c>:
  /// a single flat obj/bin path collapses every project's <c>project.assets.json</c>
  /// onto one file (last-writer-wins), which breaks multi-project restores.
  /// Each csproj's relative <c>obj/</c> keeps the isolation MSBuild expects;
  /// container/host obj-poisoning is handled by the agent running
  /// <c>dotnet clean</c> before <c>dotnet build</c>.
  /// </summary>
  public static readonly IReadOnlyList<(string Subdir, string EnvVar, bool TrailingSlash)> Subdirs = new[]
  {
    ("home",   "HOME",           false),
    ("tmp",    "TMPDIR",         false),
    ("nuget",  "NUGET_PACKAGES", false),
  };
}

/// <summary>Specification for a single <c>docker exec</c> inside a running bash-tool container.</summary>
public sealed record DockerExecSpec(
  string ContainerName,
  string WorkDirContainerPath,
  string Command,
  IReadOnlyDictionary<string, string> EffectiveEnv,
  int TimeoutSec,
  int StdoutCapBytes,
  int StderrCapBytes,
  long HardKillBytes);

/// <summary>Return type of <see cref="IDockerCli.ExecAsync"/>.</summary>
public sealed record DockerExecResult(
  int ExitCode,
  string Stdout,
  string Stderr,
  bool Truncated,
  bool TimedOut,
  bool HardKilled,
  long DurationMs);

/// <summary>
/// Metadata returned by <c>docker ps --format json</c> for a single
/// <c>forge.run=*</c> container. Used by the janitor to decide what to reap.
/// </summary>
public sealed record DockerContainerInfo(string Name, string Id, string Image);

/// <summary>
/// Thin abstraction over the Docker CLI. Production path is
/// <see cref="DockerProcessCli"/>; unit tests substitute a stub so they can
/// run without a live Docker daemon.
/// </summary>
public interface IDockerCli
{
  /// <summary>Resolve the canonical image digest via <c>docker inspect</c>. Throws <see cref="ValidationException"/> if the image is not present locally.</summary>
  Task<string> InspectImageDigestAsync(string imageRef, CancellationToken ct);

  /// <summary>Start the container detached. Returns the Docker-assigned container id.</summary>
  Task<string> RunDetachedAsync(DockerRunSpec spec, CancellationToken ct);

  /// <summary>Run a command inside the container. The command is piped via stdin to <c>bash -lc</c> so callers never have to shell-quote host-side.</summary>
  Task<DockerExecResult> ExecAsync(DockerExecSpec spec, CancellationToken ct);

  /// <summary>Stop and remove the named container. Safe to call on an already-removed container.</summary>
  Task StopAndRemoveAsync(string containerName, int gracePeriodSec, CancellationToken ct);

  /// <summary>List containers carrying <paramref name="labelKey"/>. Used by the orphan janitor.</summary>
  Task<IReadOnlyList<DockerContainerInfo>> ListByLabelAsync(string labelKey, CancellationToken ct);

  /// <summary>
  /// Snapshot of the active daemon's identity and security posture via a
  /// single composite <c>docker info</c> call. Implementations cache the
  /// result for their instance lifetime — the daemon does not switch under
  /// us mid-run for a one-shot Forge invocation. Used by the bash-tool
  /// lifecycle to enforce <see cref="BashRootlessMode"/>. Plan:
  /// <c>docs/plans/bash-tool-rootless-docker.md</c>.
  /// </summary>
  Task<DockerDaemonInfo> GetDaemonInfoAsync(CancellationToken ct);
}

/// <summary>
/// Pure argv builders for every <c>docker</c> invocation the bash tool makes.
/// Pure so <see cref="Docker.DockerArgvTests"/> can assert the flag shape
/// without a Docker daemon. All security-posture flags are emitted here and
/// cannot be suppressed by config.
/// </summary>
public static class DockerArgv
{
  /// <summary>
  /// Compose argv for <c>docker run -d --rm --name &lt;N&gt; --label forge.run=&lt;id&gt; &lt;security+resource+mounts+env&gt; &lt;image&gt; sleep infinity</c>.
  /// </summary>
  public static IReadOnlyList<string> BuildRunArgs(DockerRunSpec spec)
  {
    ArgumentNullException.ThrowIfNull(spec);
    var c = spec.Config;
    var args = new List<string>
    {
      "run",
      "-d",
      "--rm",
      "--name", spec.ContainerName,
      "--label", $"forge.run={spec.RunIdLabelValue}",
      "--network", c.Network,
      "--cap-drop", "ALL",
      "--security-opt", "no-new-privileges",
      "--user", c.User,
      "--pids-limit", c.PidsLimit.ToString(CultureInfo.InvariantCulture),
      "--ulimit", $"nproc={c.PidsLimit}:{c.PidsLimit}",
      "--memory", c.Memory,
      "--cpus", c.Cpus.ToString("0.###", CultureInfo.InvariantCulture),
      "--tmpfs", $"/tmp:size={c.TmpfsSize}",
      "--platform", c.Platform,
      "--workdir", spec.Mounts.DefaultCwd?.ContainerPath ?? MountComposer.RunContainerPath
    };

    if (c.ReadOnlyRoot)
    {
      args.Add("--read-only");
    }

    if (!string.IsNullOrEmpty(c.StorageOpt))
    {
      args.Add("--storage-opt");
      args.Add(c.StorageOpt);
    }

    foreach (var m in spec.Mounts.DockerArgs)
    {
      args.Add(m);
    }

    if (spec.Scratch is { } scratch)
    {
      // HOME / TMPDIR / NUGET_PACKAGES point package + temp caches at the
      // host-backed scratch dir so cold restore has real disk and persists
      // across exec calls in the same run. We explicitly do NOT redirect
      // BaseIntermediateOutputPath / BaseOutputPath: a flat env-var path
      // collides every project's project.assets.json onto a single file
      // (last-writer-wins) which breaks multi-project builds. Relative
      // obj/bin stays under each csproj; the agent reconciles host vs
      // container obj/ via `dotnet clean` before `dotnet build`.
      args.Add("-v");
      args.Add($"{scratch.HostRoot}:{scratch.ContainerRoot}:rw");
      foreach (var (subdir, envVar, trailingSlash) in BashScratchMount.Subdirs)
      {
        args.Add("-e");
        args.Add($"{envVar}={scratch.ContainerRoot}/{subdir}{(trailingSlash ? "/" : string.Empty)}");
      }
    }

    args.Add(spec.ImageRef);
    args.Add("sleep");
    args.Add("infinity");
    return args;
  }

  /// <summary>
  /// Compose argv for a single <c>docker exec -i --workdir … --env KEY=VAL … &lt;name&gt; bash -lc -</c>. The command is fed on stdin by the caller.
  /// </summary>
  public static IReadOnlyList<string> BuildExecArgs(DockerExecSpec spec)
  {
    ArgumentNullException.ThrowIfNull(spec);
    var args = new List<string>
    {
      "exec",
      "-i",
      "--workdir", spec.WorkDirContainerPath
    };

    foreach (var (k, v) in spec.EffectiveEnv)
    {
      args.Add("--env");
      args.Add($"{k}={v}");
    }

    args.Add(spec.ContainerName);
    args.Add("bash");
    args.Add("-lc");
    args.Add(spec.Command);
    return args;
  }

  /// <summary>Compose argv for <c>docker stop --time=N name</c>.</summary>
  public static IReadOnlyList<string> BuildStopArgs(string containerName, int gracePeriodSec) => new[]
  {
    "stop",
    "--time",
    gracePeriodSec.ToString(CultureInfo.InvariantCulture),
    containerName
  };

  /// <summary>Compose argv for <c>docker rm -f name</c>.</summary>
  public static IReadOnlyList<string> BuildRmForceArgs(string containerName) => new[]
  {
    "rm",
    "-f",
    containerName
  };

  /// <summary>Compose argv for <c>docker inspect --format='{{.Id}}' imageRef</c>.</summary>
  public static IReadOnlyList<string> BuildInspectImageArgs(string imageRef) => new[]
  {
    "inspect",
    "--format",
    "{{.Id}}",
    imageRef
  };

  /// <summary>Compose argv for <c>docker ps --filter label=key --format {.Names}\t{.ID}\t{.Image}</c>.</summary>
  public static IReadOnlyList<string> BuildListByLabelArgs(string labelKey) => new[]
  {
    "ps",
    "--all",
    "--filter",
    $"label={labelKey}",
    "--format",
    "{{.Names}}\t{{.ID}}\t{{.Image}}"
  };

  /// <summary>
  /// Compose argv for <c>docker info --format &lt;format&gt;</c>. The default
  /// format is <see cref="DockerInfoParser.FormatString"/>; callers can pass
  /// a different template if needed (the doctor health-check probe uses the
  /// same constant so the parsing path is shared).
  /// </summary>
  public static IReadOnlyList<string> BuildInfoArgs(string formatString) => new[]
  {
    "info",
    "--format",
    formatString
  };
}

/// <summary>
/// Default <see cref="IDockerCli"/> implementation backed by
/// <see cref="System.Diagnostics.Process"/>. Redirects stdout/stderr through
/// <see cref="StreamCap"/> so runaway commands cannot drain host memory.
/// </summary>
public sealed class DockerProcessCli : IDockerCli
{
  private readonly string _dockerExecutable;
  private readonly SemaphoreSlim _daemonInfoLock = new(1, 1);
  private DockerDaemonInfo? _cachedDaemonInfo;

  /// <param name="dockerExecutable">Override the path to the Docker CLI binary. Defaults to <c>docker</c> (resolved via <c>PATH</c>).</param>
  public DockerProcessCli(string dockerExecutable = "docker")
  {
    _dockerExecutable = dockerExecutable;
  }

  public async Task<string> InspectImageDigestAsync(string imageRef, CancellationToken ct)
  {
    var argv = DockerArgv.BuildInspectImageArgs(imageRef);
    var result = await RunCaptureAsync(argv, stdin: null, timeoutSec: 30, ct).ConfigureAwait(false);
    if (result.ExitCode != 0)
    {
      throw new ValidationException(
        $"docker inspect failed for image `{imageRef}` (exit {result.ExitCode}). Pull it first — Forge never runs `docker pull` implicitly. stderr: {result.Stderr.Trim()}");
    }

    return result.Stdout.Trim();
  }

  public async Task<string> RunDetachedAsync(DockerRunSpec spec, CancellationToken ct)
  {
    var argv = DockerArgv.BuildRunArgs(spec);
    var result = await RunCaptureAsync(argv, stdin: null, timeoutSec: 60, ct).ConfigureAwait(false);
    if (result.ExitCode != 0)
    {
      throw new AgentException(
        $"docker run failed for container `{spec.ContainerName}` (exit {result.ExitCode}). stderr: {result.Stderr.Trim()}");
    }

    return result.Stdout.Trim();
  }

  public async Task<DockerExecResult> ExecAsync(DockerExecSpec spec, CancellationToken ct)
  {
    // BuildExecArgs composes the argv including a final `bash -lc <command>` —
    // keeping command as the last arg (not via stdin) sidesteps a class of
    // stdin-framing issues at the cost of longer argv. Windows command-line
    // length limit is ~32k; bash commands are expected to stay well below.
    var argv = DockerArgv.BuildExecArgs(spec);
    var sw = Stopwatch.StartNew();
    var result = await RunCaptureAsync(
      argv,
      stdin: null,
      timeoutSec: spec.TimeoutSec,
      ct,
      stdoutCap: spec.StdoutCapBytes,
      stderrCap: spec.StderrCapBytes,
      hardKillBytes: spec.HardKillBytes).ConfigureAwait(false);
    sw.Stop();

    return new DockerExecResult(
      ExitCode: result.ExitCode,
      Stdout: result.Stdout,
      Stderr: result.Stderr,
      Truncated: result.Truncated,
      TimedOut: result.TimedOut,
      HardKilled: result.HardKilled,
      DurationMs: sw.ElapsedMilliseconds);
  }

  public async Task StopAndRemoveAsync(string containerName, int gracePeriodSec, CancellationToken ct)
  {
    var stopArgv = DockerArgv.BuildStopArgs(containerName, gracePeriodSec);
    var stop = await RunCaptureAsync(stopArgv, stdin: null, timeoutSec: gracePeriodSec + 5, ct).ConfigureAwait(false);
    // Either success or "container not found" — the whole point of --rm is
    // that normal shutdown already removed it. Fall through to rm -f only if
    // stop produced a non-zero-and-not-NoSuchContainer error.
    if (stop.ExitCode == 0)
    {
      return;
    }

    if (stop.Stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    var rmArgv = DockerArgv.BuildRmForceArgs(containerName);
    await RunCaptureAsync(rmArgv, stdin: null, timeoutSec: 10, ct).ConfigureAwait(false);
  }

  public async Task<DockerDaemonInfo> GetDaemonInfoAsync(CancellationToken ct)
  {
    if (_cachedDaemonInfo is { } cached)
    {
      return cached;
    }

    await _daemonInfoLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (_cachedDaemonInfo is { } cachedAfterLock)
      {
        return cachedAfterLock;
      }

      var argv = DockerArgv.BuildInfoArgs(DockerInfoParser.FormatString);
      var result = await RunCaptureAsync(argv, stdin: null, timeoutSec: 10, ct).ConfigureAwait(false);
      if (result.ExitCode != 0)
      {
        throw new ConfigException(
          $"docker info failed (exit {result.ExitCode}). Is the daemon reachable? stderr: {result.Stderr.Trim()}");
      }

      var parsed = DockerInfoParser.Parse(result.Stdout);
      _cachedDaemonInfo = parsed;
      return parsed;
    }
    finally
    {
      _daemonInfoLock.Release();
    }
  }

  public async Task<IReadOnlyList<DockerContainerInfo>> ListByLabelAsync(string labelKey, CancellationToken ct)
  {
    var argv = DockerArgv.BuildListByLabelArgs(labelKey);
    var result = await RunCaptureAsync(argv, stdin: null, timeoutSec: 15, ct).ConfigureAwait(false);
    if (result.ExitCode != 0)
    {
      return Array.Empty<DockerContainerInfo>();
    }

    var infos = new List<DockerContainerInfo>();
    foreach (var line in result.Stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
    {
      var parts = line.Split('\t', 3);
      if (parts.Length == 3)
      {
        infos.Add(new DockerContainerInfo(parts[0], parts[1], parts[2]));
      }
    }

    return infos;
  }

  private sealed record CaptureResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool Truncated,
    bool TimedOut,
    bool HardKilled);

  private async Task<CaptureResult> RunCaptureAsync(
    IReadOnlyList<string> argv,
    string? stdin,
    int timeoutSec,
    CancellationToken ct,
    int stdoutCap = 64 * 1024,
    int stderrCap = 16 * 1024,
    long hardKillBytes = StreamCap.DefaultHardKillBytes)
  {
    var psi = new ProcessStartInfo
    {
      FileName = _dockerExecutable,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = stdin is not null,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (var a in argv)
    {
      psi.ArgumentList.Add(a);
    }

    using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
    try
    {
      if (!proc.Start())
      {
        throw new AgentException("Failed to start docker CLI process.");
      }
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
      throw new ConfigException(
        $"`docker` executable not found on PATH (or `{_dockerExecutable}` if overridden). Install Docker or remove `bash` from the agent's `tools:` list. Underlying error: {ex.Message}");
    }

    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
    linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

    if (stdin is not null)
    {
      await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
      proc.StandardInput.Close();
    }

    var stdoutTask = StreamCap.ReadCappedAsync(proc.StandardOutput.BaseStream, stdoutCap, hardKillBytes, linked.Token);
    var stderrTask = StreamCap.ReadCappedAsync(proc.StandardError.BaseStream, stderrCap, hardKillBytes, linked.Token);

    var timedOut = false;
    var hardKilled = false;
    try
    {
      await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      if (!ct.IsCancellationRequested)
      {
        timedOut = true;
      }

      TryKill(proc);
      await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    CappedStreamResult stdout;
    CappedStreamResult stderr;
    try
    {
      stdout = await stdoutTask.ConfigureAwait(false);
      stderr = await stderrTask.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      stdout = new CappedStreamResult(string.Empty, Truncated: true, HardKillHit: false, TotalBytesRead: 0);
      stderr = new CappedStreamResult(string.Empty, Truncated: true, HardKillHit: false, TotalBytesRead: 0);
    }

    if (stdout.HardKillHit || stderr.HardKillHit)
    {
      hardKilled = true;
      TryKill(proc);
    }

    return new CaptureResult(
      proc.ExitCode,
      stdout.Text,
      stderr.Text,
      stdout.Truncated || stderr.Truncated,
      timedOut,
      hardKilled);
  }

  private static void TryKill(Process proc)
  {
    try
    {
      if (!proc.HasExited)
      {
        proc.Kill(entireProcessTree: true);
      }
    }
    catch
    {
    }
  }
}
