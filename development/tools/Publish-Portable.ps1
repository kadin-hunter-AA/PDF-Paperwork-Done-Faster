param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\portable-win-x64'),
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '1.0.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Join-Path $projectRoot 'tools\dotnet\dotnet.exe'
$desktopProject = Join-Path $projectRoot 'App.Desktop\App.Desktop.csproj'
$workerProject = Join-Path $projectRoot 'App.Worker\App.Worker.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$zipPath = "$output.zip"
if (Test-Path -LiteralPath $output) {
    if ((Get-ChildItem -LiteralPath $output -Force | Measure-Object).Count -gt 0) {
        throw "Output directory already exists and is not empty: $output. Choose a fresh versioned path."
    }
} else { New-Item -ItemType Directory -Path $output -Force | Out-Null }
if (Test-Path -LiteralPath $zipPath) { throw "ZIP already exists. Choose a fresh output path: $zipPath" }
if (Test-Path -LiteralPath "$zipPath.sha256") { throw "ZIP checksum already exists. Choose a fresh output path: $zipPath.sha256" }
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { throw "Bundled SDK is missing: $sdk" }
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "PackageVersion must be numeric (major.minor.patch with an optional fourth component): $PackageVersion" }

& $sdk publish $workerProject --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:Version=$PackageVersion -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishDir="$output\worker\"
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed with exit code $LASTEXITCODE" }

& $sdk publish $desktopProject --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:Version=$PackageVersion -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishDir="$output\"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$required = @(
    (Join-Path $output 'App.Desktop.exe'),
    (Join-Path $output 'worker\App.Worker.exe'),
    (Join-Path $output 'worker\pdfium.dll'),
    (Join-Path $output 'advanced\python.exe'),
    (Join-Path $output 'advanced\worker.py'),
    (Join-Path $output 'advanced\forms.py'),
    (Join-Path $output 'advanced\ocr.py'),
    (Join-Path $output 'advanced\signing.py'),
    (Join-Path $output 'advanced\redaction.py'),
    (Join-Path $output 'advanced\structured_text.py'),
    (Join-Path $output 'advanced\tessdata\eng.traineddata'),
    (Join-Path $output 'advanced\tessdata\LICENSE')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Portable package is missing required runtime file: $path" }
}
$advanced = Join-Path $output 'advanced'
$runtimeInventory = Get-Content -LiteralPath (Join-Path $advanced 'runtime-inventory.json') -Raw | ConvertFrom-Json
$advancedPrefix = [IO.Path]::GetFullPath($advanced).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($entry in $runtimeInventory.Files) {
    $payload = [IO.Path]::GetFullPath((Join-Path $advanced $entry.Path))
    if (-not $payload.StartsWith($advancedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $payload -PathType Leaf) -or
        (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.SHA256) {
        throw 'Packaged companion runtime does not match its pinned file inventory.'
    }
}
& (Join-Path $advanced 'python.exe') -I -B -c 'import sys,pypdf,pymupdf,cryptography; from pyhanko.sign import signers; assert sys.version_info[:3] == (3,14,8); assert pypdf.__version__ == "6.1.3"; assert pymupdf.VersionBind == "1.28.2"; print("Packaged isolated companion imports passed")'
if ($LASTEXITCODE -ne 0) { throw 'Packaged companion-runtime smoke failed.' }

$native = Join-Path $projectRoot 'native'
$licenseOutput = Join-Path $output 'licenses\pdfium'
$dotnetLicenseOutput = Join-Path $output 'licenses\dotnet'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
New-Item -ItemType Directory -Path $dotnetLicenseOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $native 'LICENSE') -Destination $licenseOutput
Copy-Item -LiteralPath (Join-Path $native 'VERSION') -Destination $licenseOutput
Copy-Item -Path (Join-Path $native 'licenses\*') -Destination $licenseOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\dotnet\LICENSE.txt') -Destination $dotnetLicenseOutput
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\dotnet\ThirdPartyNotices.txt') -Destination $dotnetLicenseOutput
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-PerUser.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-PerUser.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE-README.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PER-USER-INSTALL.md') -Destination $output
foreach ($name in @('BUILD-STATUS.md','CAPABILITIES.md','COMPLETION-TRACKER.md','REDISTRIBUTION-REVIEW.md','ADVANCED-RUNTIME-NOTICES.md','RELEASE-ACCEPTANCE.md')) {
    $sourceDoc = Join-Path $projectRoot "docs\$name"
    if (-not (Test-Path -LiteralPath $sourceDoc -PathType Leaf)) { throw "Release documentation is missing: $sourceDoc" }
    Copy-Item -LiteralPath $sourceDoc -Destination $output
}

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "Portable ZIP was not created: $zipPath" }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$zipHash  $(Split-Path $zipPath -Leaf)" -Encoding ascii

Write-Output "Portable Windows x64 build verified at $output"
Write-Output "ZIP package created at $zipPath"
Write-Output "SHA-256 checksum created at $zipPath.sha256"
Write-Output "Package version: $PackageVersion"
Write-Output 'This is a portable ZIP, not a compiled installer or MSIX package.'
