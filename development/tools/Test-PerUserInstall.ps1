param(
    [string]$Installer = (Join-Path $PSScriptRoot 'Install-PerUser.ps1'),
    [string]$Uninstaller = (Join-Path $PSScriptRoot 'Uninstall-PerUser.ps1')
)
$ErrorActionPreference = 'Stop'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$sandbox = Join-Path $tempBase ('PDFPaperwork-InstallProof-' + [guid]::NewGuid().ToString('N'))
if (-not $sandbox.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $sandbox)) { throw "Unsafe or pre-existing disposable root: $sandbox" }
$payload = Join-Path $sandbox 'payload'
$archive = Join-Path $sandbox 'package.zip'
$sidecar = $archive + '.sha256'
$programs = Join-Path $sandbox 'programs'
$install = Join-Path $programs 'PDF - Paperwork Done Faster'

function Quote-Argument([string]$Value) { '"' + $Value.Replace('"', '\"') + '"' }
function Invoke-Script([string]$Path, [string[]]$Arguments) {
    $p = [Diagnostics.Process]::new(); $p.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $p.StartInfo.FileName = if (Test-Path -LiteralPath (Join-Path $PSHOME 'pwsh.exe')) { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
    $p.StartInfo.UseShellExecute = $false; $p.StartInfo.CreateNoWindow = $true
    $p.StartInfo.RedirectStandardOutput = $true; $p.StartInfo.RedirectStandardError = $true
    $all = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$Path) + $Arguments
    $p.StartInfo.Arguments = ($all | ForEach-Object { Quote-Argument $_ }) -join ' '
    [void]$p.Start(); $out = $p.StandardOutput.ReadToEnd(); $err = $p.StandardError.ReadToEnd(); $p.WaitForExit()
    [pscustomobject]@{ ExitCode=$p.ExitCode; Output=$out; Error=$err }
}
function Assert-RefusedBeforeStage($Result, [string]$Reason) {
    if ($Result.ExitCode -eq 0) { throw "$Reason unexpectedly succeeded." }
    if (Test-Path -LiteralPath $programs) { throw "$Reason created an install or staging root." }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw "$Reason modified the source archive." }
}
function New-Archive([string]$Path, [string]$EntryName, [string]$Contents, [int]$ExternalAttributes = 0) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $entry = $zip.CreateEntry($EntryName)
            if ($ExternalAttributes -ne 0) { $entry.ExternalAttributes = $ExternalAttributes }
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($Contents) } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
try {
    Add-Type -AssemblyName System.IO.Compression
    New-Item -ItemType Directory -Path $payload | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'App.Desktop.exe') -Value 'disposable fake executable'
    Copy-Item -LiteralPath $Uninstaller -Destination (Join-Path $payload 'Uninstall-PerUser.ps1')
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

    Set-Content -LiteralPath $sidecar -Value ('0' * 64)
    Assert-RefusedBeforeStage (Invoke-Script $Installer @('-PackageArchive',$archive,'-InstallRoot',$programs,'-NoShortcut')) 'Bad checksum'
    Set-Content -LiteralPath $sidecar -Value 'not a checksum'
    Assert-RefusedBeforeStage (Invoke-Script $Installer @('-PackageArchive',$archive,'-InstallRoot',$programs,'-NoShortcut')) 'Malformed sidecar'

    $traversalArchive = Join-Path $sandbox 'traversal.zip'
    $traversalHashFile = $traversalArchive + '.sha256'
    $escapeName = 'PDFPaperwork-ZipEscape-' + [guid]::NewGuid().ToString('N') + '.txt'
    $escapePath = Join-Path ([IO.Path]::GetTempPath()) $escapeName
    if (Test-Path -LiteralPath $escapePath) { throw "Unexpected pre-existing escape probe: $escapePath" }
    New-Archive $traversalArchive ("../" + $escapeName) 'test traversal payload'
    Set-Content -LiteralPath $traversalHashFile -Value (Get-FileHash -LiteralPath $traversalArchive -Algorithm SHA256).Hash
    $traversalRefused = Invoke-Script $Installer @('-PackageArchive',$traversalArchive,'-ChecksumFile',$traversalHashFile,'-InstallRoot',$programs,'-NoShortcut')
    if ($traversalRefused.ExitCode -eq 0 -or ($traversalRefused.Error + $traversalRefused.Output) -notmatch 'Unsafe archive entry' -or (Test-Path -LiteralPath $programs) -or (Test-Path -LiteralPath $escapePath)) { throw 'Checksummed traversal ZIP was not refused safely before staging/extraction escape.' }

    $linkArchive = Join-Path $sandbox 'unix-link.zip'
    $linkHashFile = $linkArchive + '.sha256'
    New-Archive $linkArchive 'linked-file' 'target.txt' (0xA1FF -shl 16)
    Set-Content -LiteralPath $linkHashFile -Value (Get-FileHash -LiteralPath $linkArchive -Algorithm SHA256).Hash
    $linkRefused = Invoke-Script $Installer @('-PackageArchive',$linkArchive,'-ChecksumFile',$linkHashFile,'-InstallRoot',$programs,'-NoShortcut')
    if ($linkRefused.ExitCode -eq 0 -or ($linkRefused.Error + $linkRefused.Output) -notmatch 'Archive link entry is not allowed' -or (Test-Path -LiteralPath $programs)) { throw 'Checksummed Unix symlink ZIP entry was not refused before staging.' }

    Set-Content -LiteralPath $sidecar -Value $archiveHash
    $valid = Invoke-Script $Installer @('-PackageArchive',$archive,'-InstallRoot',$programs,'-NoShortcut')
    if ($valid.ExitCode -ne 0) { throw "Valid archive install failed: $($valid.Error) $($valid.Output)" }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'App.Desktop.exe'))) { throw 'Archive payload was not installed.' }
    if (Test-Path -LiteralPath (Join-Path $install 'package.zip')) { throw 'The archive itself was copied into the installation.' }
    Set-Content -LiteralPath (Join-Path $install 'user-note.txt') -Value 'preserve me'
    $repeat = Invoke-Script $Installer @('-PackageArchive',$archive,'-InstallRoot',$programs,'-NoShortcut')
    if ($repeat.ExitCode -eq 0 -or (Get-Content -LiteralPath (Join-Path $install 'user-note.txt') -Raw).Trim() -ne 'preserve me') { throw 'Existing install refusal was not source-preserving.' }
    $removed = Invoke-Script $Uninstaller @('-InstallDirectory',$install,'-StartMenuDirectory',(Join-Path $sandbox 'start-menu'))
    if ($removed.ExitCode -ne 0) { throw "Disposable uninstall failed: $($removed.Error) $($removed.Output)" }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'user-note.txt'))) { throw 'Uninstall removed an unknown file.' }

    $directoryPrograms = Join-Path $sandbox 'directory-programs'
    $directoryInstall = Join-Path $directoryPrograms 'PDF - Paperwork Done Faster'
    $directoryResult = Invoke-Script $Installer @('-PackageDirectory',$payload,'-InstallRoot',$directoryPrograms,'-NoShortcut')
    if ($directoryResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $directoryInstall 'App.Desktop.exe'))) { throw "Existing directory-source API smoke install failed: $($directoryResult.Error) $($directoryResult.Output)" }
    $directoryRemove = Invoke-Script $Uninstaller @('-InstallDirectory',$directoryInstall,'-StartMenuDirectory',(Join-Path $sandbox 'directory-start-menu'))
    if ($directoryRemove.ExitCode -ne 0) { throw "Directory-source smoke uninstall failed: $($directoryRemove.Error) $($directoryRemove.Output)" }

    $v1 = Join-Path $sandbox 'v1'; $v2 = Join-Path $sandbox 'v2'
    $versionPrograms = Join-Path $sandbox 'versioned-programs'
    $versionInstall = Join-Path $versionPrograms 'PDF - Paperwork Done Faster'
    foreach ($dir in @($v1,$v2)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
        Copy-Item -LiteralPath $Uninstaller -Destination (Join-Path $dir 'Uninstall-PerUser.ps1')
    }
    Set-Content -LiteralPath (Join-Path $v1 'App.Desktop.exe') -Value 'version one'
    Set-Content -LiteralPath (Join-Path $v1 'runtime.dat') -Value 'runtime'
    Set-Content -LiteralPath (Join-Path $v2 'App.Desktop.exe') -Value 'version two'
    Set-Content -LiteralPath (Join-Path $v2 'runtime.dat') -Value 'runtime'
    Set-Content -LiteralPath (Join-Path $v2 'new-runtime.dat') -Value 'new runtime'
    $v1Install = Invoke-Script $Installer @('-PackageDirectory',$v1,'-InstallRoot',$versionPrograms,'-NoShortcut','-PackageVersion','1.0.0')
    if ($v1Install.ExitCode -ne 0) { throw "Versioned install failed: $($v1Install.Error) $($v1Install.Output)" }
    Remove-Item -LiteralPath (Join-Path $versionInstall 'runtime.dat')
    $repair = Invoke-Script $Installer @('-PackageDirectory',$v1,'-InstallRoot',$versionPrograms,'-NoShortcut','-PackageVersion','1.0.0','-Repair')
    if ($repair.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $versionInstall 'runtime.dat'))) { throw "Repair failed to restore a missing tracked file: $($repair.Error) $($repair.Output)" }
    $upgrade = Invoke-Script $Installer @('-PackageDirectory',$v2,'-InstallRoot',$versionPrograms,'-NoShortcut','-PackageVersion','2.0.0','-Upgrade')
    if ($upgrade.ExitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $versionInstall 'App.Desktop.exe') -Raw).Trim() -ne 'version two' -or -not (Test-Path -LiteralPath (Join-Path $versionInstall 'new-runtime.dat'))) { throw "Upgrade failed: $($upgrade.Error) $($upgrade.Output)" }
    $unexpected = Join-Path $versionInstall 'user-data.txt'
    Set-Content -LiteralPath $unexpected -Value 'keep me'
    $refusedUpgrade = Invoke-Script $Installer @('-PackageDirectory',$v1,'-InstallRoot',$versionPrograms,'-NoShortcut','-PackageVersion','3.0.0','-Upgrade')
    if ($refusedUpgrade.ExitCode -eq 0 -or (Get-Content -LiteralPath $unexpected -Raw).Trim() -ne 'keep me' -or (Get-Content -LiteralPath (Join-Path $versionInstall 'App.Desktop.exe') -Raw).Trim() -ne 'version two') { throw 'Unexpected-file upgrade refusal was not source-preserving.' }
    Write-Output 'PASS checksums and unsafe ZIP entries refused; install, missing-file repair, newer-version upgrade, and conservative refusal paths verified in disposable directories.'
} finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or $resolved -notmatch 'PDFPaperwork-InstallProof-[0-9a-f]{32}$') { throw "Refusing cleanup outside owned disposable root: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
