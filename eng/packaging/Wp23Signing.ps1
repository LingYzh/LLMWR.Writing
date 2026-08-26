Set-StrictMode -Version Latest

function New-Wp23SignToolSignArguments {
    param(
        [Parameter(Mandatory)]
        [string]$CertificateThumbprint,

        [Parameter(Mandatory)]
        [string]$MsixPath
    )

    return @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My', $MsixPath)
}

function Invoke-Wp23MsixSigning {
    param(
        [Parameter(Mandatory)]
        [string]$MsixPath,

        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$PfxPath,

        [Parameter(Mandatory)]
        [System.Security.SecureString]$PfxPassword,

        [Parameter(Mandatory)]
        [string]$SignToolPath
    )

    [xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
    $publisher = [string]$manifest.Package.Identity.Publisher
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        throw 'The package manifest Publisher is missing.'
    }

    $pfxThumbprints = @()
    $preExistingThumbprints = @()
    try {
        $pfxData = Get-PfxData -FilePath $PfxPath -Password $PfxPassword
        $pfxThumbprints = @(@($pfxData.EndEntityCertificates) + @($pfxData.OtherCertificates) |
            Where-Object { $null -ne $_ } |
            ForEach-Object { $_.Thumbprint } |
            Sort-Object -Unique)
        if ($pfxThumbprints.Count -eq 0) {
            throw 'The signing PFX contains no certificate.'
        }

        $preExistingThumbprints = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' |
            Where-Object { $_.Thumbprint -in $pfxThumbprints } |
            ForEach-Object { $_.Thumbprint })

        try {
            $imported = @(Import-PfxCertificate `
                -FilePath $PfxPath `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -Password $PfxPassword `
                -Exportable:$false)
        }
        catch {
            throw 'The signing PFX could not be imported into the CurrentUser certificate store.'
        }
        finally {
            $PfxPassword.Dispose()
            $PfxPassword = $null
        }

        $candidate = $imported |
            Where-Object { [StringComparer]::Ordinal.Equals($_.Subject, $publisher) } |
            Select-Object -First 1
        if ($null -eq $candidate) {
            throw 'The signing certificate Subject does not exactly match the package Publisher.'
        }
        if (-not $candidate.HasPrivateKey) {
            throw 'The signing certificate does not have a private key.'
        }

        $codeSigningOid = '1.3.6.1.5.5.7.3.3'
        $hasCodeSigningEku = @($candidate.Extensions |
            Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } |
            Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
        if (-not $hasCodeSigningEku) {
            throw 'The signing certificate does not permit Code Signing.'
        }

        $now = [DateTime]::Now
        if ($candidate.NotBefore -gt $now -or $candidate.NotAfter -lt $now) {
            throw 'The signing certificate is not currently valid.'
        }

        $signArguments = New-Wp23SignToolSignArguments `
            -CertificateThumbprint $candidate.Thumbprint `
            -MsixPath $MsixPath
        & $SignToolPath @signArguments
        if ($LASTEXITCODE -ne 0) {
            throw "MSIX signing failed with exit code $LASTEXITCODE. Signing arguments contain no credential."
        }

        & $SignToolPath verify /pa /v $MsixPath
        if ($LASTEXITCODE -ne 0) {
            throw "MSIX signature verification failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        if ($null -ne $PfxPassword) {
            $PfxPassword.Dispose()
            $PfxPassword = $null
        }

        foreach ($thumbprint in $pfxThumbprints) {
            if ($thumbprint -in $preExistingThumbprints) {
                continue
            }

            $introduced = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
            if ($null -ne $introduced) {
                Remove-Item -LiteralPath $introduced.PSPath -Force
            }
        }
    }
}
