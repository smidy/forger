using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Core.Workspace;

namespace Forge.Tools.Docker;

/// <summary>
/// Per-run container handle returned by
/// <see cref="IBashContainerLifecycle.StartForRunAsync"/>. <see cref="BashTool"/>
/// looks it up on every exec via <see cref="IBashContainerLifecycle.TryGetForRun"/>
/// so it can resolve the container name (for <c>docker exec</c>), validate the
/// caller's <c>cwd</c> against <see cref="Mounts"/>, and apply the effective
/// per-agent diff caps.
/// </summary>
public sealed record BashContainerHandle(
  string RunId,
  string ContainerName,
  string ContainerId,
  string ImageRef,
  string ImageDigest,
  BashConfig Config,
  BashMountPlan Mounts);

/// <summary>
/// Owns the per-agent-run Docker container for the bash tool. Registered as a
/// singleton so lifecycle hooks invoked from <see cref="Forge.Agent"/> can park
/// handles in one place and retrieve them at exec time. Plan:
/// <c>docs/plans/bash-tool.md</c> §Container lifecycle.
/// </summary>
/// <remarks>
/// Why singleton: the bash tool's state (a started container + its mount plan)
/// has to outlive a single tool call and span the agent's iteration loop, but
/// must also be torn down when the loop exits. A singleton with a runId-keyed
/// dictionary gives us that without bolting per-call state onto
/// <see cref="ToolContext"/>.
/// </remarks>
public interface IBashContainerLifecycle
{
  /// <summary>
  /// Start the per-run container and block until <c>docker run -d</c> returns.
  /// Emits <see cref="BashContainerStartedEvent"/> on success;
  /// <see cref="BashConfigErrorEvent"/> on any fail-fast error (daemon down,
  /// image missing, invalid mount).
  /// </summary>
  Task<BashContainerHandle> StartForRunAsync(
    string runId,
    string stageDir,
    string runWorkspaceDir,
    BashConfig config,
    ITraceSink trace,
    CancellationToken ct);

  /// <summary>Return the live handle for the given run, or <c>null</c> if none has been started (or the run was already stopped).</summary>
  BashContainerHandle? TryGetForRun(string runId);

  /// <summary>
  /// Stop and remove the per-run container. Idempotent — unknown runIds are a
  /// no-op. Exceptions from <c>docker stop</c> / <c>docker rm -f</c> are
  /// swallowed so the owning agent loop's <c>finally</c> always cleans up the
  /// dictionary slot; callers rely on the <see cref="BashContainerStoppedEvent"/>
  /// for post-mortem signal.
  /// </summary>
  Task StopForRunAsync(string runId, string reason, ITraceSink trace, CancellationToken ct);

