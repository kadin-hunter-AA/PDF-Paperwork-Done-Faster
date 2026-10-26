param([string]$Uninstaller = (Join-Path $PSScriptRoot 'Uninstall-PerUser.ps1'))

$ErrorActionPreference = 'Stop'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sandbox = [IO.Path]::GetFullPath((Join-Path $tempBase ("PDFPaperwork-UninstallProof-" + [guid]::NewGuid().ToString('N'))))
if (-not $sandbox.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing test path outside system temp: $sandbox"
}
if (Test-Path -LiteralPath $sandbox) { throw "Refusing to overwrite test path: $sandbox" }

$install = Join-Path $sandbox 'disposable-install'
$appData = Join-Path $sandbox 'user-data'
$escape = Join-Path $sandbox 'escape-proof.txt'
$markerPath = Join-Path $install '.hunterops-pdf-editor-install.json'
New-Item -ItemType Directory -Path $install | Out-Null
New-Item -ItemType Directory -Path $appData | Out-Null
Set-Content -LiteralPath (Join-Path $install 'owned.txt') -Value 'owned package file'
Set-Content -LiteralPath (Join-Path $install 'unknown.txt') -Value 'user file'
Set-Content -LiteralPath (Join-Path $appData 'recovery.json') -Value 'user data'
Set-Content -LiteralPath $escape -Value 'outside install'

function Write-Marker([string[]]$Files) {
    [ordered]@{ Identity='HunterOpsPdfEditor'; Schema=1; Files=$Files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $markerPath -Encoding utf8
}
function Invoke-Uninstaller {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $hostExe = if (Test-Path -LiteralPath (Join-Path $PSHOME 'pwsh.exe')) { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
    $process.StartInfo.FileName = $hostExe
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $quote = { param([string]$value) '"' + $value.Replace('"', '\"') + '"' }
    $process.StartInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (& $quote $Uninstaller) +
        ' -InstallDirectory ' + (& $quote $install)
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return $process.ExitCode
}

try {
    Write-Marker @('owned.txt', '../escape-proof.txt')
    $unsafeExit = Invoke-Uninstaller
    if ($unsafeExit -eq 0) { throw 'Unsafe marker unexpectedly succeeded.' }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'owned.txt')) -or
        -not (Test-Path -LiteralPath $escape) -or
        (Get-Content -LiteralPath $escape -Raw).Trim() -ne 'outside install') {
        throw 'Unsafe marker refusal changed an owned or outside file before completing preflight.'
    }

    Write-Marker @('owned.txt')
    $normalExit = Invoke-Uninstaller
    if ($normalExit -ne 0) { throw "Valid uninstall failed with exit code $normalExit." }
    if (Test-Path -LiteralPath (Join-Path $install 'owned.txt')) { throw 'Owned package file survived a valid uninstall.' }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'unknown.txt'))) { throw 'Unknown install-folder file was removed.' }
    if ((Get-Content -LiteralPath (Join-Path $install 'unknown.txt') -Raw).Trim() -ne 'user file') { throw 'Unknown install-folder file changed.' }
    if ((Get-Content -LiteralPath (Join-Path $appData 'recovery.json') -Raw).Trim() -ne 'user data') { throw 'User data changed.' }
    if ((Get-Content -LiteralPath $escape -Raw).Trim() -ne 'outside install') { throw 'Outside file changed.' }
    Write-Output 'PASS unsafe-marker preflight is non-destructive; valid uninstall removes owned files and preserves unknown files/user data.'
}
finally {
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolvedSandbox.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing test cleanup outside system temp: $resolvedSandbox"
    }
    if (Test-Path -LiteralPath $resolvedSandbox) { Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force }
}
