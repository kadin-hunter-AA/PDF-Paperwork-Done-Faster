param([switch]$OpenResults,[switch]$OpenNestedResult)
$ErrorActionPreference = 'Stop'
$outputPath = Join-Path $PSScriptRoot ('artifacts/check-' + [Guid]::NewGuid().ToString('N'))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') run --project (Join-Path $PSScriptRoot 'EngineProof') -- $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Engine checks failed. Copy the error above into the chat.' }
Get-Content -LiteralPath (Join-Path $outputPath 'proof.json')
Write-Output "Results saved in $outputPath"
if ($OpenNestedResult) { Start-Process -FilePath (Join-Path $outputPath 'complex-edited.pdf') }
if ($OpenResults) {
    foreach ($name in @('font-embedded-edited.pdf','font-subset-edited.pdf','font-missing-reembedded.pdf','complex-original.pdf','complex-edited.pdf')) {
        Start-Process -FilePath (Join-Path $outputPath $name)
    }
}
