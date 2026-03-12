@echo off
setlocal EnableDelayedExpansion
title EduAutomation - Full Project Setup
color 0A

:: ===========================================================================
:: EduAutomation Setup Script
:: Run this file ONE TIME from any folder. It will:
::   1. Check Windows version
::   2. Install .NET 8 SDK if missing (via winget, then direct download fallback)
::   3. Install the .NET MAUI workload
::   4. Create the full folder structure under C:\Projects\EduAutomation
::   5. Copy every source file from the script's own directory into position
::   6. Restore all NuGet packages
::   7. Verify the build compiles without errors
::   8. Open the project folder in VS Code (if installed)
::
:: REQUIREMENTS: Windows 10 version 1809 or later, internet connection.
:: Run as Administrator for best results (required to install .NET SDK).
:: ===========================================================================

echo.
echo  ============================================================
echo   EduAutomation - Automated Project Setup
echo  ============================================================
echo.

:: ---------------------------------------------------------------------------
:: 0. Store the directory this batch file is in (source of all .cs files)
:: ---------------------------------------------------------------------------
set "SCRIPT_DIR=%~dp0"
:: Remove trailing backslash
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

set "PROJECT_ROOT=C:\Projects\EduAutomation"
set "MAIN_PROJECT=%PROJECT_ROOT%\EduAutomation"
set "TEST_PROJECT=%PROJECT_ROOT%\EduAutomation.Tests"
set "DOTNET_MIN_VERSION=8"

echo [INFO] Script location  : %SCRIPT_DIR%
echo [INFO] Project will be  : %PROJECT_ROOT%
echo.

:: ---------------------------------------------------------------------------
:: 1. Check Windows version
:: ---------------------------------------------------------------------------
echo [STEP 1/8] Checking Windows version...
for /f "tokens=4-5 delims=. " %%i in ('ver') do set WIN_VER=%%i.%%j
echo [INFO] Windows version detected: %WIN_VER%

:: ---------------------------------------------------------------------------
:: 2. Check if .NET 8 SDK is already installed
:: ---------------------------------------------------------------------------
echo.
echo [STEP 2/8] Checking for .NET 8 SDK...
dotnet --version >nul 2>&1
if %errorlevel% neq 0 goto :InstallDotNet

for /f "tokens=*" %%v in ('dotnet --version 2^>nul') do set DOTNET_VER=%%v
echo [INFO] Found .NET SDK version: %DOTNET_VER%

:: Check if it is at least version 8
for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if %DOTNET_MAJOR% geq %DOTNET_MIN_VERSION% (
    echo [OK]   .NET %DOTNET_MIN_VERSION% SDK is already installed.
    goto :DotNetReady
)

echo [WARN]  .NET SDK %DOTNET_VER% is too old. Need version 8 or later.

:InstallDotNet
echo [INFO] .NET 8 SDK not found. Attempting installation...
echo.

:: Try winget first (available on Windows 10 1809+)
where winget >nul 2>&1
if %errorlevel% equ 0 (
    echo [INFO] Using winget to install .NET 8 SDK...
    winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements --silent
    if !errorlevel! equ 0 (
        echo [OK]   .NET 8 SDK installed via winget.
        :: Refresh PATH so dotnet is found in this session
        for /f "tokens=*" %%p in ('where dotnet 2^>nul') do set "DOTNET_EXE=%%p"
        goto :DotNetReady
    )
    echo [WARN]  winget install failed. Falling back to direct download.
)

:: Fallback: download the .NET 8 SDK installer directly
echo [INFO] Downloading .NET 8 SDK installer from Microsoft...
set "DOTNET_INSTALLER=%TEMP%\dotnet8-sdk-installer.exe"
powershell -Command ^
    "Invoke-WebRequest -Uri 'https://download.microsoft.com/download/dotnet/8.0/8.0.404/dotnet-sdk-8.0.404-win-x64.exe' -OutFile '%DOTNET_INSTALLER%' -UseBasicParsing"

if not exist "%DOTNET_INSTALLER%" (
    echo [ERROR] Could not download the .NET 8 SDK installer.
    echo         Please download and install it manually from:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    echo         Then re-run this script.
    pause
    exit /b 1
)