  /// <summary>
  /// Reap every <c>forge.run=*</c>-labelled container that is NOT currently
  /// owned by this lifecycle instance. Intended to be called lazily at Forge
  /// process start, before any bash-using run, so stale containers from an
  /// unclean prior exit are not left running indefinitely.
  /// </summary>
  Task ReapOrphansAsync(ITraceSink trace, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IBashContainerLifecycle"/> backed by an
/// <see cref="IDockerCli"/>. Tests substitute an in-memory stub for both
/// interfaces so the lifecycle is exercised without a live Docker daemon.
/// </summary>
public sealed class DockerContainerLifecycle : IBashContainerLifecycle
{
  private const int StopGracePeriodSec = 2;
  private const string RunLabelKey = "forge.run";

  private readonly IDockerCli _docker;
  private readonly ConcurrentDictionary<string, BashContainerHandle> _active =
    new(StringComparer.Ordinal);

  /// <param name="docker">The Docker CLI adapter. Injected so tests can substitute a stub.</param>
  public DockerContainerLifecycle(IDockerCli docker)
  {
    ArgumentNullException.ThrowIfNull(docker);
    _docker = docker;
  }

  /// <inheritdoc />
  public async Task<BashContainerHandle> StartForRunAsync(
    string runId,
    string stageDir,
    string runWorkspaceDir,
    BashConfig config,
    ITraceSink trace,
    CancellationToken ct)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    ArgumentException.ThrowIfNullOrWhiteSpace(stageDir);
    ArgumentException.ThrowIfNullOrWhiteSpace(runWorkspaceDir);
    ArgumentNullException.ThrowIfNull(config);
    ArgumentNullException.ThrowIfNull(trace);

    if (_active.ContainsKey(runId))
    {
      throw new AgentException(
        $"Bash container for run `{runId}` already started — the lifecycle was re-entered without a matching StopForRunAsync. This is a wiring bug in the caller.");
    }

    // Fail-fast: resolve the digest BEFORE composing mounts so a missing image
    // produces a clean ValidationException at loop start (no half-mounted
    // container left behind).
    string digest;
    try
    {
      digest = await _docker.InspectImageDigestAsync(config.Image, ct).ConfigureAwait(false);
    }
    catch (ValidationException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"image digest not resolvable: {ex.Message}"
      });
      throw;
    }
    catch (ConfigException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"docker CLI not available: {ex.Message}"
      });
      throw;
    }

    DockerDaemonInfo daemonInfo;
    try
    {
      daemonInfo = await _docker.GetDaemonInfoAsync(ct).ConfigureAwait(false);
    }
    catch (ConfigException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"docker info probe failed: {ex.Message}"
      });
      throw;
    }
    catch (FormatException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"docker info parse failed: {ex.Message}"
      });
      throw new ConfigException(
        $"`docker info` returned output the parser could not interpret. {ex.Message}");
    }

    EnforceRootlessMode(config.Rootless, daemonInfo, runId, trace);

    BashMountPlan mounts;
    try
    {
      mounts = MountComposer.Compose(config.Mounts, runWorkspaceDir, config);
    }
    catch (ValidationException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"mount composition failed: {ex.Message}"
      });
      throw;
    }

    if (daemonInfo.Rootless)
    {
      ProbeMountReadabilityForRootless(mounts, runId, trace);
    }

    var containerName = ContainerNameFor(runId);
    var scratch = ProvisionScratchIfEnabled(stageDir, config);
    var spec = new DockerRunSpec(
      ContainerName: containerName,
      RunIdLabelValue: runId,
      ImageRef: config.Image,
      Config: config,
      Mounts: mounts,
      Scratch: scratch);

    string containerId;
    try
    {
      containerId = await _docker.RunDetachedAsync(spec, ct).ConfigureAwait(false);
    }
    catch (AgentException ex) when (IsStorageOptUnsupported(ex.Message) && !string.IsNullOrEmpty(config.StorageOpt))
    {
      // Storage driver (typically Docker Desktop on macOS/Windows) rejects
      // --storage-opt. Surface a warning, strip the flag, retry once. The
      // plan: "Skipped with a warning on drivers that don't support it."
      trace.Trace(new BashStorageOptSkippedEvent
      {
        RunId = runId,
        OriginalStorageOpt = config.StorageOpt,
        DockerStderr = ex.Message
      });
      var retrySpec = spec with { Config = config with { StorageOpt = string.Empty } };
      try
      {
        containerId = await _docker.RunDetachedAsync(retrySpec, ct).ConfigureAwait(false);
      }
      catch (AgentException retryEx)
      {
        trace.Trace(new BashConfigErrorEvent
        {
          RunId = runId,
          Reason = $"docker run failed (retry without --storage-opt): {retryEx.Message}"
        });
        throw;
      }
    }
    catch (AgentException ex)
    {
      trace.Trace(new BashConfigErrorEvent
      {
        RunId = runId,
        Reason = $"docker run failed: {ex.Message}"
      });
      throw;
    }

    var handle = new BashContainerHandle(
      RunId: runId,
      ContainerName: containerName,
      ContainerId: containerId,
      ImageRef: config.Image,
      ImageDigest: digest,
      Config: config,
      Mounts: mounts);

    // Publish to the active set BEFORE the artefact write so a concurrent
    // tool call in the same run can see the handle immediately. Artefact
    // write failures are non-fatal — the container is already running.
    _active[runId] = handle;

    try
    {
      await WriteArtefactAsync(stageDir, handle, ct).ConfigureAwait(false);
    }
    catch
    {
      // Swallowed: the handle is authoritative. bash-container.json is a
      // convenience for `forge runs show` / post-mortem tooling, not a
      // correctness primitive.
    }

    var mountDescriptions = new List<string>(mounts.Mounts.Count);
    foreach (var m in mounts.Mounts)
    {
      mountDescriptions.Add($"{m.HostPath} -> {m.ContainerPath} ({(m.ReadOnly ? "ro" : "rw")})");
    }

    trace.Trace(new BashContainerStartedEvent
    {
      RunId = runId,
      ContainerName = containerName,
      ContainerId = containerId,
      ImageRef = config.Image,
      ImageDigest = digest,
      Network = config.Network,
      Mounts = mountDescriptions,
      DaemonRootless = daemonInfo.Rootless
    });

    return handle;
  }

  /// <summary>
  /// Reject the bash run if the operator's <see cref="BashRootlessMode"/>
  /// preference does not match the active daemon's posture. Plan:
  /// <c>docs/plans/bash-tool-rootless-docker.md</c> §3.
  /// </summary>
  internal static void EnforceRootlessMode(
    BashRootlessMode mode,
    DockerDaemonInfo daemon,
    string runId,
    ITraceSink trace)
  {
    switch (mode)
    {
      case BashRootlessMode.Required when !daemon.Rootless:
        trace.Trace(new BashConfigErrorEvent
        {
          RunId = runId,
          Reason = $"bash.rootless: required, but daemon is rootful (OSType={daemon.OsType}, ServerVersion={daemon.ServerVersion})"
        });
        throw new ValidationException(
          "Agent declared `bash.rootless: required` but the active Docker daemon is rootful. " +
          "Set up rootless Docker (https://docs.docker.com/engine/security/rootless/) " +
          "or change `bash.rootless` to `auto` / `forbidden`.");

      case BashRootlessMode.Forbidden when daemon.Rootless:
        trace.Trace(new BashConfigErrorEvent
        {
          RunId = runId,
          Reason = "bash.rootless: forbidden, but only a rootless daemon is reachable"
        });
        throw new ValidationException(
          "Agent declared `bash.rootless: forbidden` but only a rootless Docker daemon is reachable. " +
          "Switch DOCKER_HOST / DOCKER_CONTEXT to a rootful daemon, or change `bash.rootless` to `auto` / `required`.");
    }
  }

  /// <summary>
  /// Verify each composed bind-mount host path exists and is enumerable from
  /// the current process user before attempting <c>docker run -d</c> against
  /// a rootless daemon. Catches the common failure mode where rootless
  /// dockerd cannot read a path the operator declared. Limitation: assumes
  /// the rootless dockerd uid matches the current process user — when they
  /// differ (e.g. <c>forge</c> running as one user, rootless installed by
  /// another), this check is incomplete and the daemon's mount-time error
  /// surface is the secondary defense. Plan:
  /// <c>docs/plans/bash-tool-rootless-docker.md</c> §Acceptance.
  /// </summary>
  internal static void ProbeMountReadabilityForRootless(
    BashMountPlan mounts,
    string runId,
    ITraceSink trace)
  {
    foreach (var m in mounts.Mounts)
    {
      if (!Directory.Exists(m.HostPath))
      {
        var reason = $"mount-readability probe (rootless): host path `{m.HostPath}` does not exist as a directory";
        trace.Trace(new BashConfigErrorEvent { RunId = runId, Reason = reason });
        throw new ValidationException(
          $"Bash mount-readability probe failed: host path `{m.HostPath}` does not exist or is not a directory. " +
          $"Rootless Docker requires every mount source to be readable by the dockerd-rootless uid. " +
          $"Create the directory or remove its declaration from the agent's filesystem scope.");
      }

      try
      {
        using var enumerator = Directory.EnumerateFileSystemEntries(m.HostPath).GetEnumerator();
        enumerator.MoveNext();
      }
      catch (UnauthorizedAccessException ex)
      {
        var reason = $"mount-readability probe (rootless): cannot enumerate `{m.HostPath}` ({ex.Message})";
        trace.Trace(new BashConfigErrorEvent { RunId = runId, Reason = reason });
        throw new ValidationException(
          $"Bash mount-readability probe failed: current process user cannot enumerate `{m.HostPath}`. " +
          $"Rootless Docker mounts run with the host user's permissions; if the dockerd-rootless uid " +
          $"is the same as the current process user (the common case), this would fail at mount time. " +
          $"Underlying error: {ex.Message}");
      }
      catch (DirectoryNotFoundException ex)
      {
        var reason = $"mount-readability probe (rootless): `{m.HostPath}` not found ({ex.Message})";
        trace.Trace(new BashConfigErrorEvent { RunId = runId, Reason = reason });
        throw new ValidationException(
          $"Bash mount-readability probe failed: `{m.HostPath}` was reported as existing but " +
          $"could not be enumerated. Underlying error: {ex.Message}");
      }
    }
  }

  /// <inheritdoc />
  public BashContainerHandle? TryGetForRun(string runId) =>
    _active.TryGetValue(runId, out var h) ? h : null;

  /// <inheritdoc />
  public async Task StopForRunAsync(string runId, string reason, ITraceSink trace, CancellationToken ct)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    ArgumentNullException.ThrowIfNull(trace);

    if (!_active.TryRemove(runId, out var handle))
    {
      return;
    }

    try
    {
      await _docker.StopAndRemoveAsync(handle.ContainerName, StopGracePeriodSec, ct).ConfigureAwait(false);
    }
    catch
    {
      // Never raise from teardown — the agent's finally block must not see
      // a docker failure after a successful agent run. The stopped event
      // still fires so operators can see the run ended, even if dirty.
    }

    trace.Trace(new BashContainerStoppedEvent
    {
      RunId = runId,
      ContainerName = handle.ContainerName,
      Reason = reason
    });
  }

  /// <inheritdoc />
  public async Task ReapOrphansAsync(ITraceSink trace, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(trace);

    IReadOnlyList<DockerContainerInfo> survivors;
    try
    {
      survivors = await _docker.ListByLabelAsync(RunLabelKey, ct).ConfigureAwait(false);
    }
    catch
    {
      // Daemon missing or unreachable — no survivors to reap. Non-bash
      // commands must not be blocked on this best-effort path.
      return;
    }

    var active = _active.Values.Select(h => h.ContainerName).ToHashSet(StringComparer.Ordinal);
    foreach (var c in survivors)
    {
      if (active.Contains(c.Name))
      {
        continue;
      }

      try
      {
        await _docker.StopAndRemoveAsync(c.Name, StopGracePeriodSec, ct).ConfigureAwait(false);
      }
      catch
      {
        continue;
      }

      trace.Trace(new BashOrphanKilledEvent
      {
        ContainerName = c.Name,
        ContainerId = c.Id
      });
    }
  }

  // Run ids are already filename-safe (slug + timestamp + hex) per
  // `RunIdGenerator`. Docker container names accept [a-zA-Z0-9_.-], which is
  // a superset, so pass through unchanged beneath the `forge-bash-` prefix.
  private static string ContainerNameFor(string runId) => $"forge-bash-{runId}";

  // Docker daemon's stderr for the well-known incompatibility between
  // --storage-opt=size and non-overlay/xfs-pquota drivers. The exact wording
  // has been stable across Docker versions; we match on the core phrase.
  private static bool IsStorageOptUnsupported(string message) =>
    message.Contains("--storage-opt is supported only for overlay", StringComparison.Ordinal);

  /// <summary>
  /// Provision a host-backed scratch dir under the stage's run workspace and
  /// return a <see cref="BashScratchMount"/> pointing the container at it.
  /// Returns null when <see cref="BashConfig.AutoScratch"/> is disabled. The
  /// container path is deliberately distinct from the FsScope-derived mounts
  /// so it does not surface in the first-call mount banner.
  /// </summary>
  private static BashScratchMount? ProvisionScratchIfEnabled(string stageDir, BashConfig config)
  {
    if (!config.AutoScratch)
    {
      return null;
    }

    var hostRoot = Path.Combine(stageDir, "bash-scratch");
    foreach (var (subdir, _, _) in BashScratchMount.Subdirs)
    {
      Directory.CreateDirectory(Path.Combine(hostRoot, subdir));
    }

    return new BashScratchMount(HostRoot: hostRoot);
  }

  private static async Task WriteArtefactAsync(string stageDir, BashContainerHandle handle, CancellationToken ct)
  {
    var mountArray = new JsonArray();
    foreach (var m in handle.Mounts.Mounts)
    {
      mountArray.Add(new JsonObject
      {
        ["hostPath"] = m.HostPath,
        ["containerPath"] = m.ContainerPath,
        ["readOnly"] = m.ReadOnly
      });
    }

    var doc = new JsonObject
    {
      ["runId"] = handle.RunId,
      ["containerName"] = handle.ContainerName,
      ["containerId"] = handle.ContainerId,
      ["imageRef"] = handle.ImageRef,
      ["imageDigest"] = handle.ImageDigest,
      ["network"] = handle.Config.Network,
      ["mounts"] = mountArray,
      ["startedAtUtc"] = DateTimeOffset.UtcNow.ToString("o")
    };

    var path = Path.Combine(stageDir, "bash-container.json");
    await WorkspaceIo.WriteJsonAtomicAsync(path, doc, ct).ConfigureAwait(false);
  }
}
