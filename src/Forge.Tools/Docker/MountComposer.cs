using System.Runtime.InteropServices;
using Forge.Core.Config;
using Forge.Core.Exceptions;

namespace Forge.Tools.Docker;

/// <summary>One mount entry of the composed container namespace.</summary>
/// <param name="HostPath">Canonical host path after symlink resolution + Windows translation.</param>
/// <param name="ContainerPath">Destination inside the container.</param>
/// <param name="ReadOnly"><c>true</c> for <c>ro</c> mounts, <c>false</c> for <c>rw</c>.</param>
public sealed record BashMountEntry(string HostPath, string ContainerPath, bool ReadOnly);

/// <summary>
/// Output of <see cref="MountComposer.Compose"/>. Carries both the mount list
/// (used by the tool to validate <c>cwd</c>) and the flattened <c>-v</c> argv
/// (used by <see cref="DockerCli"/> at container-run time).
/// </summary>
public sealed record BashMountPlan(
  IReadOnlyList<BashMountEntry> Mounts,
  IReadOnlyList<string> DockerArgs,
  BashMountEntry? DefaultCwd);

/// <summary>
/// Translates an explicit <see cref="BashMount"/> list into a mount namespace
/// for the bash tool. The per-run workspace is always added at <c>/run</c>
/// (rw). Each user-declared mount is validated (symlink resolution, Windows
/// long-path/UNC rejection) and emitted as a <c>docker run -v</c> argument.
/// No NCA heuristic, no deny list, no <c>/repo</c> or <c>/inputs/&lt;i&gt;</c>
/// layout — the agent author owns the security choice.
/// </summary>
public static class MountComposer
{
  private const int MaxSymlinkHops = 8;
  private const int MaxWindowsPathLength = 260;

  /// <summary>Container path at which the per-run workspace is mounted.</summary>
  public const string RunContainerPath = "/run";

  /// <summary>
  /// Compose the mount plan from the agent-declared mount list plus the
  /// implicit run-workspace mount at <c>/run</c>. Host symlinks are resolved
  /// to their physical path (8-hop loop refusal). Windows long paths (&gt; 260
  /// chars) and UNC / <c>\\?\</c> paths are rejected.
  /// </summary>
  /// <param name="userMounts">Explicit mounts from <c>bash.mounts:</c> in the agent YAML. May be empty.</param>
  /// <param name="runWorkspaceDir">Host path of the per-run workspace. Mounted at <c>/run</c>.</param>
  /// <param name="config">Bash config; unused in compose logic, accepted for call-site symmetry.</param>
  public static BashMountPlan Compose(
    IReadOnlyList<BashMount> userMounts,
    string runWorkspaceDir,
    BashConfig? config = null)
  {
    ArgumentNullException.ThrowIfNull(userMounts);
    ArgumentException.ThrowIfNullOrWhiteSpace(runWorkspaceDir);

    var runHost = PrepareHostPath(runWorkspaceDir);

    var mounts = new List<BashMountEntry>(userMounts.Count + 1);
    foreach (var m in userMounts)
    {
      var host = PrepareHostPath(m.Host);
      mounts.Add(new BashMountEntry(host, m.Container, ReadOnly: m.Mode == BashMountMode.ReadOnly));
    }

    mounts.Add(new BashMountEntry(runHost, RunContainerPath, ReadOnly: false));

    EnsureNoOverlappingContainerPaths(mounts);

    var args = new List<string>(mounts.Count * 2);
    foreach (var m in mounts)
    {
      args.Add("-v");
      args.Add(m.ReadOnly
        ? $"{m.HostPath}:{m.ContainerPath}:ro"
        : $"{m.HostPath}:{m.ContainerPath}");
    }

    var defaultCwd = userMounts.Count > 0 ? mounts[0] : mounts[^1];
    return new BashMountPlan(mounts, args, DefaultCwd: defaultCwd);
  }

