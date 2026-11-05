# Fails when a shipping project resolves a package that is not on the license allowlist.
# The core library must stay dependency free on modern .NET; on .NET Standard 2.0 it may use only
# Microsoft's span polyfill packages listed under coreNetStandardOnly. Packages under onlyIn may
# ship with the named projects and no other (ONNX Runtime with the OCR add-on and the OCR tool).
# Data files a package carries, the OCR models and the hyphenation patterns, must be listed under
# bundledFiles with their license.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $PSScriptRoot 'license-allowlist.json') -Raw | ConvertFrom-Json
$allow = $config.packages
$coreNetStandard = @($config.coreNetStandardOnly)
$onlyIn = $config.onlyIn
$projects = @(
    'src/PdfNetStack/PdfNetStack.csproj',
    'src/PdfNetStack.Cli/PdfNetStack.Cli.csproj',
    'src/PdfNetStack.Html/PdfNetStack.Html.csproj',
    'src/PdfNetStack.Hyphenation/PdfNetStack.Hyphenation.csproj',
    'src/PdfNetStack.Ocr/PdfNetStack.Ocr.csproj',
    'src/PdfNetStack.Ocr.Models/PdfNetStack.Ocr.Models.csproj',
    'src/PdfNetStack.Ocr.Tool/PdfNetStack.Ocr.Tool.csproj'
)
$buildOnly = @('Microsoft.NET.ILLink.Tasks')
$failed = $false

foreach ($project in $projects) {
    $json = dotnet list (Join-Path $root $project) package --include-transitive --format json | ConvertFrom-Json
    foreach ($p in $json.projects) {
        foreach ($f in $p.frameworks) {
            $packages = @($f.topLevelPackages) + @($f.transitivePackages) | Where-Object { $_ }
            foreach ($pkg in $packages) {
                $isCore = $project -like '*PdfNetStack/PdfNetStack.csproj'
                $coreOk = $buildOnly -contains $pkg.id -or (($f.framework -like 'netstandard*' -or $f.framework -like '.NETStandard*') -and $coreNetStandard -contains $pkg.id)
                if ($isCore -and -not $coreOk) {
                    Write-Host "FAIL $project ($($f.framework)): core library must have no runtime dependencies, found $($pkg.id)"
                    $failed = $true
                }
                elseif (-not $allow.PSObject.Properties[$pkg.id]) {
                    Write-Host "FAIL $project ($($f.framework)): $($pkg.id) $($pkg.resolvedVersion) is not on the license allowlist"
                    $failed = $true
                }
                elseif ($onlyIn.PSObject.Properties[$pkg.id] -and @($onlyIn.($pkg.id)) -notcontains $project) {
                    Write-Host "FAIL $project ($($f.framework)): $($pkg.id) may ship only with $(@($onlyIn.($pkg.id)) -join ', ')"
                    $failed = $true
                }
            }
        }
    }
}

# The model files the OCR models package packs, and the OCR tool carries too.
$modelsProject = [xml](Get-Content (Join-Path $root 'src/PdfNetStack.Ocr.Models/PdfNetStack.Ocr.Models.csproj') -Raw)
$models = @($modelsProject.Project.ItemGroup.OcrModel | Where-Object { $_ })
if ($models.Count -eq 0) {
    Write-Host 'FAIL src/PdfNetStack.Ocr.Models: no OcrModel items found'
    $failed = $true
}
foreach ($model in $models) {
    $name = Split-Path -Leaf $model.Include
    if (-not $config.bundledFiles.PSObject.Properties[$name]) {
        Write-Host "FAIL src/PdfNetStack.Ocr.Models: $name is not on the license allowlist"
        $failed = $true
    }
}

# The hyphenation pattern sets, each generated from a source whose license was read.
$patterns = @(Get-ChildItem (Join-Path $root 'src/PdfNetStack.Hyphenation/Patterns') -Filter '*.deflate')
if ($patterns.Count -eq 0) {
    Write-Host 'FAIL src/PdfNetStack.Hyphenation: no pattern sets found'
    $failed = $true
}
foreach ($set in $patterns) {
    $name = "hyphenation/$($set.Name)"
    if (-not $config.bundledFiles.PSObject.Properties[$name]) {
        Write-Host "FAIL src/PdfNetStack.Hyphenation: $($set.Name) is not on the license allowlist"
        $failed = $true
    }
}

if ($failed) { exit 1 }
Write-Host 'License check passed: every runtime package and bundled file is on the allowlist.'
