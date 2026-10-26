param([switch]$OpenResults, [string]$OutputPath, [string]$PopplerPath)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!$OutputPath) { $OutputPath = Join-Path $PSScriptRoot ('artifacts/nested-' + [Guid]::NewGuid().ToString('N')) }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Use a new output directory; existing results are preserved.' }
if (!$PopplerPath) {
    $found = Get-Command pdftoppm -ErrorAction SilentlyContinue
    if ($found) { $PopplerPath = $found.Source }
    else { $PopplerPath = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/native/poppler/Library/bin/pdftoppm.exe' }
}
if (!(Test-Path -LiteralPath $PopplerPath)) { throw 'Poppler is required for the independent acceptance check. Supply -PopplerPath <pdftoppm.exe>.' }
$python = if ($env:PDF_EDITOR_PYTHON) { $env:PDF_EDITOR_PYTHON } else { 'python' }
function Invoke-Checked { param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Nested check failed: $Executable" }
}
Invoke-Checked $python @((Join-Path $PSScriptRoot 'tools/nested_checks.py'), $OutputPath, 'create')
Invoke-Checked (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') @('run','--project',(Join-Path $PSScriptRoot 'EngineProof'),'--',(Join-Path $OutputPath 'engine'))
Invoke-Checked (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') @('run','--project',(Join-Path $PSScriptRoot 'EngineProof'),'--',$OutputPath,'--nested')
Invoke-Checked $python @((Join-Path $PSScriptRoot 'tools/nested_checks.py'), $OutputPath, 'inspect', $PopplerPath)
$gate = [ordered]@{ status = 'PASS'; scope = 'Milestone 1 engine proof with documented supported-case boundaries'; engineReport = 'engine/proof.json'; nativeNestedReport = 'native-nested.json'; independentNestedReport = 'independent-nested.json' }
$gate | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputPath 'milestone1.json')
Write-Output "Nested-edit evidence: $OutputPath"
if ($OpenResults) { Start-Process -FilePath (Join-Path $OutputPath 'shared-forms-edited.pdf') }
