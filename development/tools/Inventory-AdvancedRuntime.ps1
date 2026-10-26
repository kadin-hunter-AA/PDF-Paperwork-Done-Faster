param([string]$DevelopmentRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $DevelopmentRoot).Path
$runtime = Join-Path $root 'tools/advanced-python-3.14.8'
$files = Get-ChildItem -LiteralPath $runtime -File -Recurse | Where-Object {
    $_.Name -ne 'runtime-inventory.json' -and $_.Extension -ne '.pyc' -and -not $_.FullName.Contains('__pycache__')
} | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/'); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$dependencies = Get-ChildItem -LiteralPath (Join-Path $runtime 'packages') -Directory -Filter '*.dist-info' | Sort-Object Name | ForEach-Object {
    $metadata = Join-Path $_.FullName 'METADATA'
    $name = (Select-String -LiteralPath $metadata -Pattern '^Name: ' | Select-Object -First 1).Line.Substring(6)
    $version = (Select-String -LiteralPath $metadata -Pattern '^Version: ' | Select-Object -First 1).Line.Substring(9)
    [pscustomobject]@{Name=$name;Version=$version;Metadata=[IO.Path]::GetRelativePath($runtime,$metadata).Replace('\','/')}
}
[pscustomobject]@{
    Version='3.14.8'; Architecture='Windows x64'; Scope='Personal/internal development; redistribution review open'
    PythonArchiveSHA256='a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310'
    EnglishModelSHA256='7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'
    Dependencies=@($dependencies); FileCount=@($files).Count; Files=@($files)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'runtime-inventory.json') -Encoding utf8
Write-Output "Recorded $(@($files).Count) companion-runtime files and $(@($dependencies).Count) dependencies."
