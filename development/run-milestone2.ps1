param([switch]$BuildCorpus,[switch]$OpenResults,[string]$OutputPath)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
if(!$OutputPath){$OutputPath=Join-Path $PSScriptRoot 'fixtures/corpus-v1'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$sdk=Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe'
$project=Join-Path $PSScriptRoot 'EngineProof'
function Invoke-Checked { param([string]$Executable,[string[]]$Arguments) & $Executable @Arguments; if($LASTEXITCODE -ne 0){throw "Checkpoint failed: $Executable"} }
if($BuildCorpus){
    if(Test-Path -LiteralPath $OutputPath){throw 'Destination already exists. Use a new OutputPath; existing corpus snapshots are preserved.'}
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
    $engine=Join-Path $OutputPath 'engine';$corpus=Join-Path $OutputPath 'corpus';$compat=Join-Path $OutputPath 'compatibility';$edge=Join-Path $OutputPath 'edge';$product=Join-Path $OutputPath 'product'
    Invoke-Checked $sdk @('run','--project',$project,'--',$engine)
    Invoke-Checked $sdk @('run','--project',$project,'--',$corpus,'--corpus')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/compatibility_corpus.py'),$compat,'create')
    Invoke-Checked $sdk @('run','--project',$project,'--',$compat,'--compatibility')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/compatibility_corpus.py'),$compat,'inspect')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/edge_corpus.py'),$edge,'create')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/signed_fixture.py'),(Join-Path $edge 'ligature-mapped.pdf'),$edge)
    Invoke-Checked $sdk @('run','--project',$project,'--',$edge,'--edge')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/edge_corpus.py'),$edge,'inspect')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/signed_fixture.py'),(Join-Path $edge 'ligature-mapped.pdf'),$edge,'inspect')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/product_corpus.py'),$product,'create')
    Invoke-Checked $sdk @('run','--project',$project,'--',$product,'--product')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/product_corpus.py'),$product,'inspect')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/scenarios.json') -Destination (Join-Path $OutputPath 'scenarios.json')
    Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/corpus_gate.py'),$OutputPath,'build')
}
Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'tools/corpus_gate.py'),$OutputPath,'verify')
Write-Output "Versioned corpus: $OutputPath"
if($OpenResults){foreach($name in @('product/markup-gallery-saved.pdf','product/mixed-styles-languages-saved.pdf')){Start-Process -FilePath (Join-Path $OutputPath $name)}}
