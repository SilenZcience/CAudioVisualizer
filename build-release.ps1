# CAudioVisualizer Build Script

param(
    [string]$Version = "1.0.0"
)

Write-Host "Building CAudioVisualizer v$Version for distribution..." -ForegroundColor Green

Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow
# Clean only build artifacts, preserve releases folder with previous versions
dotnet clean -c Release --verbosity quiet
if (Test-Path ".\bin") { Remove-Item ".\bin" -Recurse -Force }
if (Test-Path ".\obj") { Remove-Item ".\obj" -Recurse -Force }
if (Test-Path ".\releases\installer-ready") { Remove-Item ".\releases\installer-ready" -Recurse -Force }

# Create output directory
$outputDir = ".\releases"
if (!(Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Write-Host "Building self-contained x64 executable..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:AssemblyName="CAudioVisualizer-v$Version" -o "$outputDir\win-x64"

Write-Host "Building self-contained x86 executable..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:AssemblyName="CAudioVisualizer-v$Version" -o "$outputDir\win-x86"

Write-Host "Building framework-dependent version..." -ForegroundColor Yellow
dotnet publish -c Release --self-contained false -p:DebugType=None -p:DebugSymbols=false -p:AssemblyName="CAudioVisualizer-v$Version" -o "$outputDir\framework-dependent"

Write-Host "Building installer-ready version with DLLs..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "$outputDir\installer-ready"

Write-Host "Build completed!" -ForegroundColor Green
Write-Host "Output directories:" -ForegroundColor Cyan
Write-Host "  - $outputDir\win-x64 (self-contained 64-bit single file)" -ForegroundColor White
Write-Host "  - $outputDir\win-x86 (self-contained 32-bit single file)" -ForegroundColor White
Write-Host "  - $outputDir\framework-dependent (requires .NET 9)" -ForegroundColor White
Write-Host "  - $outputDir\installer-ready (installer-ready with DLLs)" -ForegroundColor White

# Show file sizes
Write-Host "`nFile sizes:" -ForegroundColor Cyan
if (Test-Path "$outputDir\win-x64\CAudioVisualizer-v$Version.exe") {
    $size64 = [math]::Round((Get-Item "$outputDir\win-x64\CAudioVisualizer-v$Version.exe").Length / 1MB, 2)
    Write-Host "  - win-x64: $size64 MB" -ForegroundColor White
}
if (Test-Path "$outputDir\win-x86\CAudioVisualizer-v$Version.exe") {
    $size86 = [math]::Round((Get-Item "$outputDir\win-x86\CAudioVisualizer-v$Version.exe").Length / 1MB, 2)
    Write-Host "  - win-x86: $size86 MB" -ForegroundColor White
}
if (Test-Path "$outputDir\framework-dependent\CAudioVisualizer-v$Version.exe") {
    $sizeFD = [math]::Round((Get-Item "$outputDir\framework-dependent\CAudioVisualizer-v$Version.exe").Length / 1MB, 2)
    Write-Host "  - framework-dependent: $sizeFD MB" -ForegroundColor White
}
if (Test-Path "$outputDir\installer-ready\CAudioVisualizer.exe") {
    $sizeInstaller = [math]::Round((Get-Item "$outputDir\installer-ready\CAudioVisualizer.exe").Length / 1MB, 2)
    Write-Host "  - installer-ready: $sizeInstaller MB" -ForegroundColor White
}

Write-Host "`nReady for distribution! 🎵" -ForegroundColor Green
