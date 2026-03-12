@echo off
setlocal EnableDelayedExpansion
title EduAutomation Setup

echo.
echo ============================================================
echo   EduAutomation - Full Setup and Build Script
echo ============================================================
echo.

:: ── Locate project root (folder containing this bat file) ────
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "MAIN=%ROOT%\EduAutomation"
set "TESTS=%ROOT%\EduAutomation.Tests"
set "LOG=%ROOT%\setup_log.txt"

echo Setup started: %DATE% %TIME% > "%LOG%"
echo Root: %ROOT% >> "%LOG%"

:: ── Step 1: Check .NET SDK ────────────────────────────────────
echo [1/9] Checking .NET SDK...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found.
    echo         Install from: https://dotnet.microsoft.com/download
    echo         Minimum version required: .NET 8
    pause & exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo       .NET SDK version: %DOTNET_VER%
echo .NET version: %DOTNET_VER% >> "%LOG%"

:: ── Step 2: Install/update MAUI workload ─────────────────────
echo [2/9] Installing MAUI workload (this may take a few minutes)...
dotnet workload install maui >> "%LOG%" 2>&1
if errorlevel 1 (
    echo [WARN] workload install returned non-zero. Attempting update...
    dotnet workload update >> "%LOG%" 2>&1
)
echo       MAUI workload ready.

:: ── Step 3: Fix project structure ────────────────────────────
echo [3/9] Fixing project folder structure...

:: Create Tests folder at correct level if missing
if not exist "%TESTS%" (
    mkdir "%TESTS%"
    echo       Created EduAutomation.Tests folder.
)

:: Move test file if accidentally inside main project
set "MISPLACED1=%MAIN%\EduAutomation.Tests\ServiceAndViewModelTests.cs"
set "MISPLACED2=%MAIN%\EduAutomation.Test\ServiceAndViewModelTests.cs"
set "MISPLACED3=%MAIN%\ServiceAndViewModelTests.cs"

if exist "%MISPLACED1%" (
    copy /Y "%MISPLACED1%" "%TESTS%\ServiceAndViewModelTests.cs" >nul
    del "%MISPLACED1%"
    rmdir "%MAIN%\EduAutomation.Tests" 2>nul
    echo       Moved test file from EduAutomation.Tests subfolder.
)
if exist "%MISPLACED2%" (
    copy /Y "%MISPLACED2%" "%TESTS%\ServiceAndViewModelTests.cs" >nul
    del "%MISPLACED2%"
    rmdir "%MAIN%\EduAutomation.Test" 2>nul
    echo       Moved test file from EduAutomation.Test subfolder.
)
if exist "%MISPLACED3%" (
    copy /Y "%MISPLACED3%" "%TESTS%\ServiceAndViewModelTests.cs" >nul
    del "%MISPLACED3%"
    echo       Moved test file from main project root.
)

:: ── Step 4: Patch C# files with missing using directives ─────
echo [4/9] Patching missing using directives...

