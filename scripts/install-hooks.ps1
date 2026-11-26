$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & git config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install repository-local hooks' }
    & git config --local fetch.prune true
    if ($LASTEXITCODE -ne 0) { throw 'Cannot configure repository-local pruning' }
    Write-Host 'Installed repository-local hooks and fetch pruning. GitHub checks remain mandatory.'
} finally { Pop-Location }
