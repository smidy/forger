# Internal helper. Called by delegate-bg.ps1 via Start-Process.
# Runs `forge agent <name>` and writes the exit code to <ExitCodePath>.
#
# usage: _bg-runner.ps1 <agent-name> <input-arg> <exit-code-path>

param(
    [Parameter(Mandatory=$true, Position=0)] [string]$Agent,
    [Parameter(Mandatory=$true, Position=1)] [string]$InputArg,
    [Parameter(Mandatory=$true, Position=2)] [string]$ExitCodePath
)

$ErrorActionPreference = 'Continue'

& forge agent $Agent --input $InputArg --callers auto-deny

$ascii = New-Object System.Text.ASCIIEncoding
[System.IO.File]::WriteAllText($ExitCodePath, "$LASTEXITCODE", $ascii)