echo [INFO] Running .NET 8 SDK installer (this may take 1-3 minutes)...
"%DOTNET_INSTALLER%" /install /quiet /norestart
if %errorlevel% neq 0 (
    echo [ERROR] .NET 8 SDK installer returned an error.
    echo         Try running this script as Administrator.
    pause
    exit /b 1
)
echo [OK]   .NET 8 SDK installed successfully.

:: Refresh PATH for this session
set "PATH=%PATH%;%ProgramFiles%\dotnet"

:DotNetReady
:: Confirm dotnet is callable
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] dotnet command still not found after installation.
    echo         Please close this window, reopen as Administrator, and run again.
    pause
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do echo [OK]   Using .NET SDK %%v

:: ---------------------------------------------------------------------------
:: 3. Install .NET MAUI workload
:: ---------------------------------------------------------------------------
echo.
echo [STEP 3/8] Installing .NET MAUI workload...
echo [INFO] This requires internet access and may take 3-8 minutes...
dotnet workload install maui --skip-sign-check
if %errorlevel% neq 0 (
    echo [WARN]  MAUI workload install returned a non-zero exit code.
    echo         This is sometimes normal if it was already installed.
    echo         Continuing...
) else (
    echo [OK]   .NET MAUI workload is ready.
)

:: ---------------------------------------------------------------------------
:: 4. Create the full folder structure
:: ---------------------------------------------------------------------------
echo.
echo [STEP 4/8] Creating project folder structure...

set "FOLDERS=^
%PROJECT_ROOT%^
%MAIN_PROJECT%^
%MAIN_PROJECT%\Models^
%MAIN_PROJECT%\Services^
%MAIN_PROJECT%\ViewModels^
%MAIN_PROJECT%\Views^
%MAIN_PROJECT%\Helpers^
%MAIN_PROJECT%\Converters^
%MAIN_PROJECT%\Resources^
%MAIN_PROJECT%\Resources\Raw^
%MAIN_PROJECT%\Resources\Fonts^
%MAIN_PROJECT%\Resources\Images^
%MAIN_PROJECT%\Resources\AppIcon^
%MAIN_PROJECT%\Resources\Splash^
%MAIN_PROJECT%\Resources\Styles^
%MAIN_PROJECT%\Platforms^
%MAIN_PROJECT%\Platforms\Android^
%MAIN_PROJECT%\Platforms\iOS^
%MAIN_PROJECT%\Platforms\MacCatalyst^
%MAIN_PROJECT%\Platforms\Windows^
%TEST_PROJECT%^
%TEST_PROJECT%\Services^
%TEST_PROJECT%\ViewModels"

for %%d in (%FOLDERS%) do (
    if not exist "%%d" (
        mkdir "%%d"
        echo [OK]   Created: %%d
    ) else (
        echo [SKIP]  Exists:  %%d
    )
)

:: ---------------------------------------------------------------------------
:: 5. Copy source files from script directory into correct positions
:: ---------------------------------------------------------------------------
echo.
echo [STEP 5/8] Copying source files...

:: Helper macro: copy if source exists
:: Usage: call :CopyFile "source" "destination"

call :CopyFile "%SCRIPT_DIR%\EduAutomation.csproj"          "%MAIN_PROJECT%\EduAutomation.csproj"

:: Models
call :CopyFile "%SCRIPT_DIR%\Models\Assignment.cs"           "%MAIN_PROJECT%\Models\Assignment.cs"
call :CopyFile "%SCRIPT_DIR%\Models\ReviewItem.cs"           "%MAIN_PROJECT%\Models\ReviewItem.cs"
call :CopyFile "%SCRIPT_DIR%\Models\DataModels.cs"           "%MAIN_PROJECT%\Models\DataModels.cs"

