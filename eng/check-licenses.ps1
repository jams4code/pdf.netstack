# Fails when a shipping project resolves a package that is not on the license allowlist.
# The core library must stay dependency free; this also fails if it gains any runtime package.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$allow = (Get-Content (Join-Path $PSScriptRoot 'license-allowlist.json') -Raw | ConvertFrom-Json).packages
$projects = @(
    'src/PdfNetStack/PdfNetStack.csproj',
    'src/PdfNetStack.Cli/PdfNetStack.Cli.csproj',
    'src/PdfNetStack.Azure/PdfNetStack.Azure.csproj'
)
$buildOnly = @('Microsoft.NET.ILLink.Tasks')
$failed = $false

foreach ($project in $projects) {
    $json = dotnet list (Join-Path $root $project) package --include-transitive --format json | ConvertFrom-Json
    foreach ($p in $json.projects) {
        foreach ($f in $p.frameworks) {
            $packages = @($f.topLevelPackages) + @($f.transitivePackages) | Where-Object { $_ }
            foreach ($pkg in $packages) {
                if ($project -like '*PdfNetStack/PdfNetStack.csproj' -and $buildOnly -notcontains $pkg.id) {
                    Write-Host "FAIL $project ($($f.framework)): core library must have no runtime dependencies, found $($pkg.id)"
                    $failed = $true
                }
                elseif (-not $allow.PSObject.Properties[$pkg.id]) {
                    Write-Host "FAIL $project ($($f.framework)): $($pkg.id) $($pkg.resolvedVersion) is not on the license allowlist"
                    $failed = $true
                }
            }
        }
    }
}

if ($failed) { exit 1 }
Write-Host 'License check passed: every runtime package is on the allowlist.'
