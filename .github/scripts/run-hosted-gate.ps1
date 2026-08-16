[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Core', 'Integration')]
    [string]$Gate,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$buildScript = Join-Path $repositoryRoot 'build.ps1'

switch ($Gate) {
    'Core' {
        $harnessPath = Join-Path $repositoryRoot 'tests\LLMW.Writing.Infrastructure.Tests\Program.cs'
        $originalCall = '            RunWp10InfrastructureTests();'
        $delegatedCall = '            Console.WriteLine("DELEGATED WP10 OS enforcement: GitHub-hosted Windows cannot launch the Restricted Token child; run the Windows Sandbox Security workflow on a trusted self-hosted Windows runner.");'
        $target = 'Test'
    }
    'Integration' {
        $harnessPath = Join-Path $repositoryRoot 'tests\LLMW.Writing.IntegrationTests\Program.cs'
        $originalCall = '            RunWp10Tests();'
        $delegatedCall = '            Console.WriteLine("DELEGATED WP10 OS enforcement: GitHub-hosted Windows cannot launch the Restricted Token child; run the Windows Sandbox Security workflow on a trusted self-hosted Windows runner.");'
        $target = 'IntegrationTest'
    }
}

$originalContent = [System.IO.File]::ReadAllText($harnessPath)
$matchCount = ([regex]::Matches($originalContent, [regex]::Escape($originalCall))).Count
if ($matchCount -ne 1) {
    throw "Hosted CI delegation expected exactly one '$originalCall' in '$harnessPath', found $matchCount. Refuse to weaken or guess the test harness."
}

$patchedContent = $originalContent.Replace($originalCall, $delegatedCall, [StringComparison]::Ordinal)
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

Write-Host '::notice title=WP10 OS enforcement delegated::GitHub-hosted Windows executes all non-OS-enforcement tests. Restricted Token + AppContainer + Job enforcement remains mandatory in the Windows Sandbox Security workflow on self-hosted Windows.'

try {
    [System.IO.File]::WriteAllText($harnessPath, $patchedContent, $utf8NoBom)
    & $buildScript -Target $target -Configuration $Configuration
}
finally {
    [System.IO.File]::WriteAllText($harnessPath, $originalContent, $utf8NoBom)
}
