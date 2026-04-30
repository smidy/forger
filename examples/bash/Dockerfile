# Reference image for the Forge `bash` tool.
#
# Agents that declare a `bash:` block must pin this image by digest (see
# ../../docs/Domain/tools.md). Build from this repo root:
#
#   docker build -t forge-bash:local examples/bash
#   docker image inspect forge-bash:local --format '{{.Id}}'
#   # then pin the digest in the agent YAML:
#   #   bash:
#   #     image: forge-bash:local@sha256:<digest>
#
# Forge never runs `docker pull` implicitly — the image must already be
# present locally, otherwise `forge doctor` and the lifecycle start fail
# cleanly with a ValidationException.
#
# Security posture enforced by Forge at container-run time (NOT here):
#   --network=none        (unless the agent opts into `bridge`)
#   --cap-drop=ALL
#   --security-opt=no-new-privileges
#   --user=1000:1000      (overridable in agent YAML — UID 0 is rejected)
#   --read-only rootfs    (with tmpfs /tmp)
#   --pids-limit / --memory / --cpus caps
#
# This Dockerfile only needs to provide a usable bash + coreutils userland
# and a non-root user that the agent's configured `user:` can match.
FROM debian:bookworm-slim

# Pin apt cache off for reproducibility — we do not care about rebuild
# speed in a reference image and this keeps the layer smaller.
RUN apt-get update \
 && apt-get install --no-install-recommends -y \
      bash \
      coreutils \
      ca-certificates \
      findutils \
      grep \
      sed \
      gawk \
      procps \
 && rm -rf /var/lib/apt/lists/*

# Non-root principal matching the default `bash.user: 1000:1000` in Forge.
# The `forge` group/user is created with a fixed UID/GID so container
# filesystem mounts (which Forge bind-mounts with the host uid) line up.
RUN groupadd --gid 1000 forge \
 && useradd --uid 1000 --gid forge --create-home --shell /bin/bash forge

WORKDIR /work
USER forge:forge
CMD ["sleep", "infinity"]
