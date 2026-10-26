param([switch]$OpenResults)
$ErrorActionPreference = 'Stop'
$outputPath = Join-Path $PSScriptRoot ('artifacts/corpus-' + [Guid]::NewGuid().ToString('N'))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') run --project (Join-Path $PSScriptRoot 'EngineProof') -- $outputPath --corpus
if ($LASTEXITCODE -ne 0) { throw 'Corpus checks failed. Copy the error above into the chat.' }
$report = Get-Content -LiteralPath (Join-Path $outputPath 'corpus.json') -Raw | ConvertFrom-Json
$report | Select-Object status,passed,benchmark,pending | ConvertTo-Json -Depth 5
Write-Output "Corpus and full hash manifest saved in $outputPath"
if ($OpenResults) {
    foreach ($name in @('layout-columns-table.pdf','mixed-digital-scan.pdf','large-500-pages.pdf')) {
        Start-Process -FilePath (Join-Path $outputPath $name)
    }
}
