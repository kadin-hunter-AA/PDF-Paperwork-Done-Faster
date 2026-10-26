param(
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$PackageArchive,
    [string]$ChecksumFile,
    [string]$InstallRoot,
    [string]$StartMenuDirectory,
    [switch]$NoShortcut,
    [switch]$Upgrade,
    [switch]$Repair,
    [string]$PackageVersion,
    # Adds this app to Explorer's "Open with" list for PDFs. Windows 11 lets only the user choose the default app.
    [switch]$AssociatePdf,
    [string]$RegistryRoot = 'HKCU:\Software\Classes'
)

$ErrorActionPreference = 'Stop'
$archiveTemp = $null
try {
    if ($Upgrade -and $Repair) { throw 'Choose either -Upgrade or -Repair.' }
    if ($PackageArchive) {
        $PackageArchive = [IO.Path]::GetFullPath($PackageArchive)
        if (-not (Test-Path -LiteralPath $PackageArchive -PathType Leaf)) { throw "Package archive was not found: $PackageArchive" }
        $sidecar = if ($ChecksumFile) { [IO.Path]::GetFullPath($ChecksumFile) } else { $PackageArchive + '.sha256' }
        if ($ChecksumFile -and -not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Checksum file was not found: $sidecar" }
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
            $text = (Get-Content -LiteralPath $sidecar -Raw).Trim()
            if ($text -notmatch '^([A-Fa-f0-9]{64})(?:\s+\*?.*)?$') { throw "Invalid SHA-256 sidecar; package was not staged: $sidecar" }
            $actual = (Get-FileHash -LiteralPath $PackageArchive -Algorithm SHA256).Hash
            if ($actual -ne $Matches[1].ToUpperInvariant()) { throw "Package ZIP checksum mismatch; package was not staged: $PackageArchive" }
        }

        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($PackageArchive)
        try {
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.Replace('\','/')
                if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name -match '^[A-Za-z]:' -or $name -match '(^|/)\.\.?(/|$)') {
                    throw "Unsafe archive entry; extraction aborted: $($entry.FullName)"
                }
                if (((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000)) { throw "Archive link entry is not allowed: $($entry.FullName)" }
            }
        } finally { $zip.Dispose() }

        $archiveTemp = Join-Path ([IO.Path]::GetTempPath()) ('PDFPaperwork-Extract-' + [guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $archiveTemp) { throw "Refusing existing extraction directory: $archiveTemp" }
        New-Item -ItemType Directory -Path $archiveTemp | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($PackageArchive, $archiveTemp)
        $PackageDirectory = $archiveTemp
    }

    $source = [IO.Path]::GetFullPath($PackageDirectory)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Package directory was not found: $source" }
    $exe = Join-Path $source 'App.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "App.Desktop.exe was not found in $source" }

    $programs = if ($InstallRoot) { [IO.Path]::GetFullPath($InstallRoot) } else { Join-Path $env:LOCALAPPDATA 'Programs' }
    $productFolder = 'PDF - Paperwork Done Faster'
    $install = Join-Path $programs $productFolder
    $markerName = '.hunterops-pdf-editor-install.json'
    $startMenu = if ($StartMenuDirectory) { [IO.Path]::GetFullPath($StartMenuDirectory) } else { Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs' }
    if (-not $NoShortcut -and $InstallRoot -and -not $StartMenuDirectory) { throw 'When using a custom InstallRoot, specify a custom StartMenuDirectory or use -NoShortcut.' }
    $shortcutPath = Join-Path $startMenu "$productFolder.lnk"
    if (-not $PackageVersion) {
        $PackageVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
        if (-not $PackageVersion) { $PackageVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion }
        if (-not $PackageVersion) { $PackageVersion = '0.0.0' }
    }
    if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "PackageVersion must be a numeric version such as 1.2.3: $PackageVersion" }
    $incomingVersion = [version]$PackageVersion
    $existingMarker = Join-Path $install '.hunterops-pdf-editor-install.json'
    $isExisting = Test-Path -LiteralPath $install
    if ($isExisting -and -not ($Upgrade -or $Repair)) {
        throw "The destination already exists. Use -Upgrade or -Repair for a verified schema-2 installation: $install"
    }
    if ($isExisting) {
        $installItem = Get-Item -LiteralPath $install -Force
        if (($installItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Install root is a reparse point; no files were changed: $install" }
        $running = Get-Process -Name 'App.Desktop' -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath((Join-Path $install 'App.Desktop.exe')) } catch { $false } }
        if ($running) { throw 'The installed app is running. Close it before installing.' }
        if (-not (Test-Path -LiteralPath $existingMarker -PathType Leaf)) { throw "No supported ownership manifest exists. Existing legacy or unmanaged install was left untouched: $install" }
        $old = Get-Content -LiteralPath $existingMarker -Raw | ConvertFrom-Json
        if ($old.Identity -ne 'HunterOpsPdfEditor' -or $old.Schema -ne 2 -or $old.Files -isnot [array] -or $old.Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
            throw "Unsupported or legacy install manifest. No files were changed: $existingMarker"
        }
        $oldVersion = [version]$old.Version
        if ($Upgrade -and $incomingVersion -le $oldVersion) { throw "Upgrade package version $incomingVersion must be newer than installed version $oldVersion." }
        if ($Repair -and $incomingVersion -ne $oldVersion) { throw "Repair package version $incomingVersion must match installed version $oldVersion; use -Upgrade for a newer version." }
        $oldMap = @{}
        foreach ($record in $old.Files) {
            if ($record.Path -isnot [string] -or $record.Path -match '(^|[\\/])\.{1,2}([\\/]|$)|:' -or [IO.Path]::IsPathRooted($record.Path) -or $record.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "Unsafe or invalid installed manifest record: $($record | ConvertTo-Json -Compress)" }
            $relative = $record.Path.Replace('/','\')
            if ($oldMap.ContainsKey($relative)) { throw "Duplicate path in installed manifest: $relative" }
            $oldMap[$relative] = $record.Sha256.ToUpperInvariant()
        }
        $installPrefix = [IO.Path]::GetFullPath($install).TrimEnd('\') + '\'
        $actualItems = @(Get-ChildItem -LiteralPath $install -Force -Recurse)
        foreach ($item in $actualItems) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in install tree; no changes made: $($item.FullName)" }
        }
        $actualFiles = @($actualItems | Where-Object { -not $_.PSIsContainer })
        foreach ($file in $actualFiles) {
            $full = [IO.Path]::GetFullPath($file.FullName)
            if (-not $full.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected file escaped install root: $full" }
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in install tree; no changes made: $full" }
            $rel = $full.Substring($installPrefix.Length)
            if ($rel -eq '.hunterops-pdf-editor-install.json') { continue }
            if (-not $oldMap.ContainsKey($rel)) { throw "Unexpected user or untracked file in install; no changes made: $full" }
            $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
            if ($hash -ne $oldMap[$rel]) { throw "Tracked file was modified; no changes made: $full" }
        }
        foreach ($rel in $oldMap.Keys) {
            $candidate = Join-Path $install $rel
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -and -not $Repair) { throw "Tracked file is missing; use -Repair to restore it: $candidate" }
        }
        if ($Repair -and $old.Version -ne $PackageVersion) { throw 'Repair version must match exactly.' }
    }
    if (-not $NoShortcut -and (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
        $existing = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
        $intended = [IO.Path]::GetFullPath((Join-Path $install 'App.Desktop.exe'))
        if (-not [string]::Equals([IO.Path]::GetFullPath($existing.TargetPath), $intended, [StringComparison]::OrdinalIgnoreCase)) { throw "A Start menu shortcut with this name already points elsewhere: $shortcutPath" }
    }

    New-Item -ItemType Directory -Path $programs -Force | Out-Null
    $stage = Join-Path $programs ('.hunterops-pdf-editor-staging-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        foreach ($item in (Get-ChildItem -LiteralPath $source -Force | Where-Object Name -notin @('Install-PerUser.ps1','Uninstall-PerUser.ps1'))) {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $stage $item.Name) -Recurse -Force
        }
        Copy-Item -LiteralPath (Join-Path $source 'Uninstall-PerUser.ps1') -Destination (Join-Path $stage 'Uninstall.ps1') -Force
        $stagePrefix = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
        $ownedFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
            $full = [IO.Path]::GetFullPath($_.FullName)
            if (-not $full.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Staged file escaped the install folder: $full" }
            $relative = $full.Substring($stagePrefix.Length).Replace('\','/')
            [ordered]@{ Path=$relative; Sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
        } | Sort-Object Path)
        if ($Repair) {
            $newPaths = @($ownedFiles | ForEach-Object { $_.Path.Replace('/','\') } | Sort-Object -Unique)
            $oldPaths = @($oldMap.Keys | Sort-Object)
            if (($newPaths -join "`n") -cne ($oldPaths -join "`n")) { throw 'Repair package file inventory differs from the installed manifest; no files were changed.' }
            foreach ($record in $ownedFiles) {
                $rel = $record.Path.Replace('/','\')
                if ($oldMap[$rel] -ne $record.Sha256.ToUpperInvariant()) { throw "Repair package content differs for tracked file: $rel" }
            }
        }
        [ordered]@{ Identity='HunterOpsPdfEditor'; Schema=2; Version=$PackageVersion; Files=$ownedFiles } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage $markerName) -Encoding utf8
        if (-not $isExisting) {
            Move-Item -LiteralPath $stage -Destination $install
        } elseif ($Repair) {
            foreach ($record in $ownedFiles) {
                $target = Join-Path $install ($record.Path.Replace('/','\'))
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    $parent = Split-Path $target -Parent
                    New-Item -ItemType Directory -Path $parent -Force | Out-Null
                    Copy-Item -LiteralPath (Join-Path $stage ($record.Path.Replace('/','\'))) -Destination $target
                }
            }
            Copy-Item -LiteralPath (Join-Path $stage $markerName) -Destination $existingMarker -Force
        } else {
            # Preflight above proved the old tree contains only intact package-owned files.
            # Move it aside, swap in the prepared tree, and roll back if the second move fails.
            $backup = Join-Path $programs ('.hunterops-pdf-editor-upgrade-' + [guid]::NewGuid().ToString('N'))
            if (Test-Path -LiteralPath $backup) { throw "Refusing unexpected upgrade backup path: $backup" }
            Move-Item -LiteralPath $install -Destination $backup
            try { Move-Item -LiteralPath $stage -Destination $install }
            catch { Move-Item -LiteralPath $backup -Destination $install; throw }
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    } catch {
        if (Test-Path -LiteralPath $stage) {
            $stageFull = [IO.Path]::GetFullPath($stage)
            $programsPrefix = [IO.Path]::GetFullPath($programs).TrimEnd('\') + '\'
            if (-not $stageFull.StartsWith($programsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing unexpected staging cleanup path: $stageFull" }
            Remove-Item -LiteralPath $stageFull -Recurse -Force
        }
        throw
    }

    if (-not $NoShortcut) {
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $install 'App.Desktop.exe'
        $shortcut.WorkingDirectory = $install
        $shortcut.IconLocation = "$($shortcut.TargetPath),0"
        $shortcut.Save()
    }
    Write-Output "Installed for the current user at $install"
    if ($AssociatePdf) {
        $exePath = Join-Path $install 'App.Desktop.exe'
        $progId = Join-Path $RegistryRoot 'HunterOps.PdfEditor.Pdf'
        New-Item -Path (Join-Path $progId 'shell\open\command') -Force | Out-Null
        Set-Item -Path $progId -Value 'PDF document'
        Set-ItemProperty -Path $progId -Name 'FriendlyTypeName' -Value 'PDF document'
        New-Item -Path (Join-Path $progId 'DefaultIcon') -Force | Out-Null
        Set-Item -Path (Join-Path $progId 'DefaultIcon') -Value "`"$exePath`",0"
        Set-Item -Path (Join-Path $progId 'shell\open\command') -Value "`"$exePath`" `"%1`""
        $openWith = Join-Path $RegistryRoot '.pdf\OpenWithProgids'
        New-Item -Path $openWith -Force | Out-Null
        New-ItemProperty -Path $openWith -Name 'HunterOps.PdfEditor.Pdf' -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
        Write-Output 'Added PDF - Paperwork Done Faster to the Open with list for PDFs. To make it the default, choose it in Settings > Apps > Default apps.'
    }
    else { Write-Output 'No PDF associations were registered. App data is outside the install folder.' }
} finally {
    if ($archiveTemp -and (Test-Path -LiteralPath $archiveTemp)) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $tempFull = [IO.Path]::GetFullPath($archiveTemp)
        if ($tempFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and $tempFull -match 'PDFPaperwork-Extract-[0-9a-f]{32}$') { Remove-Item -LiteralPath $tempFull -Recurse -Force }
        else { throw "Refusing unexpected extraction cleanup path: $tempFull" }
    }
}
