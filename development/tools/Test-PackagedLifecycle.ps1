param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$PreviousPackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$previous = (Resolve-Path -LiteralPath $PreviousPackageDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Evidence destination already exists: $output" }
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$sandbox = [IO.Path]::GetFullPath((Join-Path $tempBase ('PDFPaperwork-PackageLifecycle-' + [guid]::NewGuid().ToString('N'))))
if (-not $sandbox.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $sandbox)) { throw 'Unsafe disposable root.' }
$programs = Join-Path $sandbox 'programs'
$startMenu = Join-Path $sandbox 'start-menu'
$install = Join-Path $programs 'PDF - Paperwork Done Faster'
$shortcut = Join-Path $startMenu 'PDF - Paperwork Done Faster.lnk'
$checks = [Collections.Generic.List[string]]::new()
$hostExe = (Get-Process -Id $PID).Path
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $checks.Add($Message)
}
function Invoke-Installer([string]$Source, [string]$Mode = '') {
    $installer = Join-Path $Source 'Install-PerUser.ps1'
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$installer,'-PackageArchive',($Source + '.zip'),'-ChecksumFile',($Source + '.zip.sha256'),'-InstallRoot',$programs,'-StartMenuDirectory',$startMenu)
    if ($Mode) { $arguments += $Mode }
    $result = & $hostExe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Installer failed: $result" }
}
function Assert-Inventory([string]$Source) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Source + '.zip')
    try {
        $entries = @($zip.Entries | Where-Object { $_.Name -ne '' -and $_.FullName -ne 'Install-PerUser.ps1' })
        $expected = @($entries | ForEach-Object { if ($_.FullName -eq 'Uninstall-PerUser.ps1') { 'Uninstall.ps1' } else { $_.FullName.Replace('/','\') } }) + @('.hunterops-pdf-editor-install.json')
        $actual = @(Get-ChildItem -LiteralPath $install -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($install, $_.FullName) })
        if (@(Compare-Object ($expected | Sort-Object -Unique) ($actual | Sort-Object -Unique)).Count -ne 0) { throw 'Installed inventory contains missing or unexpected files.' }
        foreach ($entry in $entries) {
            $relative = if ($entry.FullName -eq 'Uninstall-PerUser.ps1') { 'Uninstall.ps1' } else { $entry.FullName.Replace('/','\') }
            $destination = Join-Path $install $relative
            $stream = $entry.Open()
            try { $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expectedHash) { throw "Installed payload differs from ZIP: $relative" }
        }
        Check $true "All $($entries.Count) installed payload files and the uninstaller match exact ZIP bytes, with no stale extras."
    } finally { $zip.Dispose() }
}
New-Item -ItemType Directory -Path $output | Out-Null
New-Item -ItemType Directory -Path $sandbox | Out-Null
$desktop = $null
try {
    $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $previous 'App.Desktop.exe')).FileVersion
    $newVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'App.Desktop.exe')).FileVersion
    Check ([version]$newVersion -gt [version]$oldVersion) 'The actual upgrade package has a newer executable version.'
    Invoke-Installer $previous
    Assert-Inventory $previous
    Check (Test-Path -LiteralPath $shortcut -PathType Leaf) 'Fresh install creates its disposable Start shortcut.'
    Invoke-Installer $package '-Upgrade'
    Assert-Inventory $package
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    Check ($link.TargetPath -eq (Join-Path $install 'App.Desktop.exe')) 'The installed shortcut targets the installed desktop executable.'
    Check ($link.WorkingDirectory -eq $install) 'The installed shortcut uses the installed working directory.'
    $repairTarget = Join-Path $install 'worker\App.Worker.dll'
    Check (Test-Path -LiteralPath $repairTarget -PathType Leaf) 'The installed native worker payload exists before repair.'
    Remove-Item -LiteralPath $repairTarget
    Invoke-Installer $package '-Repair'
    Assert-Inventory $package
    $development = Split-Path -Parent $PSScriptRoot
    # Execute the byte-identical harness from the disposable local tree rather
    # than asking the installed interpreter to execute a synced reparse file.
    $verifierSource = Join-Path $PSScriptRoot 'verify-completion-package.py'
    $verifierCopy = Join-Path $sandbox 'verify-completion-package.py'
    $verifierBytes = [IO.File]::ReadAllBytes($verifierSource)
    [IO.File]::WriteAllBytes($verifierCopy, $verifierBytes)
    $verifierHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($verifierBytes)).ToLowerInvariant()
    Check ((Get-FileHash -LiteralPath $verifierCopy -Algorithm SHA256).Hash.ToLowerInvariant() -eq $verifierHash) 'The isolated runtime verifier matches the retained source bytes.'
    & (Join-Path $install 'advanced\python.exe') -I -B $verifierCopy $development $install (Join-Path $output 'installed-runtime')
    if ($LASTEXITCODE -ne 0) { throw 'The exact installed runtime failed its completion smoke.' }
    Check $true 'The repaired installed worker and companion pass the completion-package smoke.'
    $desktop = Start-Process -FilePath (Join-Path $install 'App.Desktop.exe') -WorkingDirectory $install -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $desktop.Refresh()
    Check (-not $desktop.HasExited) 'The exact installed desktop remains alive for a three-second hidden host launch.'
    $desktop.Kill(); $desktop.WaitForExit(); $desktop.Dispose(); $desktop = $null
    $unknown = Join-Path $install 'user-owned-proof.txt'
    [IO.File]::WriteAllText($unknown, 'preserve user-owned content')
    $data = Join-Path $sandbox 'user-data\recovery-proof.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $data) | Out-Null
    [IO.File]::WriteAllText($data, 'preserve recovery content')
    $removeOutput = & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $install 'Uninstall.ps1') -InstallDirectory $install -StartMenuDirectory $startMenu 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Uninstall failed: $removeOutput" }
    Check (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall removes the matching installed shortcut.'
    Check (-not (Test-Path -LiteralPath (Join-Path $install 'App.Desktop.exe'))) 'Uninstall removes the owned desktop payload.'
    Check (([IO.File]::ReadAllText($unknown)) -eq 'preserve user-owned content') 'Uninstall preserves an unknown file inside the install directory.'
    Check (([IO.File]::ReadAllText($data)) -eq 'preserve recovery content') 'Uninstall preserves data outside the install directory.'
    Check (@(Get-ChildItem -LiteralPath $install -Recurse -File).Count -eq 1) 'Only the deliberately unowned file remains inside the install directory.'
    [ordered]@{
        Passed = $checks; Package = $package; PreviousPackage = $previous
        OldVersion = $oldVersion; NewVersion = $newVersion
        VerifierSHA256 = $verifierHash
        InstallSource = 'Checksummed immutable ZIP archives; each version uses its own shipped installer.'
        OS = [Environment]::OSVersion.VersionString
        Scope = 'Exact-package disposable host install/upgrade/repair/shortcut/uninstall and hidden launch. No clean-machine, observed window, physical input, or device acceptance.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    Write-Output "PASS $($checks.Count) packaged lifecycle checks. Evidence: $output"
}
catch {
    [ordered]@{ Status='FAIL'; Error=$_.Exception.Message; PassedBeforeFailure=$checks;
        Package=$package; PreviousPackage=$previous; DisposableRoot=$sandbox;
        Scope='Incomplete host lifecycle attempt; not acceptance.' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding utf8
    throw
}
finally {
    if ($null -ne $desktop) { if (-not $desktop.HasExited) { $desktop.Kill(); $desktop.WaitForExit() }; $desktop.Dispose() }
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or $resolved -notmatch 'PDFPaperwork-PackageLifecycle-[0-9a-f]{32}$') { throw "Unsafe cleanup target: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
