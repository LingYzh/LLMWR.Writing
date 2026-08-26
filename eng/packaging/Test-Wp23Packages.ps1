[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [switch]$SkipInstallLifecycle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repositoryRoot '.artifacts\release'
}
$ReleaseRoot = [System.IO.Path]::GetFullPath($ReleaseRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $ReleaseRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "WP23 release verification must use artifacts inside the repository: $ReleaseRoot"
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Reset-Directory {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a verification directory outside the repository: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

function Find-WindowsSdkTool {
    param([string]$Name)
    $roots = @($env:NUGET_PACKAGES, (Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'), (Join-Path $repositoryRoot '.artifacts\nuget-packages')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($null -ne $candidate) { return $candidate.FullName }
    }
    throw "$Name was not found in the restored Windows SDK BuildTools graph."
}

function Stop-TestProcess {
    param([System.Diagnostics.Process]$Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        $Process.Kill($true)
        $null = $Process.WaitForExit(5000)
    }
    if ($null -ne $Process) { $Process.Dispose() }
}

function Assert-UiLaunch {
    param([string]$Executable)
    $process = [System.Diagnostics.Process]::Start([System.Diagnostics.ProcessStartInfo]@{
        FileName = $Executable
        UseShellExecute = $false
        WorkingDirectory = Split-Path -Parent $Executable
    })
    if ($null -eq $process) { throw "UI launch returned no process: $Executable" }
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $observedAlive = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "UI exited during release launch verification with code $($process.ExitCode)."
            }
            $observedAlive = $true
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 100
        }
        Assert-True $observedAlive 'UI never reached a live process state.'
    }
    finally { Stop-TestProcess $process }
}

$manifestPath = Join-Path $ReleaseRoot 'release-manifest.json'
Assert-True (Test-Path -LiteralPath $manifestPath) "Release manifest is missing: $manifestPath"
$release = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$portableArtifact = $release.artifacts | Where-Object { $_.name -like '*-portable.zip' } | Select-Object -First 1
$msixArtifact = $release.artifacts | Where-Object { $_.name -like '*.msix' } | Select-Object -First 1
Assert-True ($null -ne $portableArtifact) 'Portable artifact is absent from release-manifest.json.'
Assert-True ($null -ne $msixArtifact) 'MSIX artifact is absent from release-manifest.json.'
$portableZip = Join-Path $ReleaseRoot $portableArtifact.name
$msix = Join-Path $ReleaseRoot $msixArtifact.name
Assert-True ((Get-FileHash -LiteralPath $portableZip -Algorithm SHA256).Hash -eq $portableArtifact.sha256) 'Portable SHA-256 does not match the release manifest.'
Assert-True ((Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash -eq $msixArtifact.sha256) 'MSIX SHA-256 does not match the release manifest.'

$workRoot = Join-Path $repositoryRoot '.artifacts\wp23-package-tests'
Reset-Directory $workRoot
$portableRoot = Join-Path $workRoot 'portable'
Expand-Archive -LiteralPath $portableZip -DestinationPath $portableRoot
foreach ($relative in @('LLMW.Writing.UI.exe', 'core\LLMW.Writing.Core.exe', 'runtime\LLMW.Writing.AgentRuntime.exe', 'worker\LLMW.Writing.Worker.exe', 'portable.marker', 'data')) {
    Assert-True (Test-Path -LiteralPath (Join-Path $portableRoot $relative)) "Portable payload is missing $relative."
}
Assert-True (-not (Test-Path -LiteralPath (Join-Path $portableRoot 'AppxManifest.xml'))) 'Portable payload must not carry MSIX identity.'
Assert-UiLaunch (Join-Path $portableRoot 'LLMW.Writing.UI.exe')

$e2eOutput = Join-Path $repositoryRoot '.artifacts\test-results\wp23-packaged-runtime.json'
Invoke-Checked 'dotnet' @(
    'run', '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.E2E.Tests\LLMW.Writing.E2E.Tests.csproj'),
    '--configuration', 'Release', '--', '--runtime-root', $portableRoot, '--output', $e2eOutput)

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$unpacked = Join-Path $workRoot 'msix-unpacked'
New-Item -ItemType Directory -Path $unpacked -Force | Out-Null
Invoke-Checked $makeAppx @('unpack', '/p', $msix, '/d', $unpacked, '/o')
[xml]$appxManifest = Get-Content -LiteralPath (Join-Path $unpacked 'AppxManifest.xml') -Raw
$namespace = [System.Xml.XmlNamespaceManager]::new($appxManifest.NameTable)
$namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespace.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
$capabilities = @($appxManifest.SelectNodes('/f:Package/f:Capabilities/*', $namespace) | ForEach-Object { $_.Prefix + ':' + $_.LocalName + ':' + $_.Attributes['Name'].Value })
Assert-True ($capabilities.Count -eq 1 -and $capabilities[0] -eq 'rescap:Capability:runFullTrust') 'MSIX capability declaration changed or escalated.'
Assert-True (-not ($appxManifest.OuterXml -match 'broadFileSystemAccess|internetClient|documentsLibrary|picturesLibrary|videosLibrary')) 'MSIX declares a forbidden filesystem/network/library capability.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $unpacked 'portable.marker'))) 'MSIX must use installed application-data semantics, not portable mode.'

