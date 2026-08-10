[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'bin\release'
}

$appDirectory = Join-Path $repoRoot 'bin\app'
$appExecutable = Join-Path $appDirectory 'NahidaTool.exe'
$launcher = Join-Path $repoRoot 'bin\NahidaTool.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "Application build output was not found: $appExecutable"
}
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Launcher build output was not found: $launcher"
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$archivePath = Join-Path $resolvedOutput "NahidaTool-$Architecture.zip"
$checksumPath = "$archivePath.sha256"
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("NahidaTool-release-" + [Guid]::NewGuid().ToString('N'))

try {
    $stagingApp = Join-Path $stagingRoot 'app'
    [IO.Directory]::CreateDirectory($stagingApp) | Out-Null
    Copy-Item -Path (Join-Path $appDirectory '*') -Destination $stagingApp -Recurse -Force
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $stagingRoot 'NahidaTool.exe') -Force

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Compress-Archive -LiteralPath (Join-Path $stagingRoot 'NahidaTool.exe'), $stagingApp `
        -DestinationPath $archivePath -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $archivePath)" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    Get-Item -LiteralPath $archivePath, $checksumPath
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
