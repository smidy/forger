# Reference image for the Forge `bash` tool

The Forge `bash` tool is opt-in and requires a container image **pinned by digest**
(not by tag). This directory provides a minimal reference image that agents can
build locally and pin. The image and its build recipe are intentionally plain —
`bash` + GNU coreutils + a non-root user — so it is auditable end-to-end.

> Forge never runs `docker pull` implicitly. A missing image is surfaced by
> `forge doctor` and by lifecycle startup as a clean `ValidationException`.

## Build + pin + reference

From the repo root:

```bash
docker build -t forge-bash:local examples/bash
docker image inspect forge-bash:local --format '{{.Id}}'
# → sha256:abc123…
```

Then pin the digest in any agent YAML that declares a `bash:` block:

```yaml
tools: [bash]

bash:
  image: sha256:abc123...   # use the digest just printed
  timeout_sec: 30
  user: 1000:1000
  # Optional `mounts:` list — the per-run workspace is always mounted at /run.
  # mounts:
  #   - { host: /Users/me/repo, container: /repo, mode: rw }
```

Run the bash-demo agent to validate end-to-end:

```bash
forge agent bash-demo --input '{"name":"world"}'
```

## Why digest-pinning is mandatory

Tags are mutable — a later `docker pull` can replace `forge-bash:local` with
a different image that still answers to the same tag. A content-addressed pin
is immutable, so the security posture of a committed agent YAML cannot drift
underneath the agent author.

Forge accepts two pin forms:

- **`sha256:<image-id>`** — bare image-ID, the direct output of
  `docker image inspect --format '{{.Id}}'`. Use this for locally-built images
  that have not been pushed and therefore have no `RepoDigests` entry.
- **`<repo>@sha256:<digest>`** — proper RepoDigest pin for images that have
  been pushed to a registry.

The Forge `bash` YAML parser rejects any `image:` value that does not match
one of these two forms.

## Docker Desktop / macOS / Windows

As of 2026-04-24 (`bash-tool-friction-v2`), Forge defaults `bash.storage_opt`
to `""` (flag omitted). The option is only supported when the Docker daemon's
active storage driver is `overlay2` on `xfs` with `pquota` enabled — a
production-Linux-only shape that no Docker Desktop install provides. Keeping
it in the default set traded one layer of defense-in-depth for a universal
retry-after-warning round-trip, so v2 drops it and callers who have a
compatible driver re-enable it explicitly.

If you set `bash.storage_opt: size=1G` explicitly on an incompatible driver,
`docker run` exits with:

> `--storage-opt is supported only for overlay over xfs with 'pquota' mount option`

Forge detects this, emits a `bash_storage_opt_skipped` trace event, and
retries the `docker run` with the flag stripped. The container still runs with
`--memory` + `--pids-limit` + tmpfs caps.

## Non-root requirement

The plan mandates a non-root container user (`bash.user` defaults to `1000:1000`).
This Dockerfile creates `forge:forge` with UID/GID `1000:1000`, which matches the
default and covers the common case. If the agent YAML overrides `bash.user`, the
image must contain a user with that UID — otherwise `docker run` will start the
container with a numeric UID that has no `/etc/passwd` entry, and tools expecting
`$HOME` will misbehave.

**Running `USER root` in the image is not enough for the tool to work** — Forge
still refuses UID 0 at parse time to defend against `--user root` bypass attempts
that set the UID via an env var at the CLI.

## Running against rootless Docker

On Linux, Forge can route the bash container through a [rootless Docker daemon](https://docs.docker.com/engine/security/rootless/) so a container-escape (the runc CVE class) lands in an unprivileged host shell instead of `root`. Rootless is **additive** to the existing `--cap-drop=ALL` + `no-new-privileges` + `--user 1000:1000` posture — every flag still emits.

Install rootless via the upstream script (Forge does not bundle install logic):

```bash
curl -fsSL https://get.docker.com/rootless | sh
systemctl --user enable --now docker
export DOCKER_HOST=unix:///run/user/$(id -u)/docker.sock
```

Then declare the preference in any agent's `bash:` block:

```yaml
bash:
  image: sha256:abc123...
  rootless: required   # auto (default) | required | forbidden
```

`required` refuses to start the container if the active daemon is rootful — `forge doctor` warns ahead of time when an agent declares `bash.rootless: required` against a rootful daemon. `auto` picks rootless when the daemon reports it and rootful otherwise; `forbidden` refuses when only a rootless daemon is reachable. Win/macOS: the knob is a no-op (Docker Desktop's VM boundary supersedes); `forge doctor` reports `skip` for `bash.docker.rootless` on those hosts.

**cgroup-v2 prerequisite.** Rootless on cgroup-v1 silently ignores `--memory`/`--cpus`/`--pids-limit`. `forge doctor` reports `bash.docker.cgroupv2` as `warn` when rootless is active but the controllers are not delegated; the upstream "[Limiting resources](https://docs.docker.com/engine/security/rootless/#limiting-resources)" section covers systemd `delegate=memory cpu pids io` setup.

## Extending the image

Most agent workloads will need an interpreter (`python3`, `node`, etc.). Do not
depend on this directory's `Dockerfile` — create your own and pin its digest:

```dockerfile
FROM sha256:abc123...
USER root
RUN apt-get update && apt-get install --no-install-recommends -y python3 python3-pip && \
    rm -rf /var/lib/apt/lists/*
USER forge:forge
```

Then `docker build` and pin the new digest in the agent YAML. Forge's doctor
will still check the base image on PATH; the specific digest mismatch is only
surfaced when the agent actually runs.
