using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Tools.Docker;

namespace Forge.Tools;

/// <summary>
/// Tool input shape for the <c>bash</c> built-in. Matches the plan spec at
/// <c>docs/plans/bash-tool.md</c> §Tool contract.
/// </summary>
public sealed class BashInput
{
  /// <summary>Required. Passed verbatim to <c>bash -lc</c> inside the container. Never interpolated into a host shell.</summary>
  public required string Command { get; init; }

  /// <summary>Optional absolute path inside the container — must be under a declared <c>bash.mounts</c> path or <c>/run</c>. Defaults to the first user-declared mount or <c>/run</c>.</summary>
  public string? Cwd { get; init; }

  /// <summary>Optional per-call environment overrides layered on top of the agent's configured <see cref="BashConfig.Env"/>. Keys must appear in <see cref="BashConfig.EnvAllow"/>.</summary>
  public IReadOnlyDictionary<string, string>? Env { get; init; }

  /// <summary>Optional per-call timeout override. Clamped to <c>[1, 300]</c> and to the agent's configured <see cref="BashConfig.TimeoutSec"/>.</summary>
  public int? TimeoutSec { get; init; }
}

/// <summary>
/// One file-system change attributed to a bash exec. Wire shape — the internal
/// <see cref="DiffEntry"/> record is mapped onto this for serialisation.
/// </summary>
public sealed class BashDiffEntry
{
  public required string Path { get; init; }
  public required string Kind { get; init; }
  public string? HashBefore { get; init; }
  public string? HashAfter { get; init; }
  public required long SizeDelta { get; init; }
}

/// <summary>
/// Tool output shape for the <c>bash</c> built-in. Matches
/// <c>docs/plans/bash-tool.md</c> §Tool contract.
/// </summary>
public sealed class BashOutput
{
  public required string Stdout { get; init; }
  public required string Stderr { get; init; }
  public required int ExitCode { get; init; }
  public required bool Truncated { get; init; }
  public required long DurationMs { get; init; }

  /// <summary>Null when the agent has no write roots; empty list when execution made no changes; non-empty when the exec touched mount-rooted files.</summary>
  public IReadOnlyList<BashDiffEntry>? Diffs { get; init; }

  /// <summary><c>true</c> when at least one write root's pre- or post-scan hit a traversal cap and the <see cref="Diffs"/> list is partial.</summary>
  public bool DiffsPartial { get; init; }
}

/// <summary>
/// The opt-in <c>bash</c> built-in. Runs a single command via <c>docker exec</c>
/// inside the per-run container managed by <see cref="IBashContainerLifecycle"/>,
/// then diffs the mount-rooted write set so host-visible changes appear in the
/// <see cref="AgentWriteLedger"/> alongside <c>apply_patch</c> / <c>write_repo_file</c>
/// writes. Plan: <c>docs/plans/bash-tool.md</c>.
/// </summary>
/// <remarks>
/// The tool never raises on a non-zero exit code — agents inspect
/// <see cref="BashOutput.ExitCode"/>. It does raise on wall-clock timeout and
/// hard-kill (stream flood), which are environment-level failures the agent
/// cannot recover from within the same call.
/// </remarks>
public sealed class BashTool : ToolBase<BashInput, BashOutput>
{
  private const int HardMinTimeoutSec = 1;
  private const int HardMaxTimeoutSec = 300;
  private const int StdoutCapBytes = StreamCap.DefaultStdoutCapBytes;
  private const int StderrCapBytes = StreamCap.DefaultStderrCapBytes;
  private const long HardKillBytes = StreamCap.DefaultHardKillBytes;

  private readonly IBashContainerLifecycle _lifecycle;
  private readonly IDockerCli _docker;

  /// <summary>
  /// Per-container "mount banner already emitted" flag. Keyed by
  /// <see cref="BashContainerHandle.ContainerId"/> (unique per run) so the banner
  /// surfaces once on the first successful exec and is suppressed thereafter.
  /// ConcurrentDictionary because <see cref="BashTool"/> is registered as a
  /// singleton and may serve parallel fan-out calls across stages.
  /// </summary>
  private readonly ConcurrentDictionary<string, byte> _mountBannerShown = new(StringComparer.Ordinal);

  /// <param name="lifecycle">Owns the per-run container handle.</param>
  /// <param name="docker">Docker CLI adapter used for the <c>docker exec</c> call.</param>
  public BashTool(IBashContainerLifecycle lifecycle, IDockerCli docker)
  {
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(docker);
    _lifecycle = lifecycle;
    _docker = docker;
  }