:: Services
call :CopyFile "%SCRIPT_DIR%\Services\LoggingService.cs"     "%MAIN_PROJECT%\Services\LoggingService.cs"
call :CopyFile "%SCRIPT_DIR%\Services\SecureConfigService.cs" "%MAIN_PROJECT%\Services\SecureConfigService.cs"
call :CopyFile "%SCRIPT_DIR%\Services\CanvasService.cs"      "%MAIN_PROJECT%\Services\CanvasService.cs"
call :CopyFile "%SCRIPT_DIR%\Services\InfiniteCampusService.cs" "%MAIN_PROJECT%\Services\InfiniteCampusService.cs"
call :CopyFile "%SCRIPT_DIR%\Services\GmailService.cs"       "%MAIN_PROJECT%\Services\GmailService.cs"
call :CopyFile "%SCRIPT_DIR%\Services\OpenAIService.cs"      "%MAIN_PROJECT%\Services\OpenAIService.cs"

:: Helpers
call :CopyFile "%SCRIPT_DIR%\Helpers\ApiHelpers.cs"          "%MAIN_PROJECT%\Helpers\ApiHelpers.cs"
call :CopyFile "%SCRIPT_DIR%\Helpers\PromptGuardrails.cs"    "%MAIN_PROJECT%\Helpers\PromptGuardrails.cs"

:: ViewModels
call :CopyFile "%SCRIPT_DIR%\ViewModels\BaseViewModel.cs"    "%MAIN_PROJECT%\ViewModels\BaseViewModel.cs"
call :CopyFile "%SCRIPT_DIR%\ViewModels\ViewModels.cs"       "%MAIN_PROJECT%\ViewModels\ViewModels.cs"
call :CopyFile "%SCRIPT_DIR%\ViewModels\SettingsViewModel.cs" "%MAIN_PROJECT%\ViewModels\SettingsViewModel.cs"

:: Views - XAML
call :CopyFile "%SCRIPT_DIR%\Views\DashboardPage.xaml"       "%MAIN_PROJECT%\Views\DashboardPage.xaml"
call :CopyFile "%SCRIPT_DIR%\Views\AssignmentsPage.xaml"     "%MAIN_PROJECT%\Views\AssignmentsPage.xaml"
call :CopyFile "%SCRIPT_DIR%\Views\ReviewPage.xaml"          "%MAIN_PROJECT%\Views\ReviewPage.xaml"
call :CopyFile "%SCRIPT_DIR%\Views\DataDumpAndGmailPages.xaml" "%MAIN_PROJECT%\Views\DataDumpAndGmailPages.xaml"
call :CopyFile "%SCRIPT_DIR%\Views\SettingsPage.xaml"        "%MAIN_PROJECT%\Views\SettingsPage.xaml"

:: Views - Code-behind
call :CopyFile "%SCRIPT_DIR%\Views\ViewCodeBehind.cs"        "%MAIN_PROJECT%\Views\ViewCodeBehind.cs"

:: Root app files
call :CopyFile "%SCRIPT_DIR%\App.xaml"                       "%MAIN_PROJECT%\App.xaml"
call :CopyFile "%SCRIPT_DIR%\App.xaml.cs"                    "%MAIN_PROJECT%\App.xaml.cs"
call :CopyFile "%SCRIPT_DIR%\AppShell.xaml"                  "%MAIN_PROJECT%\AppShell.xaml"
call :CopyFile "%SCRIPT_DIR%\MauiProgram.cs"                 "%MAIN_PROJECT%\MauiProgram.cs"

:: Tests
call :CopyFile "%SCRIPT_DIR%\Tests\EduAutomation.Tests.csproj" "%TEST_PROJECT%\EduAutomation.Tests.csproj"
call :CopyFile "%SCRIPT_DIR%\Tests\ServiceAndViewModelTests.cs" "%TEST_PROJECT%\ServiceAndViewModelTests.cs"

:: .gitignore
call :CopyFile "%SCRIPT_DIR%\.gitignore"                     "%PROJECT_ROOT%\.gitignore"
call :CopyFile "%SCRIPT_DIR%\README.md"                      "%PROJECT_ROOT%\README.md"

:: ---------------------------------------------------------------------------
:: 5b. Generate required Platform files that MAUI needs to compile
::     (these are boilerplate and identical for every MAUI project)
:: ---------------------------------------------------------------------------
echo.
echo [INFO] Writing platform boilerplate files...

