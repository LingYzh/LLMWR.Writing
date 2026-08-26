[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) { $ReleaseRoot = Join-Path $repositoryRoot '.artifacts\release' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repositoryRoot '.artifacts\performance\wp23-performance.json' }
$releaseManifest = Join-Path $ReleaseRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $releaseManifest)) {
    throw "Build WP23 release packages before measuring performance: $releaseManifest"
}

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $FilePath $($Arguments -join ' ')" }
}

function Measure-UiStartup {
    param([string]$Executable)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start([System.Diagnostics.ProcessStartInfo]@{
        FileName = $Executable
        WorkingDirectory = Split-Path -Parent $Executable
        UseShellExecute = $false
    })
    if ($null -eq $process) { throw 'Portable UI did not start for the performance baseline.' }
    try {
        if (-not $process.WaitForInputIdle(10000)) { throw 'Portable UI did not reach an input-idle interactive state.' }
        $watch.Stop()
        return $watch.Elapsed.TotalMilliseconds
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
}

$release = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
$portableName = ($release.artifacts | Where-Object { $_.name -like '*-portable.zip' } | Select-Object -First 1).name
$portableZip = Join-Path $ReleaseRoot $portableName
$measureRoot = Join-Path $repositoryRoot '.artifacts\wp23-performance-runtime'
$repoPrefix = $repositoryRoot.TrimEnd('\') + '\'
$measureFull = [System.IO.Path]::GetFullPath($measureRoot)
if (-not $measureFull.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe performance staging path.' }
if (Test-Path -LiteralPath $measureFull) { Remove-Item -LiteralPath $measureFull -Recurse -Force }
Expand-Archive -LiteralPath $portableZip -DestinationPath $measureFull

$ui = Join-Path $measureFull 'LLMW.Writing.UI.exe'
$coldStartup = Measure-UiStartup $ui
$warmStartup = Measure-UiStartup $ui
$runtimeEvidencePath = Join-Path $repositoryRoot '.artifacts\performance\wp23-runtime-measurements.json'
Invoke-Checked 'dotnet' @(
    'run', '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.E2E.Tests\LLMW.Writing.E2E.Tests.csproj'),
    '--configuration', 'Release', '--', '--runtime-root', $measureFull, '--output', $runtimeEvidencePath)
$runtime = Get-Content -LiteralPath $runtimeEvidencePath -Raw | ConvertFrom-Json
$baseline = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'WP23.baseline.json') -Raw | ConvertFrom-Json

$measurements = [ordered]@{
    coldStartup = [Math]::Round([double]$coldStartup, 2)
    warmStartup = [Math]::Round([double]$warmStartup, 2)
    coldCoreReady = [Math]::Round([double]$runtime.coldCoreReadyMs, 2)
    warmCoreReady = [Math]::Round([double]$runtime.warmCoreReadyMs, 2)
    projectOpen = [Math]::Round([double]$runtime.projectOpenMs, 2)
    startupRecoveryOverhead = [Math]::Round([double]$runtime.recoveryOverheadMs, 2)
    migration = [Math]::Round([double]$runtime.migrationMs, 2)
}
$regressions = @()
foreach ($name in $measurements.Keys) {
    $threshold = [double]$baseline.thresholdsMs.$name
    if ([double]$measurements[$name] -gt $threshold) {
        $regressions += [ordered]@{ metric = $name; measuredMs = $measurements[$name]; thresholdMs = $threshold }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    baseline = 'eng/performance/WP23.baseline.json'
    measurementsMs = $measurements
    thresholdsMs = $baseline.thresholdsMs
    regressionCount = $regressions.Count
    regressions = $regressions
    projectLoadCoverage = 'Core open-project composition includes migration preflight, WP22 recovery, registry/repository services, extensions, editor, Git, packages, and watcher startup.'
    userVersion = $runtime.userVersion
    migrationCount = $runtime.migrationCount
}
$outputFull = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFull) -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFull -Encoding utf8NoBOM
Write-Host ($result | ConvertTo-Json -Depth 8)
if ($regressions.Count -ne 0) { throw "WP23 performance regression detected in $($regressions.Count) metric(s)." }