  /// <inheritdoc />
  public override string Name => "bash";

  /// <inheritdoc />
  public override string Description =>
    "Run a shell command inside a sandboxed per-run container. " +
    "The only file-IO surface in this build — all reads, writes, and shell operations happen here. " +
    "File writes made via bash are captured by a post-exec diff scan and recorded in the agent write ledger; " +
    "if `diff_verification` is enabled, `submit_final.files_modified` must match the captured writes. " +
    "Opt-in; requires a per-agent `bash:` config block with a digest-pinned image and explicit `mounts:`.";

  /// <inheritdoc />
  protected override async Task<BashOutput> ExecuteCoreAsync(
    BashInput input,
    ToolContext ctx,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(ctx);

    if (string.IsNullOrWhiteSpace(input.Command))
    {
      throw new ValidationException("`command` must be a non-empty string.");
    }

    var handle = _lifecycle.TryGetForRun(ctx.RunId)
      ?? throw new ConfigException(
        $"No bash container has been started for run `{ctx.RunId}`. This means the agent lists `bash` in its tools but no `bash:` block was declared, or the agent runner failed to invoke IBashContainerLifecycle.StartForRunAsync. Fix the agent YAML or re-check the wiring.");

    var cfg = handle.Config;
    var effectiveTimeout = ResolveTimeout(input.TimeoutSec, cfg);
    var effectiveEnv = ResolveEnv(input.Env, cfg);
    var workdir = ResolveWorkdir(input.Cwd, handle);

    var diff = cfg.Diff ?? new BashDiffConfig();
    var writeMounts = WriteMountsFor(handle);
    var preScan = PreScanWriteRoots(writeMounts, diff, ctx, out var preScanPartial);

    var commandHash = ComputeCommandHash(input.Command);
    ctx.Trace.Trace(new BashExecStartEvent
    {
      RunId = ctx.RunId,
      CommandHash = commandHash,
      Cwd = workdir,
      TimeoutSec = effectiveTimeout
    });

    var spec = new DockerExecSpec(
      ContainerName: handle.ContainerName,
      WorkDirContainerPath: workdir,
      Command: input.Command,
      EffectiveEnv: effectiveEnv,
      TimeoutSec: effectiveTimeout,
      StdoutCapBytes: StdoutCapBytes,
      StderrCapBytes: StderrCapBytes,
      HardKillBytes: HardKillBytes);

    DockerExecResult result;
    try
    {
      result = await _docker.ExecAsync(spec, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      ctx.Trace.Trace(new BashExecErrorEvent
      {
        RunId = ctx.RunId,
        Reason = $"docker exec raised: {ex.GetType().Name}: {ex.Message}"
      });
      throw;
    }

    if (result.TimedOut)
    {
      ctx.Trace.Trace(new BashExecErrorEvent
      {
        RunId = ctx.RunId,
        Reason = $"wall-clock timeout after {effectiveTimeout}s",
        StderrTail = TailForTrace(result.Stderr)
      });
      throw new ValidationException(
        $"bash: wall-clock timeout after {effectiveTimeout}s (command hash `{commandHash}`). The container remains up for the next call.");
    }

    if (result.HardKilled)
    {
      ctx.Trace.Trace(new BashExecErrorEvent
      {
        RunId = ctx.RunId,
        Reason = "stream hard-kill (output exceeded 64 MiB)",
        StderrTail = TailForTrace(result.Stderr)
      });
      throw new ValidationException(
        "bash: output stream exceeded the 64 MiB hard cap and the exec was killed. Redirect bulk output to a file under a write mount instead.");
    }

    var (diffs, postScanPartial) = PostScanAndDiff(preScan, diff, ctx);
    var diffsPartial = preScanPartial || postScanPartial;

    RecordLedger(diffs, writeMounts, ctx);

    ctx.Trace.Trace(new BashExecEndEvent
    {
      RunId = ctx.RunId,
      ExitCode = result.ExitCode,
      DurationMs = result.DurationMs,
      Truncated = result.Truncated,
      DiffCount = diffs?.Count ?? 0
    });

    var stdout = MaybePrependMountBanner(result.Stdout, handle);

    return new BashOutput
    {
      Stdout = stdout,
      Stderr = result.Stderr,
      ExitCode = result.ExitCode,
      Truncated = result.Truncated,
      DurationMs = result.DurationMs,
      Diffs = diffs,
      DiffsPartial = diffsPartial
    };
  }

  /// <summary>
  /// On the first successful exec per container, prepend a condensed mount-table
  /// banner to stdout so the agent can map <c>/work/read/&lt;i&gt;</c> and
  /// <c>/work/write/&lt;i&gt;</c> to host paths without issuing discovery
  /// <c>ls</c> calls. Honours <see cref="BashConfig.ShowMountTable"/> — when
  /// false, returns stdout unchanged. The "already shown" state is keyed by
  /// <see cref="BashContainerHandle.ContainerId"/> so re-use of a container name
  /// across separate runs still gets its own banner.
  /// </summary>
  private string MaybePrependMountBanner(string stdout, BashContainerHandle handle)
  {
    if (!handle.Config.ShowMountTable)
    {
      return stdout;
    }

    if (!_mountBannerShown.TryAdd(handle.ContainerId, 0))
    {
      return stdout;
    }

    var banner = BuildMountBanner(handle.Mounts);
    // Delimit with a newline so the banner is clearly separated from any
    // command stdout below it, including the empty-stdout case.
    return string.IsNullOrEmpty(stdout)
      ? banner
      : banner + "\n" + stdout;
  }

  internal static string BuildMountBanner(BashMountPlan plan)
  {
    ArgumentNullException.ThrowIfNull(plan);
    var sb = new StringBuilder();
    sb.AppendLine("=== Bash container ready (first call) ===");
    sb.AppendLine("Mounts (host paths reachable at the container paths below):");
    foreach (var m in plan.Mounts)
    {
      var ro = m.ReadOnly ? "ro" : "rw";
      sb.Append("  ").Append(m.ContainerPath.PadRight(16)).Append('(').Append(ro).Append(")  ").AppendLine(m.HostPath);
    }
    sb.Append("Default cwd: ").AppendLine(plan.DefaultCwd?.ContainerPath ?? "(none — pass `cwd` on every call)");
    sb.AppendLine("HOME, TMPDIR, NUGET_PACKAGES, and MSBuild obj/bin are pre-wired to");
    sb.AppendLine("host-backed scratch dirs — run `cd /repo && dotnet build` as a dev would.");
    sb.AppendLine("=== end banner ===");
    return sb.ToString();
  }

  private static int ResolveTimeout(int? requested, BashConfig cfg)
  {
    var effective = requested ?? cfg.TimeoutSec;
    if (effective < HardMinTimeoutSec)
    {
      effective = HardMinTimeoutSec;
    }

    var ceiling = Math.Min(HardMaxTimeoutSec, cfg.TimeoutSec);
    if (effective > ceiling)
    {
      effective = ceiling;
    }

    return effective;
  }

  private static IReadOnlyDictionary<string, string> ResolveEnv(
    IReadOnlyDictionary<string, string>? requested,
    BashConfig cfg)
  {
    // Start from the agent's configured env (already validated against
    // env_allow at parse time), then layer the per-call overrides. Every
    // per-call key is re-validated here because the parser only sees the
    // static config; tool-call inputs come from the model at run time.
    var merged = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (k, v) in cfg.Env)
    {
      merged[k] = v;
    }

    if (requested is null)
    {
      return merged;
    }

    var allow = new HashSet<string>(cfg.EnvAllow, StringComparer.Ordinal);
    foreach (var (k, v) in requested)
    {
      if (BashConfig.ForbiddenEnvPattern.IsMatch(k))
      {
        throw new ValidationException(
          $"bash: env key `{k}` is forbidden (matches {BashConfig.ForbiddenEnvPattern}) and cannot be set by a tool call.");
      }

      if (!allow.Contains(k))
      {
        throw new ValidationException(
          $"bash: env key `{k}` is not in the agent's `env_allow` list. Add it to the agent YAML or remove the override.");
      }

      merged[k] = v;
    }

    return merged;
  }

