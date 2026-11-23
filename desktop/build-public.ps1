param(
    [Parameter(Mandatory=$true)][string]$EngineDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = (Resolve-Path -LiteralPath $EngineDirectory).Path
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'bin/public-win-x64' }
$required = @('bacnet-explorer-engine.exe', 'STACK-LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $engineRoot $name) -PathType Leaf)) { throw "Missing release component: $name" }
}
if ([IO.Path]::GetFullPath($output).TrimEnd('\') -eq $engineRoot.TrimEnd('\')) { throw 'Choose an output directory separate from the installed engine.' }
& dotnet publish (Join-Path $PSScriptRoot 'BACnetExplorer/BACnetExplorer.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
foreach ($name in $required) { Copy-Item -LiteralPath (Join-Path $engineRoot $name) -Destination (Join-Path $output $name) }
foreach ($name in @('LICENSE.txt', 'NOTICE')) { Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination (Join-Path $output $name) }
Write-Host "Published interface with supplied engine: $output"
