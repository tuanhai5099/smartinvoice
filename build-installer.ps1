param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

Write-Host "=== SmartInvoice - Build installer ==="

$solutionDir = $PSScriptRoot
$publishDir  = Join-Path $solutionDir "publish\SmartInvoice"
$installerDir = Join-Path $solutionDir "installer"
$issFile     = Join-Path $installerDir "SmartInvoice.iss"

if (-not (Test-Path $issFile)) {
    throw "Inno Setup script not found: $issFile"
}

if (-not (Test-Path $installerDir)) {
    New-Item -ItemType Directory -Path $installerDir | Out-Null
}

if (-not (Test-Path $publishDir)) {
    New-Item -ItemType Directory -Path $publishDir | Out-Null
}

$selfContainedArg = $false
if ($SelfContained.IsPresent) {
    $selfContainedArg = $true
}

Write-Host "1) Publishing SmartInvoice.Bootstrapper..."
dotnet publish "$solutionDir\src\SmartInvoice.Bootstrapper\SmartInvoice.Bootstrapper.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained:$selfContainedArg `
    -o $publishDir

Write-Host "2) Running Inno Setup compiler..."
$isccDefaultPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $isccDefaultPath)) {
    throw "Không tìm thấy Inno Setup compiler tại '$isccDefaultPath'. Hãy cài Inno Setup 6 hoặc chỉnh lại đường dẫn trong script."
}

& $isccDefaultPath $issFile

Write-Host "=== Hoàn tất. File cài đặt nằm trong thư mục 'artifacts' cạnh repo. ==="

