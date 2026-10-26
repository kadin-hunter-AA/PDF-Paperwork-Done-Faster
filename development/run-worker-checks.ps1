$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dotnet=Join-Path $root 'tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& $dotnet build (Join-Path $root 'HunterPdf.slnx') --nologo
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$priorEvidence = @{}
Get-ChildItem (Join-Path $root 'artifacts') -Directory -Filter 'worker-*' -ErrorAction SilentlyContinue | ForEach-Object { $priorEvidence[$_.FullName] = $true }
& $dotnet run --no-build --project (Join-Path $root 'App.Tests') -- $root
if($LASTEXITCODE -ne 0){throw 'Worker checks failed'}
$workerEvidence = Get-ChildItem (Join-Path $root 'artifacts') -Directory -Filter 'worker-*' |
    Where-Object { -not $priorEvidence.ContainsKey($_.FullName) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if(-not $workerEvidence){throw 'Could not locate the worker evidence directory for independent page export verification'}
$pythonLauncher = Get-Command python -ErrorAction SilentlyContinue
if(-not $pythonLauncher){throw 'Python is required for the pinned pypdf independent proof'}
& $pythonLauncher.Source (Join-Path $root 'App.Tests/independent-page-proof.py') --root $root --output $workerEvidence.FullName --page-organization (Join-Path $workerEvidence.FullName 'page-organization')
if($LASTEXITCODE -ne 0){throw 'Independent pypdf page verification failed'}

