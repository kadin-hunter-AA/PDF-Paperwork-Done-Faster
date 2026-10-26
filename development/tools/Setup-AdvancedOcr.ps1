param([string]$DevelopmentRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $DevelopmentRoot).Path
$runtime = Join-Path $root 'tools/advanced-python-3.14.8'
$model = Join-Path $root 'tools/ocr-runtime/eng.traineddata'
$expectedModel = '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'
if ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedModel) {
    throw 'The pinned English model failed its integrity check.'
}
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'python.exe'))) { throw 'Stage the isolated Python runtime first.' }
foreach ($name in @('pymupdf', 'pymupdf-1.28.2.dist-info')) {
    $source = Join-Path $root "tools/redaction-pymupdf/$name"
    $target = Join-Path $runtime "packages/$name"
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Extension -eq '.pyc' -or $file.FullName.Contains('__pycache__')) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $destination = Join-Path $target $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
                throw "Refusing to replace a different staged dependency: $relative"
            }
        } else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
$data = Join-Path $runtime 'tessdata'
[IO.Directory]::CreateDirectory($data) | Out-Null
$destinationModel = Join-Path $data 'eng.traineddata'
if (Test-Path -LiteralPath $destinationModel) {
    if ((Get-FileHash -LiteralPath $destinationModel).Hash.ToLowerInvariant() -ne $expectedModel) { throw 'Existing staged model differs.' }
} else { Copy-Item -LiteralPath $model -Destination $destinationModel }
$license = Join-Path $data 'LICENSE'
if (-not (Test-Path -LiteralPath $license)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/87416418657359cb625c412a48b6e1d6d41c29bd/LICENSE' -OutFile $license
}
if ((Get-FileHash -LiteralPath $license).Hash.ToLowerInvariant() -ne 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30') {
    throw 'The upstream English model license failed its integrity check.'
}
& (Join-Path $runtime 'python.exe') -I -B -c 'import pymupdf; assert pymupdf.VersionBind == "1.28.2"; print("Pinned local OCR imports passed")'
if ($LASTEXITCODE -ne 0) { throw 'OCR runtime import failed.' }
Write-Output 'Local development OCR dependencies staged. Redistribution review remains open.'