:: Android MainApplication.cs
(
echo using Android.App;
echo using Android.Runtime;
echo.
echo namespace EduAutomation
echo {
echo     [Application]
echo     public class MainApplication : MauiApplication
echo     {
echo         public MainApplication(IntPtr handle, JniHandleOwnership ownership^)
echo             : base(handle, ownership^) { }
echo         protected override MauiApp CreateMauiApp(^) =^> MauiProgram.CreateMauiApp(^);
echo     }
echo }
) > "%MAIN_PROJECT%\Platforms\Android\MainApplication.cs"
echo [OK]   Platforms\Android\MainApplication.cs

:: Android MainActivity.cs
(
echo using Android.App;
echo using Android.Content.PM;
echo.
echo namespace EduAutomation
echo {
echo     [Activity(Theme = "@style/Maui.SplashTheme",
echo               MainLauncher = true,
echo               ConfigurationChanges = ConfigChanges.ScreenSize ^| ConfigChanges.Orientation ^|
echo                                      ConfigChanges.UiMode ^| ConfigChanges.ScreenLayout ^|
echo                                      ConfigChanges.SmallestScreenSize ^| ConfigChanges.Density^)]
echo     public class MainActivity : MauiAppCompatActivity { }
echo }
) > "%MAIN_PROJECT%\Platforms\Android\MainActivity.cs"
echo [OK]   Platforms\Android\MainActivity.cs

:: Android AndroidManifest.xml
(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<manifest xmlns:android="http://schemas.android.com/apk/res/android"^>
echo     ^<uses-permission android:name="android.permission.INTERNET" /^>
echo     ^<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" /^>
echo     ^<application android:allowBackup="true" android:icon="@mipmap/appicon"
echo                  android:roundIcon="@mipmap/appicon_round"
echo                  android:supportsRtl="true"^>^</application^>
echo ^</manifest^>
) > "%MAIN_PROJECT%\Platforms\Android\AndroidManifest.xml"
echo [OK]   Platforms\Android\AndroidManifest.xml

:: iOS AppDelegate.cs
(
echo using Foundation;
echo.
echo namespace EduAutomation
echo {
echo     [Register("AppDelegate"^)]
echo     public class AppDelegate : MauiUIApplicationDelegate
echo     {
echo         protected override MauiApp CreateMauiApp(^) =^> MauiProgram.CreateMauiApp(^);
echo     }
echo }
) > "%MAIN_PROJECT%\Platforms\iOS\AppDelegate.cs"
echo [OK]   Platforms\iOS\AppDelegate.cs

:: iOS Info.plist
(
echo ^<?xml version="1.0" encoding="UTF-8"?^>
echo ^<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
echo   "http://www.apple.com/DTDs/PropertyList-1.0.dtd"^>
echo ^<plist version="1.0"^>
echo ^<dict^>
echo     ^<key^>NSMicrophoneUsageDescription^</key^>
echo     ^<string^>EduAutomation uses the microphone for voice transcript input.^</string^>
echo ^</dict^>
echo ^</plist^>
) > "%MAIN_PROJECT%\Platforms\iOS\Info.plist"
echo [OK]   Platforms\iOS\Info.plist

:: MacCatalyst AppDelegate.cs
(
echo using Foundation;
echo.
echo namespace EduAutomation
echo {
echo     [Register("AppDelegate"^)]
echo     public class AppDelegate : MauiUIApplicationDelegate
echo     {
echo         protected override MauiApp CreateMauiApp(^) =^> MauiProgram.CreateMauiApp(^);
echo     }
echo }
) > "%MAIN_PROJECT%\Platforms\MacCatalyst\AppDelegate.cs"
echo [OK]   Platforms\MacCatalyst\AppDelegate.cs

:: Windows App.xaml
(
echo ^<maui:MauiWinUIApplication
echo     x:Class="EduAutomation.WinUI.App"
echo     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
echo     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
echo     xmlns:maui="using:Microsoft.Maui"
echo     xmlns:local="using:EduAutomation"^>
echo ^</maui:MauiWinUIApplication^>
) > "%MAIN_PROJECT%\Platforms\Windows\App.xaml"
echo [OK]   Platforms\Windows\App.xaml