powershell -NoProfile -Command "
function Add-UsingIfMissing {
    param([string]$file, [string]$using)
    if (-not (Test-Path $file)) { Write-Host \"  SKIP (not found): $file\"; return }
    $content = Get-Content $file -Raw
    if ($content -notmatch [regex]::Escape($using)) {
        # Insert after the last existing 'using' line
        $lines = Get-Content $file
        $lastUsing = ($lines | Select-String '^using ' | Select-Object -Last 1).LineNumber
        if ($lastUsing) {
            $lines = $lines[0..($lastUsing-1)] + $using + $lines[$lastUsing..($lines.Length-1)]
        } else {
            $lines = @($using) + $lines
        }
        $lines | Set-Content $file
        Write-Host \"  Added: $using -> $([System.IO.Path]::GetFileName($file))\"
    }
}

\$main = '%MAIN%'

# GmailService.cs
Add-UsingIfMissing \"\$main\GmailService.cs\" 'using Google.Apis.Auth.OAuth2;'
Add-UsingIfMissing \"\$main\GmailService.cs\" 'using Google.Apis.Gmail.v1;'
Add-UsingIfMissing \"\$main\GmailService.cs\" 'using Google.Apis.Gmail.v1.Data;'
Add-UsingIfMissing \"\$main\GmailService.cs\" 'using Google.Apis.Services;'
Add-UsingIfMissing \"\$main\GmailService.cs\" 'using Google.Apis.Util.Store;'

# InfiniteCampusService.cs
Add-UsingIfMissing \"\$main\InfiniteCampusService.cs\" 'using HtmlAgilityPack;'

# CanvasService.cs
Add-UsingIfMissing \"\$main\CanvasService.cs\" 'using HtmlAgilityPack;'
"

:: ── Step 5: Fix XAML namespace issue ─────────────────────────
echo [5/9] Fixing XAML namespace declarations...

powershell -NoProfile -Command "
\$xamlFile = '%MAIN%\DataDumpAndGmailPages.xaml'
if (-not (Test-Path \$xamlFile)) {
    Write-Host '  SKIP: DataDumpAndGmailPages.xaml not found'
    exit
}
\$content = Get-Content \$xamlFile -Raw

# Remove stray XML declarations that appear mid-file (line 113 issue)
\$lines = Get-Content \$xamlFile
\$fixed = @()
\$firstLine = \$true
foreach (\$line in \$lines) {
    if (\$line.TrimStart().StartsWith('<?xml') -and -not \$firstLine) {
        Write-Host '  Removed stray XML declaration.'
    } else {
        \$fixed += \$line
    }
    \$firstLine = \$false
}

# Add missing xmlns:models if needed
\$fixedContent = \$fixed -join \"\`n\"
if (\$fixedContent -match 'models:' -and \$fixedContent -notmatch 'xmlns:models') {
    \$fixedContent = \$fixedContent -replace '(<ContentPage\b)', \"`\$1\`n    xmlns:models=\"\"clr-namespace:EduAutomation.Models\"\"\"
    Write-Host '  Added xmlns:models namespace.'
}

# Add missing xmlns:viewmodels if needed
if (\$fixedContent -match 'viewmodels:' -and \$fixedContent -notmatch 'xmlns:viewmodels') {
    \$fixedContent = \$fixedContent -replace '(<ContentPage\b)', \"`\$1\`n    xmlns:viewmodels=\"\"clr-namespace:EduAutomation.ViewModels\"\"\"
    Write-Host '  Added xmlns:viewmodels namespace.'
}

Set-Content \$xamlFile \$fixedContent -NoNewline
Write-Host '  XAML file patched.'
"

:: ── Step 6: Patch EduAutomation.csproj to exclude Tests ──────
echo [6/9] Patching main .csproj to exclude test files...

powershell -NoProfile -Command "
\$csproj = '%MAIN%\EduAutomation.csproj'
if (-not (Test-Path \$csproj)) { Write-Host 'SKIP: csproj not found'; exit }
\$content = Get-Content \$csproj -Raw

\$exclusion = @'

  <!-- Exclude test files that may have been placed in wrong folder -->
  <ItemGroup>
    <Compile Remove=\"EduAutomation.Tests\**\" />
    <Compile Remove=\"EduAutomation.Test\**\" />
    <Compile Remove=\"**\*Tests.cs\" />
    <Compile Remove=\"**\*Test.cs\" />
  </ItemGroup>

'@

if (\$content -notmatch 'Exclude test files') {
    \$content = \$content -replace '</Project>', \"\$exclusion</Project>\"
    Set-Content \$csproj \$content -NoNewline
    Write-Host '  Added test file exclusions to csproj.'
} else {
    Write-Host '  Exclusions already present.'
}
"

:: ── Step 7: Clear cache and restore ──────────────────────────
echo [7/9] Clearing NuGet cache and restoring packages...
dotnet nuget locals all --clear >> "%LOG%" 2>&1
echo       Cache cleared.

dotnet restore "%MAIN%\EduAutomation.csproj" >> "%LOG%" 2>&1
if errorlevel 1 (
    echo [ERROR] Restore failed for main project. Check %LOG% for details.
    pause & exit /b 1
)

if exist "%TESTS%\EduAutomation.Tests.csproj" (
    dotnet restore "%TESTS%\EduAutomation.Tests.csproj" >> "%LOG%" 2>&1
)
echo       Packages restored.

:: ── Step 8: Build ─────────────────────────────────────────────
echo [8/9] Building EduAutomation (Windows target)...
dotnet build "%MAIN%\EduAutomation.csproj" -f net8.0-windows10.0.19041.0 --no-restore >> "%LOG%" 2>&1

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. Showing last 40 lines of log:
    echo ──────────────────────────────────────────────────────
    powershell -Command "Get-Content '%LOG%' | Select-Object -Last 40"
    echo ──────────────────────────────────────────────────────
    echo Full log saved to: %LOG%
    pause & exit /b 1
)
echo       Build succeeded!

:: ── Step 9: Run unit tests ────────────────────────────────────
echo [9/9] Running unit tests...
if exist "%TESTS%\EduAutomation.Tests.csproj" (
    dotnet test "%TESTS%\EduAutomation.Tests.csproj" -v minimal >> "%LOG%" 2>&1
    if errorlevel 1 (
        echo [WARN] Some tests failed. Check %LOG% for details.
    ) else (
        echo       All tests passed!
    )
) else (
    echo [WARN] EduAutomation.Tests.csproj not found at %TESTS%
    echo        Tests skipped.
)

:: ── Done ──────────────────────────────────────────────────────
echo.
echo ============================================================
echo   Setup Complete!
echo ============================================================
echo.
echo   NEXT STEPS:
echo   ───────────────────────────────────────────────────────
echo   1. Gmail (if using personal Gmail forward):
echo      - Go to school Gmail Settings ^> Forwarding
echo      - Forward all mail to your personal Gmail
echo      - Authenticate with personal Gmail in the app
echo.
echo   2. Add google_credentials.json:
echo      - Go to console.cloud.google.com
echo      - Create project ^> Enable Gmail API
echo      - Create OAuth credentials (Desktop app)
echo      - Download and rename to google_credentials.json
echo      - Place in: EduAutomation\Resources\Raw\
echo      - Set Build Action = MauiAsset in Visual Studio
echo.
echo   3. Launch the app and open Settings to enter:
echo      - Canvas URL  (e.g. https://school.instructure.com)
echo      - Canvas username + password
echo      - Infinite Campus URL + username + password
echo      - OpenAI API key (from platform.openai.com)
echo.
echo   4. Full log saved to: %LOG%
echo ============================================================
echo.
pause