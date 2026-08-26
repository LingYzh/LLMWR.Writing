[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$Publisher = 'CN=LLMW.Writing.Development',

    [string]$OutputRoot,

    [string]$SigningCertificatePath,

    [string]$SigningCertificatePassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$controlledPackageRoot = Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'
$env:NUGET_PACKAGES = $controlledPackageRoot
$nativeAudit = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng\native\LibGit2Sharp.NativeBinaries.audit.json') -Raw | ConvertFrom-Json
$controlledNativePackage = Join-Path $controlledPackageRoot 'libgit2sharp.nativebinaries\2.0.324'
$cachedNative = Join-Path $controlledNativePackage 'runtimes\win-x64\native\git2-5853918.dll'
if ((Test-Path -LiteralPath $cachedNative) -and
    ((Get-FileHash -LiteralPath $cachedNative -Algorithm SHA256).Hash -ne $nativeAudit.nativeAssets.'git2-5853918.dll')) {
    Remove-Item -LiteralPath $controlledNativePackage -Recurse -Force
}
if ([string]::IsNullOrWhiteSpace($SigningCertificatePath) -and -not [string]::IsNullOrWhiteSpace($env:LLMW_MSIX_SIGNING_CERT_PATH)) {
    $SigningCertificatePath = $env:LLMW_MSIX_SIGNING_CERT_PATH
    $SigningCertificatePassword = $env:LLMW_MSIX_SIGNING_CERT_PASSWORD
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot '.artifacts\release'
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "WP23 package output must remain inside the repository: $OutputRoot"
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
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Find-WindowsSdkTool {
    param([string]$Name)
    $roots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $roots += $env:NUGET_PACKAGES
    }
    $roots += Join-Path $repositoryRoot '.artifacts\nuget-packages-wp19-native-8099'
    $roots += Join-Path $repositoryRoot '.artifacts\nuget-packages'
    foreach ($root in $roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate.FullName }
    }
    throw "$Name was not found in the restored Windows SDK BuildTools graph. Run build.ps1 -Target Restore first."
}

function Publish-Application {
    param([string]$Project, [string]$Destination, [bool]$WindowsAppSdkSelfContained = $false)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $arguments = @(
        'publish', $Project,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $Destination,
        '--configfile', (Join-Path $repositoryRoot 'NuGet.Config'),
        '-p:PublishSingleFile=false',
        '-p:DebugType=embedded'
    )
    if ($WindowsAppSdkSelfContained) {
        $arguments += '-p:WindowsAppSDKSelfContained=true'
        $arguments += '-p:WindowsPackageType=None'
        $arguments += '-p:AppxPackage=false'
    }
    Invoke-Checked 'dotnet' $arguments
}

function New-Logo {
    param([string]$Path, [int]$Size)
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::FromArgb(36, 49, 66))
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(106, 193, 255), [Math]::Max(2, [int]($Size / 14)))
            try {
                $margin = [Math]::Max(4, [int]($Size / 6))
                $graphics.DrawRectangle($pen, $margin, $margin, $Size - (2 * $margin), $Size - (2 * $margin))
                $graphics.DrawLine($pen, $margin, [int]($Size / 2), $Size - $margin, [int]($Size / 2))
            }
            finally { $pen.Dispose() }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

[xml]$versions = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng\Versions.props') -Raw
$versionPrefix = [string]$versions.Project.PropertyGroup.VersionPrefix
$segments = $versionPrefix.Split('.')
if ($segments.Count -lt 3) { throw "VersionPrefix must contain at least three numeric components: $versionPrefix" }
$packageVersion = '{0}.{1}.{2}.0' -f $segments[0], $segments[1], $segments[2]
$artifactStem = "LLMW.Writing-$versionPrefix-$RuntimeIdentifier"

Reset-Directory $OutputRoot
$workRoot = Join-Path $repositoryRoot '.artifacts\wp23-package-work'
Reset-Directory $workRoot
$payload = Join-Path $workRoot 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null