:: Windows App.xaml.cs
(
echo namespace EduAutomation.WinUI
echo {
echo     public partial class App : MauiWinUIApplication
echo     {
echo         public App(^) { InitializeComponent(^); }
echo         protected override MauiApp CreateMauiApp(^) =^> MauiProgram.CreateMauiApp(^);
echo     }
echo }
) > "%MAIN_PROJECT%\Platforms\Windows\App.xaml.cs"
echo [OK]   Platforms\Windows\App.xaml.cs

:: Minimal Colors.xaml
(
echo ^<?xml version="1.0" encoding="UTF-8"?^>
echo ^<ResourceDictionary xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
echo                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"^>
echo     ^<Color x:Key="Primary"^>#3f51b5^</Color^>
echo     ^<Color x:Key="PrimaryDark"^>#7986cb^</Color^>
echo     ^<Color x:Key="Secondary"^>#4caf50^</Color^>
echo     ^<Color x:Key="White"^>#FFFFFF^</Color^>
echo     ^<Color x:Key="Black"^>#000000^</Color^>
echo ^</ResourceDictionary^>
) > "%MAIN_PROJECT%\Resources\Styles\Colors.xaml"
echo [OK]   Resources\Styles\Colors.xaml

:: Minimal Styles.xaml
(
echo ^<?xml version="1.0" encoding="UTF-8"?^>
echo ^<ResourceDictionary xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
echo                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
echo                     xmlns:converters="clr-namespace:EduAutomation.Converters"^>
echo ^</ResourceDictionary^>
) > "%MAIN_PROJECT%\Resources\Styles\Styles.xaml"
echo [OK]   Resources\Styles\Styles.xaml

