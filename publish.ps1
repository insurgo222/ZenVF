[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\ZenVF\ZenVF.csproj'
$output = Join-Path $root 'dist'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if (-not $NoClean -and (Test-Path $output)) {
    Remove-Item -Path $output -Recurse -Force
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

Write-Host "Publishing ZenVF ($Runtime, self-contained=$selfContained)..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained $selfContained `
    -o $output `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$required = @(
    (Join-Path $output 'ZenVF.exe'),
    (Join-Path $output 'README.md'),
    (Join-Path $output 'THIRD-PARTY-NOTICES.md'),
    (Join-Path $output 'licenses')
)

$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "Publish output is missing: $($missing -join ', ')"
}

$total = (Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host "Output: $output" -ForegroundColor Green
Write-Host ("Files:  {0}" -f (Get-ChildItem $output -Recurse -File).Count)
Write-Host ("Size:   {0:N1} MB" -f ($total / 1MB))
