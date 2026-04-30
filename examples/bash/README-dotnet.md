# .NET-SDK bash image for Forge agents

Companion recipe to [`Dockerfile.dotnet`](Dockerfile.dotnet) covering the
config tuning a bash-equipped agent needs when its workload is `dotnet
build` / `dotnet test`. The reference image (plain `bash` + coreutils
in [`Dockerfile`](Dockerfile) + [`README.md`](README.md)) is fine for most
agents; this one is the answer for .NET projects specifically.

## Build the image

From the repo root:

```powershell
docker build -t forge-bash-dotnet:local -f examples/bash/Dockerfile.dotnet examples/bash
docker image inspect forge-bash-dotnet:local --format '{{.Id}}'
# → sha256:abc123…
```

The inspect output is the **image ID** (sha256 of the image config). Pin it
verbatim in the agent YAML — **do not combine it with a tag prefix**:

```yaml
bash:
  image: sha256:abc123...   # bare image-ID form — mandatory for locally-built images
```

Locally-built images have no `RepoDigests` entry until pushed to a registry,
so the `<tag>@sha256:<image-id>` form (which requires a RepoDigest match)
cannot resolve and `docker inspect` rejects it. Forge's YAML parser accepts
the bare form since the F-G fix (commit `ee7e8d7`).

## Canonical `bash:` block for .NET agents

A canonical `bash:` block tuned for .NET workloads. Every non-default
value has a reason tied to the .NET SDK toolchain:

```yaml
bash:
  image: sha256:<paste image-ID here>
  network: bridge
  timeout_sec: 180
  memory: 2g
  cpus: 2.0
  pids_limit: 512
  env_allow: [DOTNET_CLI_HOME, DOTNET_CLI_TELEMETRY_OPTOUT, NUGET_PACKAGES, LANG]
  env:
    DOTNET_CLI_HOME: /tmp/dotnet
    DOTNET_CLI_TELEMETRY_OPTOUT: "1"
    LANG: C.UTF-8
  diff:
    max_files: 50000
    max_depth: 20
```

### Why each non-default

| Field | Value | Rationale |
|---|---|---|
| `network` | `bridge` | NuGet restore on first build needs egress to `api.nuget.org`. Without it, `dotnet restore` fails with `NU1301` / connection refused. The trade-off: any process the agent launches can also reach the network. For CI with an offline NuGet cache, switch back to `none` and pre-warm the cache. |
| `timeout_sec` | `180` | Cold NuGet restore + full-solution `dotnet build` commonly takes 60–120 s on first invocation. The default `30` times out before restore completes. Max allowed is `300`; pick the smallest that survives cold paths. |
| `memory` | `2g` | MSBuild child nodes + Roslyn compiler easily peak past `512m` on a mid-size solution. `2g` gives headroom for `dotnet test` launching test hosts in parallel. |
| `cpus` | `2.0` | `dotnet build` parallelises across projects; one CPU leaves restore-and-compile chains serialised and wall-clock doubles. |
| `pids_limit` | `512` | MSBuild forks + test hosts + target frameworks can spawn 100+ processes on a multi-project solution. Default `100` throttles `dotnet test` mid-run with silent failures. |
| `env_allow` | `[DOTNET_CLI_HOME, DOTNET_CLI_TELEMETRY_OPTOUT, NUGET_PACKAGES, LANG]` | `DOTNET_CLI_HOME` — mandatory (see below). `TELEMETRY_OPTOUT` — keep runs deterministic. `NUGET_PACKAGES` — override the default cache path when using tmpfs. `LANG` — avoids locale warnings on first-run. Everything else (including `PATH`, `HOME`, `USER`) is intentionally blocked. |
| `env.DOTNET_CLI_HOME` | `/tmp/dotnet` | The SDK writes to `$HOME/.dotnet/` on first invocation (first-run sentinel, NuGet config, certificate store). With `read_only_root: true` the container's `$HOME` is not writable, so the CLI aborts with `Access to the path '/home/forge/.dotnet' is denied`. Redirecting to `/tmp/dotnet` puts these scratch files on the writable tmpfs. |
| `diff.max_files` | `50000` | Default `10000` truncates every `dotnet build`: a single project's `obj/` has ~30 intermediate files, and a typical .NET solution has 5–10 projects + test projects. At 10k the diff scanner cuts off mid-traversal and emits `BashDiffTruncatedEvent`. 50k covers a full-solution build plus some growth headroom. |
| `diff.max_depth` | `20` | `bin/Debug/net9.0/...` bottoms out around depth 6; depth 20 is generous but harmless. |

## Invocation pattern inside the agent

Declare an explicit `mounts:` block to expose the host repo to the
container. The per-run workspace is always at `/run`; everything else is
opt-in:

```yaml
bash:
  image: sha256:<paste image-ID here>
  mounts:
    - { host: /absolute/path/to/repo, container: /repo, mode: rw }
```

The agent then builds at the configured container path:

```
bash({"command": "cd /repo && dotnet build Forge.sln"})
```

Build artefacts (`obj/`, `bin/`) land on the host at the original `host`
path. To keep the repo clean across runs, point the mount at a per-run
copy or use `dotnet build --output /run/out` so output goes to the
ephemeral run workspace.

## Regenerating the pinned image ID

Any change to `Dockerfile.dotnet` (e.g. a base-image bump when the upstream
SDK ships a new minor) changes the image ID. Forge's bash YAML parser
enforces digest pinning, so the agent YAML must be re-pinned in lock-step:

```powershell
docker build -t forge-bash-dotnet:local -f examples/bash/Dockerfile.dotnet examples/bash
docker image inspect forge-bash-dotnet:local --format '{{.Id}}'
# paste the new sha256:... value into the agent YAML
dotnet run --project src/Forge.Cli -- validate path\to\agent.yaml
```

## Known interaction: `storage_opt`

The `bash.storage_opt` default (`size=1G`) is rejected by Docker Desktop's
default driver. Forge catches the failure, emits a `bash_storage_opt_skipped`
trace, and retries without the flag — the container starts fine. See
[`README.md`](README.md) §"Docker Desktop / macOS / Windows" for the full
explanation. This behaviour is independent of the SDK image.

## Related

- [`Dockerfile.dotnet`](Dockerfile.dotnet) — the image this recipe configures
- [`../../docs/Domain/tools.md`](../../docs/Domain/tools.md) — `bash:` block schema, including `mounts`
