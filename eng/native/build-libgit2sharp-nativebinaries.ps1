[CmdletBinding()]
param(
    [string]$ArchivePath = (Join-Path $env:TEMP 'pygit2-1.20.0-cp311-cp311-win_amd64.whl')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$auditPath = Join-Path $PSScriptRoot 'LibGit2Sharp.NativeBinaries.audit.json'
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json

if (-not (Test-Path -LiteralPath $ArchivePath)) {
    Invoke-WebRequest -Uri $audit.source.archiveUrl -OutFile $ArchivePath
}

$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArchivePath).Hash.ToLowerInvariant()
if ($archiveHash -ne $audit.source.archiveSha256) {
    throw "Native source archive hash mismatch: $archiveHash"
}

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('llmw-native-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stageRoot | Out-Null
try {
    tar -xzf $ArchivePath -C $stageRoot
    $windowsAssets = Join-Path $stageRoot 'pygit2'
    foreach ($property in $audit.nativeAssets.PSObject.Properties) {
        $sourceName = if ($property.Name -eq 'git2-5853918.dll') { 'git2.dll' } else { $property.Name }
        $assetPath = Join-Path $windowsAssets $sourceName
        $assetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
        if ($assetHash -ne $property.Value) {
            throw "Native asset hash mismatch for ${sourceName}: $assetHash"
        }
    }

    $packageProject = Join-Path $PSScriptRoot 'LibGit2Sharp.NativeBinaries\LibGit2Sharp.NativeBinaries.csproj'
    $feedDirectory = Join-Path $PSScriptRoot 'feed'
    dotnet pack $packageProject --configuration Release --output $feedDirectory "-p:NativeAssetDirectory=$windowsAssets"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed with exit code $LASTEXITCODE"
    }
}
finally {
    # Keep the per-run staging directory for audit/debugging; it is created under the OS temp area.
    Write-Verbose "Native package staging directory: $stageRoot"
}
