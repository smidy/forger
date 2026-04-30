using System.Text.RegularExpressions;

namespace Forge.Core.Config;

/// <summary>Read/write access mode for a <see cref="BashMount"/> entry.</summary>
public enum BashMountMode
{
  /// <summary>Mount the host path read-only inside the container.</summary>
  ReadOnly,

  /// <summary>Mount the host path read-write inside the container.</summary>
  ReadWrite
}

/// <summary>One explicit host-to-container bind-mount declared under <c>bash.mounts:</c>.</summary>
/// <param name="Host">Host path to bind-mount. Must exist as a directory at run time.</param>
/// <param name="Container">Absolute container path (must start with <c>/</c>; <c>/run</c> is reserved).</param>
/// <param name="Mode">Read-only or read-write access for the bind mount.</param>
public sealed record BashMount(string Host, string Container, BashMountMode Mode);

/// <summary>
/// Bounded-scan parameters for the bash tool's mtime+hash diff verifier. Caps
/// the post-exec walk of every write root so an agent command that touches a
/// million files does not stall the loop. Plan: <c>docs/plans/bash-tool.md</c>.
/// </summary>
/// <remarks>
/// Lives in <c>Forge.Core</c> (not <c>Forge.Agent</c>) so the Docker-side helpers
/// in <c>Forge.Tools.Docker</c> can read the fields without reversing the
/// project layering. Parsers still live in <c>Forge.Agent.AgentConfig</c>.
/// </remarks>
public sealed class BashDiffConfig
{
  /// <summary>Maximum files visited per write root before truncation. Defaults to 10_000.</summary>
  public int MaxFiles { get; init; } = 10_000;

  /// <summary>Maximum directory depth walked per write root. Defaults to 16.</summary>
  public int MaxDepth { get; init; } = 16;

  /// <summary>Maximum per-file byte count hashed. Files larger than this are flagged but hashed up to the cap. Defaults to 4 MiB.</summary>
  public int MaxHashBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Daemon-rootless preference for the bash tool's container. Plan:
/// <c>docs/plans/bash-tool-rootless-docker.md</c>.
/// </summary>
public enum BashRootlessMode
{
  /// <summary>Pick rootless when the daemon reports it; rootful otherwise. Default.</summary>
  Auto,

  /// <summary>Refuse to start the container unless the active daemon is rootless.</summary>
  Required,

  /// <summary>Refuse to start the container if the active daemon is rootless.</summary>
  Forbidden
}

/// <summary>
/// Per-agent configuration for the opt-in <c>bash</c> tool. Presence of this
/// block is required whenever an agent's tool list includes <c>bash</c>;
/// parse-time validation rejects a missing block, forbidden fields, root user,
/// non-digest image refs, and env keys matching
/// <see cref="BashConfig.ForbiddenEnvPattern"/>. See
/// <c>docs/plans/bash-tool.md</c> for the threat model and defense-in-depth
/// rationale.
/// </summary>
public sealed record class BashConfig
{
  /// <summary>Container image pinned by digest (must contain <c>@sha256:</c>).</summary>
  public required string Image { get; init; }

  /// <summary>Docker platform. Defaults to <c>linux/amd64</c>.</summary>
  public string Platform { get; init; } = "linux/amd64";

  /// <summary>One of <c>none</c> (default) or <c>bridge</c>.</summary>
  public string Network { get; init; } = "none";

  /// <summary>Per-call wall-clock timeout, clamped to [1,300]. Defaults to 30.</summary>
  public int TimeoutSec { get; init; } = 30;

  /// <summary>Docker <c>--memory</c> argument. Defaults to <c>512m</c>.</summary>
  public string Memory { get; init; } = "512m";

  /// <summary>Docker <c>--cpus</c> argument. Defaults to <c>1.0</c>.</summary>
  public double Cpus { get; init; } = 1.0;

  /// <summary>Docker <c>--pids-limit</c> argument. Defaults to 100.</summary>
  public int PidsLimit { get; init; } = 100;

  /// <summary>
  /// Docker <c>--storage-opt</c> argument. Default empty (flag omitted); the
  /// option is only supported on overlay-over-xfs-with-pquota drivers, which
  /// excludes every Docker Desktop installation. Operators on a compatible
  /// driver can re-enable explicitly.
  /// </summary>
  public string StorageOpt { get; init; } = "";

  /// <summary>
  /// <c>/tmp</c> tmpfs size, passed as <c>--tmpfs /tmp:size=&lt;value&gt;</c>.
  /// Default <c>512m</c> — below that, a single <c>dotnet restore</c> or
  /// <c>npm install</c> blows past the cap with <c>No space left on device</c>.
  /// </summary>
  public string TmpfsSize { get; init; } = "512m";

  /// <summary>Non-root user passed as --user. Defaults to <c>1000:1000</c>; UID 0 rejected.</summary>
  public string User { get; init; } = "1000:1000";

