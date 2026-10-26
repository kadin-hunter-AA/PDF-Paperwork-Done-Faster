param([string]$InstallDirectory, [string]$StartMenuDirectory, [string]$RegistryRoot = 'HKCU:\Software\Classes')

$ErrorActionPreference = 'Stop'
if (-not $InstallDirectory) {
    $scriptDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
    if (Test-Path -LiteralPath (Join-Path $scriptDirectory '.hunterops-pdf-editor-install.json') -PathType Leaf) {
        $InstallDirectory = $scriptDirectory
    } else {
        $InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\PDF - Paperwork Done Faster'
    }
}
$install = [IO.Path]::GetFullPath($InstallDirectory)
$prefix = $install.TrimEnd('\') + '\'
function Assert-NoReparsePath([string]$TargetPath) {
    $full = [IO.Path]::GetFullPath($TargetPath)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($full, $install, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped install root; no further files were removed: $full"
    }
    if (-not (Test-Path -LiteralPath $install -PathType Container)) { throw "Install root is missing; no files were removed: $install" }
    $rootItem = Get-Item -LiteralPath $install -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point at install root; no files were removed: $install" }
    $relative = if ([string]::Equals($full, $install, [StringComparison]::OrdinalIgnoreCase)) { '' } else { $full.Substring($prefix.Length) }
    $cursor = $install
    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point found; no further files were removed: $cursor" }
        }
    }
}
Assert-NoReparsePath $install
$markerName = '.hunterops-pdf-editor-install.json'
$markerPath = Join-Path $install $markerName
if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw "Install ownership marker is missing; no files were removed: $install" }
Assert-NoReparsePath $markerPath
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.Identity -ne 'HunterOpsPdfEditor' -or $marker.Schema -notin @(1,2) -or $marker.Files -isnot [array] -or
    ($marker.Schema -eq 2 -and $marker.Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$')) {
    throw "Install ownership marker is invalid; no files were removed: $markerPath"
}

$ownedPaths = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $marker.Files) {
    $relative = if ($marker.Schema -eq 1) { $entry } else { $entry.Path }
    if ($marker.Schema -eq 2 -and ($entry.Path -isnot [string] -or $entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$')) {
        throw "Invalid path record in ownership marker; no files were removed: $($entry | ConvertTo-Json -Compress)"
    }
    if (-not ($relative -is [string]) -or [string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) {
        throw "Unsafe path in ownership marker; no files were removed: $relative"
    }
    if ($relative -match '(^|[\\/])\.{1,2}([\\/]|$)') {
        throw "Unsafe path in ownership marker; no files were removed: $relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $install ($relative.Replace('/','\'))))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path in ownership marker; no files were removed: $relative"
    }
    $cursor = $install
    $installItem = Get-Item -LiteralPath $cursor -Force
    if (($installItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse point in install path; no files were removed: $cursor"
    }
    $inside = $path.Substring($prefix.Length)
    $parts = $inside.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)
    for ($index = 0; $index -lt $parts.Length; $index++) {
        if ($parts[$index] -in @('.', '..')) { throw "Unsafe path in ownership marker; no files were removed: $relative" }
        $cursor = Join-Path $cursor $parts[$index]
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point in install inventory; no files were removed: $cursor"
            }
        }
    }
    if (-not $ownedPaths.Contains($path)) { $ownedPaths.Add($path) }
}

$expectedExe = [IO.Path]::GetFullPath((Join-Path $install 'App.Desktop.exe'))
$running = Get-Process -Name 'App.Desktop' -ErrorAction SilentlyContinue |
    Where-Object { try { [string]::Equals([IO.Path]::GetFullPath($_.Path), $expectedExe, [StringComparison]::OrdinalIgnoreCase) } catch { $false } }
if ($running) { throw 'The app is running. Close it before uninstalling.' }

foreach ($path in $ownedPaths) {
    Assert-NoReparsePath $path
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    $parent = Split-Path $path -Parent
    while ($parent.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        [void]$directories.Add($parent)
        $parent = Split-Path $parent -Parent
    }
}
Assert-NoReparsePath $markerPath
Remove-Item -LiteralPath $markerPath -Force
foreach ($directory in ($directories | Sort-Object Length -Descending)) {
    Assert-NoReparsePath $directory
    if ((Test-Path -LiteralPath $directory -PathType Container) -and
        ((Get-ChildItem -LiteralPath $directory -Force | Measure-Object).Count -eq 0)) {
        Remove-Item -LiteralPath $directory -Force
    }
}
Assert-NoReparsePath $install
if ((Test-Path -LiteralPath $install -PathType Container) -and
    ((Get-ChildItem -LiteralPath $install -Force | Measure-Object).Count -eq 0)) {
    Remove-Item -LiteralPath $install -Force
}

$shortcutRoot = if ($StartMenuDirectory) { [IO.Path]::GetFullPath($StartMenuDirectory) } else { Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs' }
$shortcutPath = Join-Path $shortcutRoot ((Split-Path $install -Leaf) + '.lnk')
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ([string]::Equals([IO.Path]::GetFullPath($shortcut.TargetPath), $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}
# Remove the optional Open with registration only when it points at this installation.
$progId = Join-Path $RegistryRoot 'HunterOps.PdfEditor.Pdf'
$command = Join-Path $progId 'shell\open\command'
if (Test-Path -LiteralPath $command) {
    $value = (Get-Item -LiteralPath $command).GetValue('')
    if ($value -and $value.StartsWith('"' + $expectedExe + '"', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $progId -Recurse -Force
        $openWith = Join-Path $RegistryRoot '.pdf\OpenWithProgids'
        if (Test-Path -LiteralPath $openWith) { Remove-ItemProperty -LiteralPath $openWith -Name 'HunterOps.PdfEditor.Pdf' -ErrorAction SilentlyContinue }
    }
}
Write-Output 'Removed package-owned files and its matching Start menu shortcut. Other files and user data were preserved.'
Write-Output 'Recovery document copies are retained in your user data; uninstall does not remove them.'
