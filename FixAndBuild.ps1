$ROOT = "C:\VsCode\EduAutomation"

Write-Host "=== EduAutomation Fix + Build ===" -ForegroundColor Cyan

# FIX 1: Patch csproj to exclude test files
Write-Host "[1/4] Patching csproj..." -ForegroundColor Yellow
$csproj = "$ROOT\EduAutomation.csproj"
$c = Get-Content $csproj -Raw
if ($c -notmatch "AUTO-ADDED") {
    $block = "`n  <!-- AUTO-ADDED -->`n  <ItemGroup>`n    <Compile Remove=`"EduAutomation.Tests\**\*.cs`" />`n    <Compile Remove=`"EduAutomation.Test\**\*.cs`" />`n    <Compile Remove=`"**\*Tests.cs`" />`n    <Compile Remove=`"**\*Test.cs`" />`n  </ItemGroup>`n"
    $c = $c -replace "</Project>", "$block</Project>"
    Set-Content $csproj $c -Encoding UTF8
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "  Already patched." -ForegroundColor Green
}

# FIX 2: Add missing usings to GmailService.cs
Write-Host "[2/4] Patching missing using directives..." -ForegroundColor Yellow
$gmail = "$ROOT\GmailService.cs"
$gc = Get-Content $gmail -Raw
$usings = @(
    "using Google.Apis.Auth.OAuth2;",
    "using Google.Apis.Gmail.v1;",
    "using Google.Apis.Gmail.v1.Data;",
    "using Google.Apis.Services;",
    "using Google.Apis.Util.Store;"
)
foreach ($u in $usings) {
    if ($gc -notmatch [regex]::Escape($u)) {
        $gc = $u + "`n" + $gc
        Write-Host "  Added: $u" -ForegroundColor Green
    }
}
Set-Content $gmail $gc -Encoding UTF8

$canvas = "$ROOT\CanvasService.cs"
$cc = Get-Content $canvas -Raw
if ($cc -notmatch "using HtmlAgilityPack") {
    $cc = "using HtmlAgilityPack;`n" + $cc
    Set-Content $canvas $cc -Encoding UTF8
    Write-Host "  Added HtmlAgilityPack to CanvasService.cs" -ForegroundColor Green
}

$ic = "$ROOT\InfiniteCampusService.cs"
$icc = Get-Content $ic -Raw
if ($icc -notmatch "using HtmlAgilityPack") {
    $icc = "using HtmlAgilityPack;`n" + $icc
    Set-Content $ic $icc -Encoding UTF8
    Write-Host "  Added HtmlAgilityPack to InfiniteCampusService.cs" -ForegroundColor Green
}

# FIX 3: Fix XAML
Write-Host "[3/4] Fixing XAML..." -ForegroundColor Yellow
$xaml = "$ROOT\DataDumpAndGmailPages.xaml"
if (Test-Path $xaml) {
    $lines = Get-Content $xaml
    $out = [System.Collections.Generic.List[string]]::new()
    $first = $true
    foreach ($line in $lines) {
        if ($line.TrimStart().StartsWith("<?xml") -and -not $first) {
            Write-Host "  Removed stray XML declaration." -ForegroundColor Green
        } else {
            $out.Add($line)
        }
        $first = $false
    }
    $xt = $out -join "`n"
    if ($xt -match "models:" -and $xt -notmatch "xmlns:models") {
        $xt = $xt -replace "(<ContentPage)", "`$1`n    xmlns:models=`"clr-namespace:EduAutomation.Models`""
        Write-Host "  Added xmlns:models." -ForegroundColor Green
    }
    if ($xt -match "viewmodels:" -and $xt -notmatch "xmlns:viewmodels") {
        $xt = $xt -replace "(<ContentPage)", "`$1`n    xmlns:viewmodels=`"clr-namespace:EduAutomation.ViewModels`""
        Write-Host "  Added xmlns:viewmodels." -ForegroundColor Green
    }
    Set-Content $xaml $xt -Encoding UTF8
    Write-Host "  XAML done." -ForegroundColor Green
}

# FIX 4: Restore and Build
Write-Host "[4/4] Restoring and building..." -ForegroundColor Yellow
dotnet nuget locals all --clear | Out-Null
dotnet restore $csproj
if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed!" -ForegroundColor Red; exit 1 }

dotnet build $csproj -f net8.0-windows10.0.19041.0 --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed. See errors above." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== BUILD SUCCEEDED ===" -ForegroundColor Green
