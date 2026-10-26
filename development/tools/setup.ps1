$ErrorActionPreference = 'Stop'
$developmentRoot = Split-Path $PSScriptRoot -Parent
$sdkRoot = Join-Path $PSScriptRoot 'dotnet'
$nativeRoot = Join-Path $developmentRoot 'native'
New-Item -ItemType Directory -Force $nativeRoot | Out-Null
if (!(Test-Path (Join-Path $sdkRoot 'dotnet.exe'))) {
    $installer = Join-Path $PSScriptRoot ('dotnet-install-verified-' + [guid]::NewGuid().ToString('N') + '.ps1')
    # Immutable official dotnet/install-scripts commit. SHA-256 is pinned independently
    # of the download so a changed or substituted installer is never executed.
    $installerUrl = 'https://raw.githubusercontent.com/dotnet/install-scripts/e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e/src/dotnet-install.ps1'
    $installerSha256 = '3BB07BC8025211836C1E4F9D3F6A044E55B1FB6EEC518A6C78851D04E210442B'
    try {
        Invoke-WebRequest $installerUrl -OutFile $installer
        $actualInstallerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        if ($actualInstallerHash -ne $installerSha256) {
            throw "dotnet-install.ps1 identity check failed (expected $installerSha256, got $actualInstallerHash); script was not executed"
        }
        & $installer -Version '10.0.401' -InstallDir $sdkRoot -NoPath
        $installerSucceeded = $?
        if (-not $installerSucceeded) { throw 'SDK installation failed; dotnet-install.ps1 reported an error' }
    } finally {
        if (Test-Path -LiteralPath $installer -PathType Leaf) { Remove-Item -LiteralPath $installer -Force }
    }
}
$archive = Join-Path $nativeRoot 'pdfium-win-x64.tgz'
Invoke-WebRequest 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8086/pdfium-win-x64.tgz' -OutFile $archive
$expectedHash = '1FD8AF952832DBB0EB16D9249F68FE09E5F5EBF7C3DD9F6066EA2720CC28487D'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'PDFium archive checksum mismatch; extraction aborted'
}
& tar -xzf $archive -C $nativeRoot
if ($LASTEXITCODE -ne 0) { throw 'PDFium extraction failed' }
Write-Output 'Pinned SDK and PDFium are ready. Retain native/LICENSE and native/licenses when distributing.'
