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

function Invoke-WebEditorBuild {
    if (-not (Test-Path -LiteralPath $webEditorManifest)) {
        throw "Web editor workspace manifest is missing: $webEditorManifest"
    }

    $manifest = Get-Content -Raw -LiteralPath $webEditorManifest | ConvertFrom-Json
    $buildProperty = $manifest.scripts.PSObject.Properties['build']
    $buildScript = if ($null -ne $buildProperty) { [string]$buildProperty.Value } else { $null }

    if ([string]::IsNullOrWhiteSpace($buildScript)) {
        Write-Host 'web-editor: WP15 ships static renderer assets under src/web-editor/app; the pnpm web build stage is a no-op until WP16.'
        return
    }

    if ($null -eq (Get-Command pnpm -ErrorAction SilentlyContinue)) {
        throw 'pnpm is required to build src/web-editor but was not found on PATH.'
    }

    Invoke-CheckedCommand -FilePath 'pnpm' -Arguments @('--dir', (Join-Path $repositoryRoot 'src\web-editor'), 'run', 'build')
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