Publish-Application (Join-Path $repositoryRoot 'src\LLMW.Writing.UI\LLMW.Writing.UI.csproj') $payload $true
Publish-Application (Join-Path $repositoryRoot 'src\LLMW.Writing.Core\LLMW.Writing.Core.csproj') (Join-Path $payload 'core')
Publish-Application (Join-Path $repositoryRoot 'src\LLMW.Writing.AgentRuntime\LLMW.Writing.AgentRuntime.csproj') (Join-Path $payload 'runtime')
Publish-Application (Join-Path $repositoryRoot 'src\LLMW.Writing.Worker\LLMW.Writing.Worker.csproj') (Join-Path $payload 'worker')
$publishedNative = Join-Path $payload 'core\git2-5853918.dll'
if (-not (Test-Path -LiteralPath $publishedNative) -or
    ((Get-FileHash -LiteralPath $publishedNative -Algorithm SHA256).Hash -ne $nativeAudit.nativeAssets.'git2-5853918.dll')) {
    throw 'The published Core payload does not contain the controlled WP19 native Git binary.'
}

$portableStage = Join-Path $workRoot 'portable'
Copy-Item -LiteralPath $payload -Destination $portableStage -Recurse
Set-Content -LiteralPath (Join-Path $portableStage 'portable.marker') -Value 'LLMW.Writing portable distribution v1' -Encoding utf8NoBOM
New-Item -ItemType Directory -Path (Join-Path $portableStage 'data') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $portableStage 'data\README.txt') -Value 'Portable application data is stored in this directory. Credentials may require re-authentication after moving machines.' -Encoding utf8NoBOM
$portableZip = Join-Path $OutputRoot "$artifactStem-portable.zip"
Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $portableZip -CompressionLevel Optimal

$msixStage = Join-Path $workRoot 'msix'
Copy-Item -LiteralPath $payload -Destination $msixStage -Recurse
$assets = Join-Path $msixStage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
New-Logo (Join-Path $assets 'StoreLogo.png') 50
New-Logo (Join-Path $assets 'Square44x44Logo.png') 44
New-Logo (Join-Path $assets 'Square150x150Logo.png') 150
$manifestTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml.template') -Raw
$manifest = $manifestTemplate.Replace('@@PUBLISHER@@', $Publisher, [StringComparison]::Ordinal).Replace('@@VERSION@@', $packageVersion, [StringComparison]::Ordinal)
Set-Content -LiteralPath (Join-Path $msixStage 'AppxManifest.xml') -Value $manifest -Encoding utf8NoBOM

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$msixPath = Join-Path $OutputRoot "$artifactStem.msix"
Invoke-Checked $makeAppx @('pack', '/d', $msixStage, '/p', $msixPath, '/o')

if (-not [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
    if (-not (Test-Path -LiteralPath $SigningCertificatePath)) {
        throw "Signing certificate not found: $SigningCertificatePath"
    }
    $signTool = Find-WindowsSdkTool 'signtool.exe'
    & $signTool sign /fd SHA256 /f $SigningCertificatePath /p $SigningCertificatePassword $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX signing failed with exit code $LASTEXITCODE. Signing arguments are redacted."
    }
}

$artifacts = @($portableZip, $msixPath) | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    [ordered]@{
        name = $item.Name
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$releaseManifest = [ordered]@{
    schemaVersion = 1
    productVersion = $versionPrefix
    packageVersion = $packageVersion
    runtimeIdentifier = $RuntimeIdentifier
    publisher = $Publisher
    signed = -not [string]::IsNullOrWhiteSpace($SigningCertificatePath)
    artifacts = $artifacts
}
$releaseManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'release-manifest.json') -Encoding utf8NoBOM

Write-Host "MSIX: $msixPath"
Write-Host "Portable: $portableZip"
Write-Host "Release manifest: $(Join-Path $OutputRoot 'release-manifest.json')"
