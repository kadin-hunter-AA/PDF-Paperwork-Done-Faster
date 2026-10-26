param(
    [string]$DevelopmentRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $DevelopmentRoot).Path
$version = '3.14.8'
$expected = 'a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310'
$url = "https://www.python.org/ftp/python/$version/python-$version-embed-amd64.zip"
$runtime = Join-Path $root "tools/advanced-python-$version"
$archive = Join-Path $root "tools/python-$version-embed-amd64.zip"
if (Test-Path -LiteralPath $runtime) {
    throw "Refusing to alter an existing runtime: $runtime"
}
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $url -OutFile $archive
}
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Python archive hash does not match the official release checksum.' }
Write-Output 'Official Python archive checksum verified.'
Expand-Archive -LiteralPath $archive -DestinationPath $runtime
$packages = Join-Path $runtime 'packages'
New-Item -ItemType Directory -Path $packages | Out-Null
# These are the already pinned project dependencies, not global site-packages.
Write-Output 'Copying pinned companion dependencies into the isolated runtime.'
Copy-Item -Path (Join-Path $root 'tools/signing-packages/*') -Destination $packages -Recurse
Copy-Item -LiteralPath (Join-Path $root 'tools/python-packages/pypdf') -Destination $packages -Recurse
Copy-Item -LiteralPath (Join-Path $root 'tools/python-packages/pypdf-6.1.3.dist-info') -Destination $packages -Recurse
# Isolated mode: no registry, user site, PYTHONPATH, or ambient site initialization.
@('python314.zip', '.', 'packages') | Set-Content -LiteralPath (Join-Path $runtime 'python314._pth') -Encoding ascii
Write-Output 'Hashing the staged companion runtime inventory.'
$inventory = Get-ChildItem -LiteralPath $runtime -File -Recurse | ForEach-Object {
    [pscustomobject]@{Path=[System.IO.Path]::GetRelativePath($runtime,$_.FullName); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[pscustomobject]@{
    Version=$version; Source=$url; ArchiveSHA256=$actual
    Release='https://www.python.org/downloads/release/python-3148/'
    Scope='Development companion-runtime staging; not integrated or released'
    Files=$inventory
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtime 'runtime-inventory.json') -Encoding utf8
& (Join-Path $runtime 'python.exe') -I -B -c 'import sys, pypdf, cryptography; from pyhanko.sign import signers; print(sys.version); print("Advanced companion runtime imports passed")'
if ($LASTEXITCODE -ne 0) { throw 'Advanced companion runtime smoke failed.' }
Write-Output "Staged development companion runtime: $runtime"
