<#
.SYNOPSIS
    AllReaderBook v0.9.02-beta Standalone Portable Publisher (SingleFolder on E:\ or D:\, ReadyToRun, zero C:\ drive usage)
#>

param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\Publish\AllReaderBook_Portable_v0.9.02-beta"
)

$ErrorActionPreference = "Stop"

# 1. Isolate all CLI, NuGet and TEMP directories strictly on E:\ / D:\
$env:DOTNET_CLI_HOME = "$PSScriptRoot\.dotnet_home"
if (Test-Path "$PSScriptRoot\packages") {
    $env:NUGET_PACKAGES = "$PSScriptRoot\packages"
} else {
    $env:NUGET_PACKAGES = "$PSScriptRoot\..\packages"
}
$env:TEMP = "$PSScriptRoot\.temp"
$env:TMP = "$PSScriptRoot\.temp"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $OutputDir | Out-Null

# Clean previous output directory
Get-ChildItem -Path $OutputDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building AllReaderBook v0.9.02-beta Standalone Portable ($Runtime)" -ForegroundColor Cyan
Write-Host " ReadyToRun (AOT Precompilation) Enabled" -ForegroundColor Cyan
Write-Host " Output Directory: $OutputDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Publish AllReaderBook (AllReaderBook.csproj) with ReadyToRun enabled
dotnet publish "$PSScriptRoot\AllReaderBook.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish AllReaderBook failed with exit code $LASTEXITCODE"
}

# 3. Copy native pdfium.dll next to AllReaderBook.exe
$pdfiumCandidate = Get-ChildItem -Path "$PSScriptRoot\..\packages\bblanchon.pdfium.win32" -Filter "pdfium.dll" -Recurse |
    Where-Object { $_.FullName -match $Runtime } |
    Select-Object -First 1

if ($null -ne $pdfiumCandidate) {
    Copy-Item -Path $pdfiumCandidate.FullName -Destination (Join-Path $OutputDir "pdfium.dll") -Force
    Write-Host "[OK] Copied native PDFium engine: $($pdfiumCandidate.FullName)" -ForegroundColor Green
}

# 4. Generate clean default allreaderbook_settings.json (no bookhub.db or cataloger artifacts)
$defaultReaderSettings = @"
{
  "WindowWidth": 1360,
  "WindowHeight": 880,
  "WindowLeft": "NaN",
  "WindowTop": "NaN",
  "WindowState": "Maximized",
  "TocWidth": 285,
  "NotesWidth": 320,
  "IsTocVisible": true,
  "IsAnnotationsVisible": false,
  "ZoomMode": "FitWidth",
  "LastReaderViewMode": "ContinuousVertical",
  "ThemeName": "Obsidian Dark",
  "Language": "uk",
  "IsLicenseAccepted": false,
  "IsSmartPdfColorFilterEnabled": true,
  "RestorePreviousSessionOnStartup": true,
  "RecentFiles": [],
  "OpenSessionTabs": []
}
"@
[System.IO.File]::WriteAllText(
    (Join-Path $OutputDir "allreaderbook_settings.json"),
    $defaultReaderSettings,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "[OK] Created default allreaderbook_settings.json" -ForegroundColor Green

# 5. Copy standalone AllReaderBook documentation strictly from $PSScriptRoot
$readerDocDir = $PSScriptRoot
foreach ($docFile in @("LICENSE", "THIRD_PARTY_LICENSES.txt", "README.md", "PRIVACY.md", "CHANGELOG.md")) {
    $srcDoc = Join-Path $readerDocDir $docFile
    if (Test-Path $srcDoc) {
        Copy-Item -Path $srcDoc -Destination (Join-Path $OutputDir $docFile) -Force
        Write-Host "[OK] Included standalone AllReaderBook documentation file: $docFile" -ForegroundColor Green
    }
}

# 6. Ensure no database, debug symbols, libofbooks, or cataloger files exist in standalone reader package
Get-ChildItem -Path $OutputDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($unwanted in @(
    "bookhub.db", "bookhub.db-shm", "bookhub.db-wal", "settings.json", "reader_settings.json",
    "ReaderHub.exe", "ReaderHub.dll", "BookHub.App.exe", "BookHub.App.dll",
    "Dapper.dll", "Microsoft.Data.Sqlite.dll", "e_sqlite3.dll",
    "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
    "VirtualizingWrapPanel.dll", "libSkiaSharp.dll", "SkiaSharp.dll", "PDFtoImage.dll"
)) {
    $p = Join-Path $OutputDir $unwanted
    if (Test-Path $p) {
        Remove-Item -LiteralPath $p -Force
    }
}
foreach ($unwantedDir in @("libofbooks", "cache", "Backups", "runtimes")) {
    $d = Join-Path $OutputDir $unwantedDir
    if (Test-Path $d -PathType Container) {
        Remove-Item -LiteralPath $d -Recurse -Force
    }
}

$zipWinX64 = "$PSScriptRoot\Publish\AllReaderBook_Portable_v0.9.02-beta_$Runtime.zip"
$zipX64 = "$PSScriptRoot\Publish\AllReaderBook_Portable_v0.9.02-beta_x64.zip"
if (Test-Path $zipWinX64) { Remove-Item $zipWinX64 -Force }
if (Test-Path $zipX64) { Remove-Item $zipX64 -Force }
Compress-Archive -Path "$OutputDir\*" -DestinationPath $zipWinX64 -CompressionLevel Optimal -Force
Copy-Item -Path $zipWinX64 -Destination $zipX64 -Force

Write-Host ""
Write-Host "[SUCCESS] AllReaderBook_Portable_v0.9.02-beta created at:" -ForegroundColor Green
Write-Host "   $OutputDir" -ForegroundColor Yellow
Write-Host "   $zipWinX64" -ForegroundColor Yellow
Write-Host "   $zipX64" -ForegroundColor Yellow
