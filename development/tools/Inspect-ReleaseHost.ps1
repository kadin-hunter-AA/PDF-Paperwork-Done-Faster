param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$EvidencePath = (Join-Path $PWD 'release-host-evidence.json')
)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package directory does not exist: $package" }
$packagePrefix = $package.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
if ([string]::Equals($evidenceFullPath, $package, [StringComparison]::OrdinalIgnoreCase) -or
    $evidenceFullPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Evidence output must be outside the package tree: $evidenceFullPath"
}
if (Test-Path -LiteralPath $evidenceFullPath) { throw "Evidence output already exists; choose a fresh path: $evidenceFullPath" }
$os = Get-CimInstance Win32_OperatingSystem
$hypervisor = Get-CimInstance Win32_ComputerSystem
$features = @()
foreach ($name in @('Microsoft-Hyper-V-All','Containers-DisposableClientVM')) {
    try {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction Stop
        $features += [pscustomobject]@{ Name=$name; State=[string]$feature.State }
    } catch {
        $features += [pscustomobject]@{ Name=$name; State='Unavailable to query' }
    }
}
$service = Get-Service vmms -ErrorAction SilentlyContinue
$printers = @(Get-Printer -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ Name=$_.Name; Driver=$_.DriverName; Port=$_.PortName }
})
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Path=$_.FullName.Substring($package.Length).TrimStart('\','/')
        Length=$_.Length
        SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$result = [ordered]@{
    CapturedUtc=[DateTime]::UtcNow.ToString('o')
    PackageDirectory=$package
    OS=[ordered]@{ Caption=$os.Caption; Version=$os.Version; Build=$os.BuildNumber; Architecture=$os.OSArchitecture }
    HypervisorPresent=[bool]$hypervisor.HypervisorPresent
    HyperVService=if ($service) { [ordered]@{ Status=[string]$service.Status; StartType=[string]$service.StartType } } else { $null }
    OptionalFeatures=$features
    RegisteredPrinters=$printers
    PackageFileCount=$files.Count
    PackageFiles=$files
    Scope='Host/package inventory only. Does not prove clean-machine behavior, GUI acceptance, signing, print rendering or legal clearance.'
}
$parent = Split-Path -Parent $evidenceFullPath
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
Write-Output "Wrote host/package evidence: $evidenceFullPath"
Write-Output "OS: $($os.Caption), build $($os.BuildNumber), $($os.OSArchitecture)"
Write-Output "Hypervisor present: $($hypervisor.HypervisorPresent); registered printers: $($printers.Count)"
Write-Output "Package files inventoried: $($files.Count)"
