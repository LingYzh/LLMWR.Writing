[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Test', 'IntegrationTest', 'Package', 'All')]
    [string]$Target = 'Build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'LLMW.Writing.sln'
$webEditorManifest = Join-Path $repositoryRoot 'src\web-editor\package.json'
$nuGetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
$artifactRoot = Join-Path $repositoryRoot '.artifacts'
$testResultRoot = Join-Path $artifactRoot 'test-results'
# The internal LibGit2Sharp.NativeBinaries package deliberately shadows the upstream
# compatibility version. A repository-local cache makes restore provenance deterministic
# even when a developer previously cached the upstream package under the same version.
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$LogPath
    )

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    else {
        $logDirectory = Split-Path -Parent $LogPath
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $LogPath
        $exitCode = $LASTEXITCODE
    }
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function New-BuildArtifact {
    $buildRoot = Join-Path $artifactRoot 'build'
    $stage = Join-Path $artifactRoot 'wp23-build-stage'
    foreach ($path in @($buildRoot, $stage)) {
        $full = [System.IO.Path]::GetFullPath($path)
        $prefix = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') + '\'
        if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace build artifacts outside the repository: $full"
        }
        if (Test-Path -LiteralPath $full) {
            Remove-Item -LiteralPath $full -Recurse -Force
        }
        New-Item -ItemType Directory -Path $full -Force | Out-Null
    }

    foreach ($project in @('LLMW.Writing.UI', 'LLMW.Writing.Core', 'LLMW.Writing.AgentRuntime', 'LLMW.Writing.Worker')) {
        $source = Join-Path $repositoryRoot "src\$project\bin\$Configuration"
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Expected build output is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $project) -Recurse
    }

    $version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng\Versions.props') -Raw)).Project.PropertyGroup.VersionPrefix
    $zip = Join-Path $buildRoot "LLMW.Writing-$version-build.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    [ordered]@{
        schemaVersion = 1
        configuration = $Configuration
        artifact = (Split-Path -Leaf $zip)
        bytes = (Get-Item -LiteralPath $zip).Length
        sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'build-manifest.json') -Encoding utf8NoBOM
}

function Write-TestSummary {
    param([string]$Gate, [string[]]$Suites)
    New-Item -ItemType Directory -Path $testResultRoot -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        gate = $Gate
        configuration = $Configuration
        status = 'passed'
        suites = $Suites
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testResultRoot "$Gate-summary.json") -Encoding utf8NoBOM
}

function Initialize-WebToolchain {
    if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) {
        throw 'Node.js 24 LTS is required to build src/web-editor but node was not found on PATH.'
    }

    if ($null -eq (Get-Command corepack -ErrorAction SilentlyContinue)) {
        throw 'Corepack is required to activate the repository-pinned pnpm 11.19.0.'
    }

    & corepack enable
    if ($LASTEXITCODE -ne 0) {
        throw "corepack enable failed with exit code $LASTEXITCODE"
    }

    & corepack prepare pnpm@11.19.0 --activate
    if ($LASTEXITCODE -ne 0) {
        throw "corepack prepare pnpm@11.19.0 failed with exit code $LASTEXITCODE"
    }
}

function Invoke-WebEditorBuild {
    if (-not (Test-Path -LiteralPath $webEditorManifest)) {
        throw "Web editor workspace manifest is missing: $webEditorManifest"
    }

    Initialize-WebToolchain
    $webDir = Join-Path $repositoryRoot 'src\web-editor'
    Invoke-CheckedCommand -FilePath 'pnpm' -Arguments @('--dir', $repositoryRoot, 'install', '--frozen-lockfile')
    Invoke-CheckedCommand -FilePath 'pnpm' -Arguments @('--dir', $webDir, 'run', 'test')
    Invoke-CheckedCommand -FilePath 'pnpm' -Arguments @('--dir', $webDir, 'run', 'build')
}

function Invoke-Restore {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('restore', $solutionPath, '--configfile', $nuGetConfigPath)
}

function Invoke-Build {
    Invoke-WebEditorBuild
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('build', $solutionPath, '--configuration', $Configuration, '--no-restore')
    New-BuildArtifact
}

function Invoke-ApplicationTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Application.Tests\LLMW.Writing.Application.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'application.log')
}

function Invoke-DomainTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Domain.Tests\LLMW.Writing.Domain.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'domain.log')
}

function Invoke-ContractsTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Contracts.Tests\LLMW.Writing.Contracts.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'contracts.log')
}

function Invoke-InfrastructureTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Infrastructure.Tests\LLMW.Writing.Infrastructure.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'infrastructure.log')
}

function Invoke-UITests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.UI.Tests\LLMW.Writing.UI.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'ui.log')
}

function Invoke-IntegrationTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.IntegrationTests\LLMW.Writing.IntegrationTests.csproj'),
        '--configuration', $Configuration,
        '--no-build') -LogPath (Join-Path $testResultRoot 'integration.log')
}

function Invoke-Package {
    $packageScript = Join-Path $repositoryRoot 'eng\packaging\New-Wp23Packages.ps1'
    & $packageScript -Configuration $Configuration

    $testScript = Join-Path $repositoryRoot 'eng\packaging\Test-Wp23Packages.ps1'
    Invoke-CheckedCommand -FilePath 'pwsh' -Arguments @('-NoProfile', '-File', $testScript) -LogPath (Join-Path $testResultRoot 'package.log')
    Write-TestSummary -Gate 'package' -Suites @('msix-install', 'msix-upgrade', 'msix-uninstall', 'portable-launch', 'clean-environment-workflow', 'security-manifest')
}

switch ($Target) {
    'Restore' {
        Invoke-Restore
    }
    'Build' {
        Invoke-Restore
        Invoke-Build
    }
    'Test' {
        Invoke-Restore
        Invoke-Build
        Invoke-ContractsTests
        Invoke-DomainTests
        Invoke-ApplicationTests
        Invoke-InfrastructureTests
        Invoke-UITests
        Write-TestSummary -Gate 'test' -Suites @('contracts', 'domain', 'application', 'infrastructure', 'ui')
    }
    'IntegrationTest' {
        Invoke-Restore
        Invoke-Build
        Invoke-IntegrationTests
        Write-TestSummary -Gate 'integration' -Suites @('integration')
    }
    'Package' {
        Invoke-Restore
        Invoke-WebEditorBuild
        Invoke-Package
    }
    'All' {
        Invoke-Restore
        Invoke-Build
        Invoke-ContractsTests
        Invoke-DomainTests
        Invoke-ApplicationTests
        Invoke-InfrastructureTests
        Invoke-UITests
        Invoke-IntegrationTests
        Write-TestSummary -Gate 'all' -Suites @('contracts', 'domain', 'application', 'infrastructure', 'ui', 'integration')
        Invoke-Package
    }
}