  /// <summary>
  /// Pass <c>--read-only</c> to make the container rootfs read-only. Default
  /// <c>false</c>: the real defense is <c>--cap-drop=ALL</c> +
  /// <c>--security-opt=no-new-privileges</c> + non-root; read-only rootfs
  /// only blocks legitimate scratch writes (apt caches, dotnet SDK profile
  /// dirs, pip's user-site lookups) that build tools perform routinely.
  /// </summary>
  public bool ReadOnlyRoot { get; init; } = false;

  /// <summary>Allowlist of environment variable names the agent may pass via <see cref="Env"/>.</summary>
  public IReadOnlyList<string> EnvAllow { get; init; } = Array.Empty<string>();

  /// <summary>Environment variables to inject. Every key must appear in <see cref="EnvAllow"/>.</summary>
  public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

  /// <summary>Diff-scan bounds. Null means defaults.</summary>
  public BashDiffConfig? Diff { get; init; }

  /// <summary>Explicit bind-mounts declared via <c>bash.mounts:</c>. Empty list means only the run workspace is mounted.</summary>
  public IReadOnlyList<BashMount> Mounts { get; init; } = Array.Empty<BashMount>();

  /// <summary>
  /// When true (default), the bash tool prepends a one-shot mount-table banner to
  /// the stdout of the first <c>bash</c> tool call per container. Subsequent calls
  /// return stdout verbatim. Designed so the agent discovers the
  /// <c>/work/read/&lt;i&gt;</c> / <c>/work/write/&lt;i&gt;</c> → host-path mapping
  /// without burning iterations on <c>ls /work/read</c> probes. Operators can
  /// disable via <c>bash.show_mount_table: false</c> when the agent's
  /// system_prompt already pins the layout.
  /// </summary>
  public bool ShowMountTable { get; init; } = true;

  /// <summary>
  /// When true (default), Forge auto-provisions a host-backed scratch directory
  /// and injects <c>HOME</c> / <c>TMPDIR</c> / <c>NUGET_PACKAGES</c> pointing at
  /// subdirs under it. Gives package caches and build-tool temp files host-backed
  /// disk and persists them across <c>docker exec</c> calls. Does NOT redirect
  /// MSBuild obj/bin — a flat env-var path collapses every project's
  /// <c>project.assets.json</c> into one file (last-writer-wins) and breaks
  /// multi-project restores; agents instead reconcile host vs container obj/
  /// via <c>dotnet clean</c> before <c>dotnet build</c>. Disable only if the
  /// image ships its own scratch wiring.
  /// </summary>
  public bool AutoScratch { get; init; } = true;

  /// <summary>
  /// When true, the bash-tool mounts <c>.git/</c> writable inside the
  /// container. Defaults to <c>false</c>: the nearest-common-ancestor mount
  /// projects the repo at <c>/repo</c> with rw access, but MountComposer
  /// stacks a <c>tmpfs</c> over <c>/repo/.git</c> so a stray
  /// <c>git reset --hard</c> cannot mutate host git state. Agents that need
  /// to run git commands from bash (uncommon; `glob`/`grep` cover discovery)
  /// flip this to true at their own risk.
  /// </summary>
  public bool ExposeGit { get; init; } = false;

  /// <summary>
  /// Daemon-rootless preference. <see cref="BashRootlessMode.Auto"/> (default)
  /// picks rootless when the active Docker daemon reports the
  /// <c>rootless</c> security option, rootful otherwise.
  /// <see cref="BashRootlessMode.Required"/> refuses to start the container
  /// unless rootless; <see cref="BashRootlessMode.Forbidden"/> refuses to
  /// start when only a rootless daemon is reachable. Linux-only — on
  /// Windows/macOS the active daemon's <c>OSType</c> is <c>linux</c> inside
  /// Docker Desktop's VM, but the host kernel boundary already provides
  /// equivalent isolation, so the knob is a no-op (warned by
  /// <c>forge doctor</c>). Plan:
  /// <c>docs/plans/bash-tool-rootless-docker.md</c>.
  /// </summary>
  public BashRootlessMode Rootless { get; init; } = BashRootlessMode.Auto;

  /// <summary>Regex matching env keys that must never be set by an agent regardless of the allowlist.</summary>
  public static readonly Regex ForbiddenEnvPattern =
    new("^(PATH|LD_.*|DYLD_.*|NODE_OPTIONS|PYTHONPATH)$", RegexOptions.Compiled);

  /// <summary>Config keys that are forbidden outright — accepting them would weaken the sandbox.</summary>
  public static readonly IReadOnlyList<string> ForbiddenKeys = new[]
  {
    "cap_add", "privileged", "pid_host", "ipc_host", "userns_host", "devices", "extra_mounts"
  };
}
