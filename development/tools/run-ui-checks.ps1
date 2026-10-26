param(
    [string]$DevelopmentRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$WorkflowOnly,
    [switch]$ShellOnly,
    [switch]$FormFillOnly,
    [switch]$AdvancedDialogsOnly,
    [switch]$AnnotationsOnly,
    [switch]$StructuredTextOnly,
    [switch]$ContinuationOnly,
    [switch]$DrawingOnly,
    [switch]$DirectEditOnly,
    [switch]$PrintOnly,
    # Any other focused proof flag, e.g. --viewer-only or --signature-only.
    [string]$Only
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $DevelopmentRoot).Path
$uiOutput = Join-Path $root 'App.UiTests\bin\Debug\net10.0-windows'
$dotnet = Join-Path $root 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath (Join-Path $uiOutput 'App.UiTests.dll'))) {
    throw "UI test output is missing. Build HunterPdf.slnx first: $uiOutput"
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "The bundled .NET host is missing: $dotnet"
}

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempRun = Join-Path $tempBase ("HunterPdf.UiTests-" + [guid]::NewGuid().ToString('N'))
$fullTempRun = [System.IO.Path]::GetFullPath($tempRun)
if (-not $fullTempRun.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a temporary path outside the system temp directory: $fullTempRun"
}
if (Test-Path -LiteralPath $fullTempRun) {
    throw "Refusing to overwrite an existing temporary path: $fullTempRun"
}

New-Item -ItemType Directory -Path $fullTempRun | Out-Null
try {
    # Copy the private runtime concurrently; serial Copy-Item can spend minutes
    # copying its many small files from a synced workspace before tests start.
    & robocopy $uiOutput $fullTempRun /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /MT:8 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "UI test output copy failed (robocopy exit $LASTEXITCODE)." }
    if (([int]$WorkflowOnly.IsPresent + [int]$ShellOnly.IsPresent + [int]$FormFillOnly.IsPresent + [int]$AdvancedDialogsOnly.IsPresent + [int]$AnnotationsOnly.IsPresent + [int]$StructuredTextOnly.IsPresent + [int]$ContinuationOnly.IsPresent + [int]$DrawingOnly.IsPresent + [int]$DirectEditOnly.IsPresent + [int]$PrintOnly.IsPresent) -gt 1) {
        throw 'Choose only one focused UI proof.'
    }
    if ($Only) {
        if ($Only -notmatch '^--[a-z-]+-only$') { throw "Use a focused proof flag such as --viewer-only." }
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root $Only
    }
    elseif ($PrintOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--print-only'
    }
    elseif ($DirectEditOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--direct-edit-only'
    }
    elseif ($DrawingOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--drawing-only'
    }
    elseif ($ContinuationOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--continuation-only'
    }
    elseif ($StructuredTextOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--structured-text-only'
    }
    elseif ($AnnotationsOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--annotations-only'
    }
    elseif ($AdvancedDialogsOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--advanced-dialogs-only'
    }
    elseif ($FormFillOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--form-fill-only'
    }
    elseif ($WorkflowOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--workflow-only'
    }
    elseif ($ShellOnly) {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root '--shell-only'
    }
    else {
        & $dotnet (Join-Path $fullTempRun 'App.UiTests.dll') $root
    }
    if ($LASTEXITCODE -ne 0) {
        throw "UI regression runner exited with code $LASTEXITCODE"
    }
}
finally {
    if (Test-Path -LiteralPath $fullTempRun) {
        Remove-Item -LiteralPath $fullTempRun -Recurse -Force
    }
}
