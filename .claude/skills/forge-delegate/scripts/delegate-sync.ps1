# delegate-sync.ps1 <agent-name> <input-json>
#
# Sync wrapper for `forge agent <name>`. Blocks until forge exits.
# Stdout = forge's stdout (the agent's final JSON output).
# Stderr = forge's stderr (logs).
# Exit code = forge's exit code (0 / 1 / 2 / 6 / 7 / 130).
#
# Caller decides on exit-code semantics; this script does not auto-resume.

param(
    [Parameter(Mandatory=$true, Position=0)] [string]$AgentName,
    [Parameter(Mandatory=$true, Position=1)] [string]$InputJson
)

$ErrorActionPreference = 'Continue'

& forge agent $AgentName --input $InputJson --callers auto-deny
exit $LASTEXITCODE
