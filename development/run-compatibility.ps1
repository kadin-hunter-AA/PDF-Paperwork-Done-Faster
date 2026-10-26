param([switch]$OpenResults)
$ErrorActionPreference = 'Stop'
$outputPath = Join-Path $PSScriptRoot ('artifacts/compatibility-' + [Guid]::NewGuid().ToString('N'))
$scriptPath = Join-Path $PSScriptRoot 'tools/compatibility_corpus.py'
& python $scriptPath $outputPath create
if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') run --project (Join-Path $PSScriptRoot 'EngineProof') -- $outputPath --compatibility
if ($LASTEXITCODE -ne 0) { throw 'Native compatibility checks failed.' }
& python $scriptPath $outputPath inspect
if ($LASTEXITCODE -ne 0) { throw 'Independent structure checks failed.' }
Write-Output "Compatibility results saved in $outputPath"
if ($OpenResults) {
    Start-Process -FilePath (Join-Path $outputPath 'interactive-features-saved.pdf')
    Start-Process -FilePath (Join-Path $outputPath 'graphics-boxes-saved.pdf')
    Start-Process -FilePath (Join-Path $outputPath 'password-aes256-saved.pdf')
}
