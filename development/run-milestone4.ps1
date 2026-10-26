$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'run-worker-checks.ps1')
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') run --no-build --project (Join-Path $PSScriptRoot 'App.UiTests') -- $PSScriptRoot
if($LASTEXITCODE -ne 0){throw 'Desktop state checks failed'}

