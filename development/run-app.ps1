$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& (Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe') build (Join-Path $PSScriptRoot 'App.Desktop/App.Desktop.csproj') --nologo
if($LASTEXITCODE -ne 0){throw 'Application build failed'}
Start-Process -FilePath (Join-Path $PSScriptRoot 'App.Desktop/bin/Debug/net10.0-windows/App.Desktop.exe') -WindowStyle Normal