  private static string ResolveWorkdir(string? requested, BashContainerHandle handle)
  {
    if (requested is not null)
    {
      // ResolveContainerCwd validates the path against the mount namespace,
      // rejects `..`-traversal, and returns the matched mount entry. The
      // tool never writes to a read-only mount by default, so requireWritable
      // is `false` — read-only cwd is fine (e.g. running tests under a read mount).
      MountComposer.ResolveContainerCwd(handle.Mounts, requested, requireWritable: false);
      return requested;
    }

    var def = handle.Mounts.DefaultCwd
      ?? throw new ValidationException(
        "bash: no writable mount available and no `cwd` provided. Declare at least one `filesystem.write` root or pass an explicit `cwd` under a read mount.");
    return def.ContainerPath;
  }

  private static string ComputeCommandHash(string command)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(command));
    return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
  }

  private static string? TailForTrace(string? stderr)
  {
    if (string.IsNullOrEmpty(stderr))
    {
      return null;
    }

    const int maxTail = 1024;
    return stderr.Length <= maxTail ? stderr : stderr[^maxTail..];
  }

  private static IReadOnlyList<BashMountEntry> WriteMountsFor(BashContainerHandle handle) =>
    handle.Mounts.Mounts.Where(m => !m.ReadOnly).ToList();

  private static IReadOnlyList<(string HostRoot, FsSnapshot Snapshot)> PreScanWriteRoots(
    IReadOnlyList<BashMountEntry> writeMounts,
    BashDiffConfig diff,
    ToolContext ctx,
    out bool wasPartial)
  {
    wasPartial = false;
    var snapshots = new List<(string HostRoot, FsSnapshot Snapshot)>(writeMounts.Count);
    foreach (var m in writeMounts)
    {
      var snap = MtimeHashScanner.Scan(m.HostPath, diff.MaxFiles, diff.MaxDepth, diff.MaxHashBytes);
      snapshots.Add((m.HostPath, snap));
      if (snap.WasTruncated)
      {
        wasPartial = true;
        ctx.Trace.Trace(new BashDiffTruncatedEvent
        {
          RunId = ctx.RunId,
          Reason = snap.TruncationReason ?? "unknown",
          Root = m.HostPath,
          FilesScanned = snap.FilesScanned,
          MaxDepthReached = snap.MaxDepthReached
        });
      }
    }

    return snapshots;
  }

  private static (IReadOnlyList<BashDiffEntry>? Diffs, bool WasPartial) PostScanAndDiff(
    IReadOnlyList<(string HostRoot, FsSnapshot Snapshot)> preScan,
    BashDiffConfig diff,
    ToolContext ctx)
  {
    if (preScan.Count == 0)
    {
      // No write roots → no diffs contract (null, per plan's "Absent when no write roots are declared").
      return (null, false);
    }

    var aggregated = new List<BashDiffEntry>();
    var partial = false;
    foreach (var (hostRoot, pre) in preScan)
    {
      var post = MtimeHashScanner.Scan(hostRoot, diff.MaxFiles, diff.MaxDepth, diff.MaxHashBytes);
      if (post.WasTruncated)
      {
        partial = true;
        ctx.Trace.Trace(new BashDiffTruncatedEvent
        {
          RunId = ctx.RunId,
          Reason = post.TruncationReason ?? "unknown",
          Root = hostRoot,
          FilesScanned = post.FilesScanned,
          MaxDepthReached = post.MaxDepthReached
        });
      }

      foreach (var entry in MtimeHashScanner.Diff(pre, post))
      {
        aggregated.Add(new BashDiffEntry
        {
          Path = entry.Path,
          Kind = entry.Kind,
          HashBefore = entry.HashBefore,
          HashAfter = entry.HashAfter,
          SizeDelta = entry.SizeDelta
        });
      }
    }

    return (aggregated, partial);
  }

  private void RecordLedger(
    IReadOnlyList<BashDiffEntry>? diffs,
    IReadOnlyList<BashMountEntry> writeMounts,
    ToolContext ctx)
  {
    if (diffs is null || diffs.Count == 0 || ctx.WriteLedger is null)
    {
      return;
    }

    foreach (var d in diffs)
    {
      var (resolved, bytes) = ResolveAgainstWriteMounts(d.Path, writeMounts, d.Kind);
      if (resolved is null)
      {
        continue;
      }

      ctx.WriteLedger.Record(new AgentWriteRecord(
        ToolName: Name,
        RequestedPath: d.Path,
        ResolvedPath: resolved,
        RootCategory: "user-mount",
        BytesWritten: bytes,
        WasNoOp: false));
    }
  }

  private static (string? Resolved, long Bytes) ResolveAgainstWriteMounts(
    string relPath,
    IReadOnlyList<BashMountEntry> writeMounts,
    string kind)
  {
    foreach (var mount in writeMounts)
    {
      var candidate = Path.GetFullPath(Path.Combine(mount.HostPath, relPath.Replace('/', Path.DirectorySeparatorChar)));
      try
      {
        var info = new FileInfo(candidate);
        if (info.Exists)
        {
          return (candidate, info.Length);
        }
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }

      if (string.Equals(kind, "removed", StringComparison.Ordinal))
      {
        return (candidate, 0);
      }
    }

    return (null, 0);
  }
}
