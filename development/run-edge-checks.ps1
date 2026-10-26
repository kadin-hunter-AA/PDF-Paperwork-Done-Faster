param([switch]$OpenResults)
$ErrorActionPreference='Stop'
$outputPath=Join-Path $PSScriptRoot ('artifacts/edge-'+[Guid]::NewGuid().ToString('N'))
$edgeScript=Join-Path $PSScriptRoot 'tools/edge_corpus.py'
$signScript=Join-Path $PSScriptRoot 'tools/signed_fixture.py'
& python $edgeScript $outputPath create
if($LASTEXITCODE -ne 0){throw 'Edge fixture creation failed'}
& python $signScript (Join-Path $outputPath 'ligature-mapped.pdf') $outputPath
if($LASTEXITCODE -ne 0){throw 'Signature fixture checks failed'}
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') run --project (Join-Path $PSScriptRoot 'EngineProof') -- $outputPath --edge
if($LASTEXITCODE -ne 0){throw 'Native edge checks failed'}
& python $edgeScript $outputPath inspect
if($LASTEXITCODE -ne 0){throw 'Independent edge checks failed'}
& python $signScript (Join-Path $outputPath 'ligature-mapped.pdf') $outputPath inspect
if($LASTEXITCODE -ne 0){throw 'Signature rewrite verification failed'}
Write-Output "Edge results saved in $outputPath"
if($OpenResults){Start-Process -FilePath (Join-Path $outputPath 'edge-preview.pdf')}
