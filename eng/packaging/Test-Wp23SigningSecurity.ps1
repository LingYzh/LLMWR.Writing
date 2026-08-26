[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsixPath,

    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$SignToolPath,

    [Parameter(Mandatory)]
    [string]$WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SigningSecurity {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function New-TestPfx {
    param([string]$Subject, [string]$Name, [bool]$LeaveInPersonalStore)

    $pfxPath = Join-Path $WorkRoot "$Name.pfx"
    $cerPath = Join-Path $WorkRoot "$Name.cer"
    $certificate = $null
    $signingPassword = $null
    $trustExisted = $true
    try {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $Subject `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyExportPolicy Exportable `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -NotAfter ([DateTime]::Now.AddDays(2))
        $plainPassword = "SEC-WP23-01-CANARY-$Name-$([Guid]::NewGuid().ToString('N'))"
        try {
            $exportPassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $signingPassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
        }
        finally {
            $plainPassword = $null
        }
        try {
            Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $exportPassword -Force | Out-Null
            Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null
        }
        finally {
            $exportPassword.Dispose()
            $exportPassword = $null
        }

        $trustExisted = Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
        if (-not $trustExisted) {
            & certutil.exe -user -f -addstore Root $cerPath | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'The test signing certificate could not be trusted.' }
        }
        if (-not $LeaveInPersonalStore) {
            Remove-Item -LiteralPath $certificate.PSPath -Force
        }

        return [pscustomobject]@{
            Certificate = $certificate
            PfxPath = $pfxPath
            CerPath = $cerPath
            Password = $signingPassword
            TrustExisted = $trustExisted
            LeaveInPersonalStore = $LeaveInPersonalStore
        }
    }
    catch {
        if ($null -ne $certificate) {
            Remove-TestPfx ([pscustomobject]@{
                Certificate = $certificate
                PfxPath = $pfxPath
                CerPath = $cerPath
                Password = $signingPassword
                TrustExisted = $trustExisted
            })
        }
        throw
    }
}

function Remove-TestPfx {
    param($Fixture)
    if ($null -eq $Fixture) { return }
    if ($null -ne $Fixture.Password) {
        $Fixture.Password.Dispose()
        $Fixture.Password = $null
    }
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($Fixture.Certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    if (-not $Fixture.TrustExisted) {
        & certutil.exe -user -delstore Root $Fixture.Certificate.Thumbprint | Out-Null
        if ($LASTEXITCODE -ne 0 -and (Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($Fixture.Certificate.Thumbprint)")) {
            throw 'The temporary test trust certificate could not be removed.'
        }
    }
    Remove-Item -LiteralPath $Fixture.PfxPath, $Fixture.CerPath -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'Wp23Signing.ps1')

$signingSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Wp23Signing.ps1') -Raw
$packageSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-Wp23Packages.ps1') -Raw
$buildSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\build.ps1') -Raw
Assert-SigningSecurity (-not ($signingSource -match '(?i)[''"]/p[''"]')) 'The signing implementation contains a SignTool /p password argument.'
Assert-SigningSecurity (-not ($packageSource -match '(?i)signtool[^\r\n]*\/p')) 'The package entry point passes a password to SignTool.'
Assert-SigningSecurity (-not ($packageSource -match '\[string\]\$SigningCertificatePassword')) 'The package entry point accepts a plaintext password parameter.'
$clearIndex = $packageSource.IndexOf('Remove-Item Env:LLMW_MSIX_SIGNING_CERT_PASSWORD', [StringComparison]::Ordinal)
$publishIndex = $packageSource.IndexOf('Publish-Application (Join-Path', [StringComparison]::Ordinal)
Assert-SigningSecurity ($clearIndex -ge 0 -and $publishIndex -gt $clearIndex) 'The signing password is not cleared before release child processes start.'
Assert-SigningSecurity (-not ($buildSource -match 'Invoke-CheckedCommand\s+-FilePath\s+''pwsh''\s+-Arguments\s+\$arguments')) 'build.ps1 launches packaging in a child PowerShell process that can inherit the plaintext password.'
$argumentProbe = New-Wp23SignToolSignArguments -CertificateThumbprint ('A' * 40) -MsixPath 'probe.msix'
Assert-SigningSecurity ('/p' -notin $argumentProbe) 'The SignTool argument builder emitted /p.'
Assert-SigningSecurity (($argumentProbe -join ' ') -eq "sign /fd SHA256 /sha1 $('A' * 40) /s My probe.msix") 'The SignTool argument form changed unexpectedly.'

[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$publisher = [string]$manifest.Package.Identity.Publisher
$fixtures = @()
try {
    $success = New-TestPfx -Subject $publisher -Name 'success' -LeaveInPersonalStore $false
    $fixtures += $success
    $signedMsix = Join-Path $WorkRoot 'signed.msix'
    Copy-Item -LiteralPath $MsixPath -Destination $signedMsix -Force
    $signOutput = Invoke-Wp23MsixSigning `
        -MsixPath $signedMsix `
        -ManifestPath $ManifestPath `
        -PfxPath $success.PfxPath `
        -PfxPassword $success.Password `
        -SignToolPath $SignToolPath 2>&1 | Out-String
    $success.Password = $null
    Assert-SigningSecurity (-not ($signOutput -match 'SEC-WP23-01-CANARY-')) 'Signing output exposed the PFX password canary.'
    Assert-SigningSecurity (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$($success.Certificate.Thumbprint)")) 'A newly imported signing certificate was not removed after success.'
    & $SignToolPath verify /pa /v $signedMsix | Out-Null
    Assert-SigningSecurity ($LASTEXITCODE -eq 0) 'The signed security-test MSIX did not verify.'

    $mismatch = New-TestPfx -Subject $publisher -Name 'mismatch' -LeaveInPersonalStore $false
    $fixtures += $mismatch
    $mismatchManifest = Join-Path $WorkRoot 'mismatch-AppxManifest.xml'
    [xml]$changedManifest = Get-Content -LiteralPath $ManifestPath -Raw
    $changedManifest.Package.Identity.Publisher = 'CN=SEC-WP23-01-Mismatch'
    $changedManifest.Save($mismatchManifest)
    $mismatchFailed = $false
    try {
        Invoke-Wp23MsixSigning `
            -MsixPath (Join-Path $WorkRoot 'mismatch.msix') `
            -ManifestPath $mismatchManifest `
            -PfxPath $mismatch.PfxPath `
            -PfxPassword $mismatch.Password `
            -SignToolPath $SignToolPath
    }
    catch {
        $mismatchFailed = $_.Exception.Message -eq 'The signing certificate Subject does not exactly match the package Publisher.'
        Assert-SigningSecurity (-not ($_.Exception.ToString() -match 'SEC-WP23-01-CANARY-')) 'Publisher mismatch diagnostics exposed the PFX password canary.'
    }
    $mismatch.Password = $null
    Assert-SigningSecurity $mismatchFailed 'Publisher/Subject mismatch did not fail closed with the expected diagnostic.'
    Assert-SigningSecurity (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$($mismatch.Certificate.Thumbprint)")) 'A newly imported certificate remained after failed validation.'

    $preExisting = New-TestPfx -Subject $publisher -Name 'preexisting' -LeaveInPersonalStore $true
    $fixtures += $preExisting
    $preExistingMsix = Join-Path $WorkRoot 'preexisting.msix'
    Copy-Item -LiteralPath $MsixPath -Destination $preExistingMsix -Force
    Invoke-Wp23MsixSigning `
        -MsixPath $preExistingMsix `
        -ManifestPath $ManifestPath `
        -PfxPath $preExisting.PfxPath `
        -PfxPassword $preExisting.Password `
        -SignToolPath $SignToolPath | Out-Null
    $preExisting.Password = $null
    Assert-SigningSecurity (Test-Path -LiteralPath "Cert:\CurrentUser\My\$($preExisting.Certificate.Thumbprint)") 'Signing removed a certificate that existed before the invocation.'
}
finally {
    foreach ($fixture in $fixtures) { Remove-TestPfx $fixture }
}

Write-Host 'WP23 signing security tests passed.'
