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
# The internal LibGit2Sharp.NativeBinaries package deliberately shadows the upstream
# compatibility version. A repository-local cache makes restore provenance deterministic
# even when a developer previously cached the upstream package under the same version.
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
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
}

function Invoke-ApplicationTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Application.Tests\LLMW.Writing.Application.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
}

function Invoke-DomainTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Domain.Tests\LLMW.Writing.Domain.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
}

function Invoke-ContractsTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Contracts.Tests\LLMW.Writing.Contracts.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
}

function Invoke-InfrastructureTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.Infrastructure.Tests\LLMW.Writing.Infrastructure.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
}

function Invoke-UITests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.UI.Tests\LLMW.Writing.UI.Tests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
}

function Invoke-IntegrationTests {
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
        'run',
        '--project', (Join-Path $repositoryRoot 'tests\LLMW.Writing.IntegrationTests\LLMW.Writing.IntegrationTests.csproj'),
        '--configuration', $Configuration,
        '--no-build')
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
    }
    'IntegrationTest' {
        Invoke-Restore
        Invoke-Build
        Invoke-IntegrationTests
    }
    'Package' {
        throw 'Packaging is intentionally deferred to WP23; no package artifact is produced by WP00.'
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
        throw 'Packaging is intentionally deferred to WP23; no package artifact is produced by WP00.'
    }
}
