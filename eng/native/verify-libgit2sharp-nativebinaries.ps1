[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$auditPath = Join-Path $PSScriptRoot 'LibGit2Sharp.NativeBinaries.audit.json'
$packagePath = Join-Path $PSScriptRoot 'feed\LibGit2Sharp.NativeBinaries.2.0.324.nupkg'
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json

$packageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash.ToLowerInvariant()
if ($packageHash -ne $audit.packageSha256) {
    throw "Native package hash mismatch: $packageHash"
}

Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entry = $archive.GetEntry('runtimes/win-x64/native/git2-5853918.dll')
    if ($null -eq $entry) {
        throw 'The controlled native package does not contain the win-x64 libgit2 asset.'
    }

    $stream = $entry.Open()
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $actual = [Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$expected = $audit.nativeAssets.'git2-5853918.dll'
if ($actual -ne $expected) {
    throw "Native libgit2 asset hash mismatch: $actual"
}

if ($audit.libgit2Version -notmatch '^1\.9\.') {
    throw "Native libgit2 must remain on a patched 1.9.x release, not $($audit.libgit2Version)."
}

Write-Host "WP19 native audit package=$packageHash libgit2=$($audit.libgit2Version) binary=$actual"
