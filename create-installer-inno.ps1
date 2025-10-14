# CAudioVisualizer Inno Setup Installer Builder
# This script creates installer packages using Inno Setup

param(
    [string]$Version = "1.0.0",
    [switch]$SkipBuild = $false
)

Write-Host "Creating CAudioVisualizer Inno Setup Installer v$Version" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Green

# Create necessary directories
$outputDir = ".\releases"
$sourceDir = ".\releases\win-installer-ready"

if (!(Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Build the application if not skipped
if (-not $SkipBuild) {
    Write-Host "`nBuilding application..." -ForegroundColor Yellow
    & .\build-release.ps1 -Version $Version

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "Skipping build..." -ForegroundColor Yellow
}

# Verify source directory exists and has files
if (!(Test-Path $sourceDir)) {
    Write-Host "Error: Source directory not found: $sourceDir" -ForegroundColor Red
    Write-Host "Run the build-release.ps1 script first or remove -SkipBuild parameter" -ForegroundColor Yellow
    exit 1
}

Write-Host "`nCreating Inno Setup installer..." -ForegroundColor Yellow

# Ensure config files are available for installer
$configSource = ".\config.json"
$readmeSource = ".\README.md"

if (Test-Path $configSource) {
    Copy-Item $configSource "$sourceDir\config.json" -Force
    Write-Host "Made config.json available for installer" -ForegroundColor Cyan
}

if (Test-Path $readmeSource) {
    Copy-Item $readmeSource "$sourceDir\README.md" -Force
    Write-Host "Made README.md available for installer" -ForegroundColor Cyan
}

# Create Inno Setup script
$innoScript = @"
; CAudioVisualizer Inno Setup Script
; Audio Visualizer Installer

[Setup]
AppName=CAudioVisualizer
AppVersion=$Version
AppPublisher=Silas Kraume
AppPublisherURL=https://github.com/SilenZcience/CAudioVisualizer
AppSupportURL=https://github.com/SilenZcience/CAudioVisualizer/issues
AppUpdatesURL=https://github.com/SilenZcience/CAudioVisualizer/releases
DefaultDirName={autopf}\CAudioVisualizer
DefaultGroupName=CAudioVisualizer
AllowNoIcons=yes
OutputDir=..
OutputBaseFilename=CAudioVisualizer-$Version-win-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\CAudioVisualizer.exe
UninstallDisplayName=CAudioVisualizer v$Version
VersionInfoVersion=$Version.0
VersionInfoCompany=Silas Kraume
VersionInfoDescription=Audio Visualizer
VersionInfoProductName=CAudioVisualizer
; 64-bit installer configuration
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.iss,*.tmp,*.log,config.json"
; Config files go directly to user AppData
Source: "config.json"; DestDir: "{userappdata}\CAudioVisualizer"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\CAudioVisualizer"; Filename: "{app}\CAudioVisualizer.exe"; WorkingDir: "{app}"; Comment: "Audio Visualizer"
Name: "{group}\{cm:UninstallProgram,CAudioVisualizer}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CAudioVisualizer"; Filename: "{app}\CAudioVisualizer.exe"; WorkingDir: "{app}"; Comment: "Audio Visualizer"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\CAudioVisualizer"; Filename: "{app}\CAudioVisualizer.exe"; WorkingDir: "{app}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\CAudioVisualizer.exe"; Description: "{cm:LaunchProgram,CAudioVisualizer}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure InitializeWizard;
begin
  // Auto-accept license if present
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

function UnInstallOldVersion(): Integer;
var
  sUnInstallString: String;
  iResultCode: Integer;
begin
  Result := 0;
  sUnInstallString := GetUninstallString();
  if sUnInstallString <> '' then begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if Exec(sUnInstallString, '/SILENT /NORESTART /SUPPRESSMSGBOXES','', SW_HIDE, ewWaitUntilTerminated, iResultCode) then
      Result := 3
    else
      Result := 2;
  end else
    Result := 1;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep=ssInstall) then
  begin
    if (IsUpgrade()) then
    begin
      UnInstallOldVersion();
    end;
  end;
end;
"@

Set-Content -Path "$sourceDir\CAudioVisualizer.iss" -Value $innoScript

Write-Host "Inno Setup script created: CAudioVisualizer.iss" -ForegroundColor Cyan

# Try to find Inno Setup compiler
$innoCompiler = ""
$possiblePaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 5\ISCC.exe"
)

foreach ($path in $possiblePaths) {
    if (Test-Path $path) {
        $innoCompiler = $path
        break
    }
}

if ($innoCompiler -eq "") {
    Write-Host "`nInno Setup compiler not found!" -ForegroundColor Red
    Write-Host "Please install Inno Setup from: https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    Write-Host "Or specify the ISCC.exe path manually." -ForegroundColor Yellow
    Write-Host "`nSearched locations:" -ForegroundColor Cyan
    foreach ($path in $possiblePaths) {
        Write-Host "  - $path" -ForegroundColor White
    }
    exit 1
}

Write-Host "Found Inno Setup compiler: $innoCompiler" -ForegroundColor Green

# Compile the installer
Write-Host "`nCompiling installer with Inno Setup..." -ForegroundColor Yellow
$currentDir = Get-Location
Set-Location $sourceDir

$compileResult = & $innoCompiler "CAudioVisualizer.iss" 2>&1
$compileExitCode = $LASTEXITCODE

Set-Location $currentDir

if ($compileExitCode -eq 0) {
    $setupFile = "$outputDir\CAudioVisualizer-$Version-win-Setup.exe"
    if (Test-Path $setupFile) {
        $setupSize = [math]::Round((Get-Item $setupFile).Length / 1MB, 2)
        Write-Host "`n✅ Inno Setup installer created successfully!" -ForegroundColor Green
        Write-Host "File: $setupFile" -ForegroundColor Cyan
        Write-Host "Size: $setupSize MB" -ForegroundColor Cyan

        Write-Host "`nInstaller ready for distribution!" -ForegroundColor Green
    } else {
        Write-Host "`nSetup file not found at expected location!" -ForegroundColor Red
    }
} else {
    Write-Host "`nInno Setup compilation failed!" -ForegroundColor Red
    Write-Host "Compilation output:" -ForegroundColor Yellow
    Write-Host $compileResult -ForegroundColor White
    exit 1
}