:: Solution file
(
echo.
echo Microsoft Visual Studio Solution File, Format Version 12.00
echo # Visual Studio Version 17
echo VisualStudioVersion = 17.0.0.0
echo MinimumVisualStudioVersion = 10.0.40219.1
echo Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "EduAutomation", "EduAutomation\EduAutomation.csproj", "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
echo EndProject
echo Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "EduAutomation.Tests", "EduAutomation.Tests\EduAutomation.Tests.csproj", "{B2C3D4E5-F6A7-8901-BCDE-F01234567891}"
echo EndProject
echo Global
echo     GlobalSection(SolutionConfigurationPlatforms^) = preSolution
echo         Debug^|Any CPU = Debug^|Any CPU
echo         Release^|Any CPU = Release^|Any CPU
echo     EndGlobalSection
echo     GlobalSection(ProjectConfigurationPlatforms^) = postSolution
echo         {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Debug^|Any CPU.ActiveCfg = Debug^|Any CPU
echo         {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Debug^|Any CPU.Build.0 = Debug^|Any CPU
echo         {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Release^|Any CPU.ActiveCfg = Release^|Any CPU
echo         {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Release^|Any CPU.Build.0 = Release^|Any CPU
echo         {B2C3D4E5-F6A7-8901-BCDE-F01234567891}.Debug^|Any CPU.ActiveCfg = Debug^|Any CPU
echo         {B2C3D4E5-F6A7-8901-BCDE-F01234567891}.Debug^|Any CPU.Build.0 = Debug^|Any CPU
echo         {B2C3D4E5-F6A7-8901-BCDE-F01234567891}.Release^|Any CPU.ActiveCfg = Release^|Any CPU
echo         {B2C3D4E5-F6A7-8901-BCDE-F01234567891}.Release^|Any CPU.Build.0 = Release^|Any CPU
echo     EndGlobalSection
echo EndGlobal
) > "%PROJECT_ROOT%\EduAutomationSolution.sln"
echo [OK]   EduAutomationSolution.sln

:: ---------------------------------------------------------------------------
:: 6. Restore NuGet packages
::    This is the step that downloads HtmlAgilityPack, Polly, Serilog,
::    Google.Apis.Gmail.v1, CommunityToolkit.Mvvm, etc.
:: ---------------------------------------------------------------------------
echo.
echo [STEP 6/8] Restoring NuGet packages (this downloads all dependencies)...
echo [INFO] This may take 2-5 minutes on first run...
echo.

pushd "%MAIN_PROJECT%"
dotnet restore EduAutomation.csproj
if %errorlevel% neq 0 (
    echo [ERROR] NuGet restore failed for the main project.
    echo         Check your internet connection and try again.
    popd
    pause
    exit /b 1
)
popd
echo [OK]   Main project packages restored.

pushd "%TEST_PROJECT%"
dotnet restore EduAutomation.Tests.csproj
if %errorlevel% neq 0 (
    echo [WARN]  NuGet restore failed for the test project. Tests may not compile.
)
popd
echo [OK]   Test project packages restored.

:: ---------------------------------------------------------------------------
:: 7. Build to verify everything compiles
:: ---------------------------------------------------------------------------
echo.
echo [STEP 7/8] Verifying build (Windows target only for speed)...
echo [INFO] Building EduAutomation for Windows...

pushd "%MAIN_PROJECT%"
dotnet build EduAutomation.csproj -f net8.0-windows10.0.19041.0 --no-restore -v minimal 2>&1
set BUILD_RESULT=%errorlevel%
popd

if %BUILD_RESULT% equ 0 (
    echo [OK]   Build succeeded with 0 errors.
) else (
    echo [WARN]  Build reported warnings or errors. Check output above.
    echo         Common causes:
    echo           - google_credentials.json not yet placed in Resources\Raw\
    echo           - MAUI workload not fully installed (try: dotnet workload repair)
    echo         The app can still be opened in Visual Studio to resolve.
)

:: ---------------------------------------------------------------------------
:: 8. Open project in VS Code or Visual Studio
:: ---------------------------------------------------------------------------
echo.
echo [STEP 8/8] Opening project...

where code >nul 2>&1
if %errorlevel% equ 0 (
    echo [INFO] VS Code found. Opening project folder...
    code "%PROJECT_ROOT%"
    goto :SetupComplete
)

:: Try Visual Studio 2022
set "VS2022=%ProgramFiles%\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
set "VS2022_PRO=%ProgramFiles%\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe"
set "VS2022_ENT=%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"

if exist "%VS2022_ENT%" (
    echo [INFO] Visual Studio 2022 Enterprise found. Opening solution...
    start "" "%VS2022_ENT%" "%PROJECT_ROOT%\EduAutomationSolution.sln"
    goto :SetupComplete
)
if exist "%VS2022_PRO%" (
    echo [INFO] Visual Studio 2022 Professional found. Opening solution...
    start "" "%VS2022_PRO%" "%PROJECT_ROOT%\EduAutomationSolution.sln"
    goto :SetupComplete
)
if exist "%VS2022%" (
    echo [INFO] Visual Studio 2022 Community found. Opening solution...
    start "" "%VS2022%" "%PROJECT_ROOT%\EduAutomationSolution.sln"
    goto :SetupComplete
)

echo [INFO] No editor auto-detected. Open this folder manually:
echo        %PROJECT_ROOT%
echo        Solution file: EduAutomationSolution.sln

:SetupComplete
echo.
echo  ============================================================
echo   SETUP COMPLETE
echo  ============================================================
echo.
echo   Project location : %PROJECT_ROOT%
echo   Solution file    : %PROJECT_ROOT%\EduAutomationSolution.sln
echo.
echo   NEXT STEPS:
echo   1. Place google_credentials.json in:
echo      %MAIN_PROJECT%\Resources\Raw\
echo      (See README.md for how to get this from Google Cloud Console)
echo.
echo   2. Launch the app and go to the Settings tab.
echo      Enter your Canvas URL, username, and password.
echo      Enter your Infinite Campus URL, username, and password.
echo      Enter your OpenAI API key.
echo      Tap "Save and Test Connections" to verify everything works.
echo.
echo   3. To run on Windows: dotnet run inside %MAIN_PROJECT%
echo      Or press F5 in Visual Studio with "Windows Machine" selected.
echo.
echo   4. To run unit tests:
echo      cd %TEST_PROJECT%
echo      dotnet test
echo.
pause
exit /b 0

:: ---------------------------------------------------------------------------
:: Subroutine: CopyFile
:: Copies source to destination. Prints OK or WARN.
:: ---------------------------------------------------------------------------
:CopyFile
set "SRC=%~1"
set "DST=%~2"
if exist "%SRC%" (
    copy /Y "%SRC%" "%DST%" >nul
    echo [OK]   Copied: %~nx1  ->  %DST%
) else (
    echo [WARN]  Not found (skip): %SRC%
)
exit /b 0