  /// <summary>
  /// Validate a container cwd path (from the tool input) against the composed
  /// mounts. Returns the mount entry whose <see cref="BashMountEntry.ContainerPath"/>
  /// is a prefix of <paramref name="containerCwd"/>. Throws if no mount claims
  /// the path, if the relative suffix contains a <c>..</c> segment, or if the
  /// matched mount is read-only when <paramref name="requireWritable"/> is
  /// <c>true</c>.
  /// </summary>
  public static BashMountEntry ResolveContainerCwd(
    BashMountPlan plan,
    string containerCwd,
    bool requireWritable)
  {
    ArgumentNullException.ThrowIfNull(plan);
    if (string.IsNullOrWhiteSpace(containerCwd))
    {
      throw new ValidationException("`cwd` must be a non-empty container path.");
    }

    if (!containerCwd.StartsWith('/'))
    {
      throw new ValidationException($"`cwd` must be an absolute container path starting with `/`. Got: `{containerCwd}`.");
    }

    BashMountEntry? best = null;
    foreach (var m in plan.Mounts)
    {
      if (!containerCwd.Equals(m.ContainerPath, StringComparison.Ordinal)
          && !containerCwd.StartsWith(m.ContainerPath + "/", StringComparison.Ordinal))
      {
        continue;
      }

      if (best is null || m.ContainerPath.Length > best.ContainerPath.Length)
      {
        best = m;
      }
    }

    if (best is null)
    {
      throw new ValidationException(
        $"`cwd` `{containerCwd}` does not map to any mount. Use `/run` or one of the configured `bash.mounts` container paths.");
    }

    var suffix = containerCwd.Length == best.ContainerPath.Length
      ? string.Empty
      : containerCwd.Substring(best.ContainerPath.Length + 1);

    foreach (var seg in suffix.Split('/'))
    {
      if (seg == "..")
      {
        throw new ValidationException($"`cwd` contains a parent-traversal segment: `{containerCwd}`.");
      }
    }

    if (requireWritable && best.ReadOnly)
    {
      throw new ValidationException($"`cwd` resolves under a read-only mount `{best.ContainerPath}`; writes are not allowed here.");
    }

    return best;
  }

  private static void EnsureNoOverlappingContainerPaths(IReadOnlyList<BashMountEntry> mounts)
  {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var m in mounts)
    {
      if (!seen.Add(m.ContainerPath))
      {
        throw new ValidationException(
          $"Duplicate container mount path `{m.ContainerPath}`. Each mount must map to a unique container path.");
      }
    }
  }

  private static string PrepareHostPath(string canonicalPath)
  {
    ValidateWindowsPath(canonicalPath);
    var resolved = ResolveSymlinks(canonicalPath);
    ValidateWindowsPath(resolved);
    return resolved;
  }

  private static string ResolveSymlinks(string input)
  {
    var current = Path.GetFullPath(input);
    var hops = 0;
    while (hops < MaxSymlinkHops && Path.Exists(current) && IsReparsePoint(current))
    {
      var target = File.ResolveLinkTarget(current, returnFinalTarget: false);
      if (target is null)
      {
        break;
      }

      current = Path.GetFullPath(target.FullName);
      hops++;
    }

    if (Path.Exists(current) && IsReparsePoint(current))
    {
      throw new ValidationException(
        $"Symlink chain exceeded {MaxSymlinkHops} hops while preparing bash mount for `{input}`.");
    }

    return current;
  }

  private static bool IsReparsePoint(string path)
  {
    try
    {
      var attr = File.GetAttributes(path);
      return (attr & FileAttributes.ReparsePoint) != 0;
    }
    catch
    {
      return false;
    }
  }

  private static void ValidateWindowsPath(string path)
  {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return;
    }

    if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
    {
      throw new ValidationException(
        $"Windows extended-length (`\\\\?\\`) paths are not supported for bash mounts. Got: `{path}`.");
    }

    if (path.StartsWith(@"\\", StringComparison.Ordinal))
    {
      throw new ValidationException(
        $"UNC / network share paths are not supported for bash mounts. Got: `{path}`.");
    }

    if (path.Length > MaxWindowsPathLength)
    {
      throw new ValidationException(
        $"Host path is longer than {MaxWindowsPathLength} chars — Docker Desktop cannot mount it. Got: {path.Length} chars.");
    }
  }
}
