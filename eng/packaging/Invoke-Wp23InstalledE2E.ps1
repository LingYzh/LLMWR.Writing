[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeRoot,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [Parameter(Mandatory)]
    [string]$ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'
$exitCode = 1
$failure = $null
try {
    & dotnet run --project (Join-Path $repositoryRoot 'tests\LLMW.Writing.E2E.Tests\LLMW.Writing.E2E.Tests.csproj') `
        --configuration Release -- --runtime-root $RuntimeRoot --output $OutputPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $failure = "Installed-payload E2E exited with code $exitCode."
    }
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        status = if ($exitCode -eq 0 -and $null -eq $failure) { 'passed' } else { 'failed' }
        exitCode = $exitCode
        failure = $failure
    } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8NoBOM
}

exit $exitCode