$lifecycle = 'skipped'
if (-not $SkipInstallLifecycle) {
    $testIdentity = 'LLMW.Writing.WP23.Test.' + ([Guid]::NewGuid().ToString('N').Substring(0, 12))
    $publisher = 'CN=LLMW.Writing.WP23.Test, OID.2.25.311729368913984317654407730594956997722=1'
    $installed = $null
    try {
        $currentStage = Join-Path $workRoot 'msix-current'
        $oldStage = Join-Path $workRoot 'msix-old'
        Copy-Item -LiteralPath $unpacked -Destination $currentStage -Recurse
        Copy-Item -LiteralPath $unpacked -Destination $oldStage -Recurse
        foreach ($entry in @(@($currentStage, $release.packageVersion), @($oldStage, '0.0.9.0'))) {
            [xml]$testManifest = Get-Content -LiteralPath (Join-Path $entry[0] 'AppxManifest.xml') -Raw
            $testManifest.Package.Identity.Name = $testIdentity
            $testManifest.Package.Identity.Publisher = $publisher
            $testManifest.Package.Identity.Version = $entry[1]
            $testManifest.Save((Join-Path $entry[0] 'AppxManifest.xml'))
        }

        $currentPackage = Join-Path $workRoot 'wp23-current-test.msix'
        $oldPackage = Join-Path $workRoot 'wp23-old-test.msix'
        Invoke-Checked $makeAppx @('pack', '/d', $oldStage, '/p', $oldPackage, '/o')
        Invoke-Checked $makeAppx @('pack', '/d', $currentStage, '/p', $currentPackage, '/o')

        Add-AppxPackage -Path $oldPackage -AllowUnsigned -ForceApplicationShutdown
        $installed = Get-AppxPackage -Name $testIdentity
        Assert-True ($null -ne $installed -and $installed.Version.ToString() -eq '0.0.9.0') 'MSIX clean install did not register the lower test version.'

        Add-AppxPackage -Path $currentPackage -AllowUnsigned -ForceApplicationShutdown
        $installed = Get-AppxPackage -Name $testIdentity
        Assert-True ($null -ne $installed -and $installed.Version.ToString() -eq $release.packageVersion) 'MSIX upgrade did not replace the lower test version.'

        Invoke-Checked 'dotnet' @(
            'run', '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.E2E.Tests\LLMW.Writing.E2E.Tests.csproj'),
            '--configuration', 'Release', '--', '--runtime-root', $installed.InstallLocation,
            '--output', (Join-Path $repositoryRoot '.artifacts\test-results\wp23-msix-runtime.json'))

        $existingIds = @(Get-Process -Name 'LLMW.Writing.UI' -ErrorAction SilentlyContinue | ForEach-Object Id)
        Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($installed.PackageFamilyName)!App"
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $launched = $null
        while ([DateTime]::UtcNow -lt $deadline -and $null -eq $launched) {
            Start-Sleep -Milliseconds 200
            $launched = Get-Process -Name 'LLMW.Writing.UI' -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $existingIds } | Select-Object -First 1
        }
        Assert-True ($null -ne $launched) 'Installed MSIX application did not launch through package activation.'
        if ($null -ne $launched) { Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue }

        Remove-AppxPackage -Package $installed.PackageFullName
        $installed = $null
        Assert-True ($null -eq (Get-AppxPackage -Name $testIdentity)) 'MSIX uninstall left the test package registered.'
        $lifecycle = 'install-launch-upgrade-uninstall-passed'
    }
    finally {
        $leftover = Get-AppxPackage -Name $testIdentity -ErrorAction SilentlyContinue
        if ($null -ne $leftover) { Remove-AppxPackage -Package $leftover.PackageFullName -ErrorAction SilentlyContinue }
    }
}

[ordered]@{
    schemaVersion = 1
    status = 'passed'
    portableLaunch = 'passed'
    packagedRuntimeWorkflow = 'passed'
    msixManifestSecurity = 'passed'
    msixLifecycle = $lifecycle
    userVersion = (Get-Content -LiteralPath $e2eOutput -Raw | ConvertFrom-Json).userVersion
    migrationCount = (Get-Content -LiteralPath $e2eOutput -Raw | ConvertFrom-Json).migrationCount
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repositoryRoot '.artifacts\test-results\wp23-package-summary.json') -Encoding utf8NoBOM

Write-Host "WP23 package verification passed: $lifecycle"
