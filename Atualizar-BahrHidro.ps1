$ErrorActionPreference = "Stop"
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  BAHR HIDRO - INSTALADOR E ATUALIZADOR (REVIT 2026) " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

$RepoDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoDir

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Host "[AVISO] O Autodesk Revit esta aberto!" -ForegroundColor Yellow
    Write-Host "Feche o Revit antes de continuar para liberar as DLLs do plugin." -ForegroundColor Yellow
    Read-Host "Pressione ENTER apos fechar o Revit"
}

if (Test-Path (Join-Path $RepoDir ".git")) {
    Write-Host "[1/4] Baixando ultimas atualizacoes do GitHub..." -ForegroundColor Green
    git pull origin main
}

Write-Host "[2/4] Compilando Bahr Hidro (.NET 8 Windows)..." -ForegroundColor Green
dotnet build "BahrHidro.Revit\BahrHidro.Revit.csproj" -c Release

$BuildOut = Join-Path $RepoDir "BahrHidro.Revit\bin\Release\net8.0-windows"
if (-not (Test-Path $BuildOut)) {
    $BuildOut = Get-ChildItem -Path (Join-Path $RepoDir "BahrHidro.Revit\bin\Release") -Directory | Select-Object -First 1 -ExpandProperty FullName
}

$AppDataBahr = Join-Path $env:APPDATA "BahrHidro"
$BinTarget = Join-Path $AppDataBahr "Bin"
New-Item -ItemType Directory -Force -Path $BinTarget | Out-Null

Write-Host "[3/4] Instalando DLLs em $BinTarget..." -ForegroundColor Green
Copy-Item -Path "$BuildOut\*" -Destination $BinTarget -Recurse -Force

$RevitAddinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026"
New-Item -ItemType Directory -Force -Path $RevitAddinsDir | Out-Null
$AddinManifestPath = Join-Path $RevitAddinsDir "BahrHidro.addin"
$AssemblyDllPath = Join-Path $BinTarget "BahrHidro.Revit.dll"

$AddinXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Bahr Hidro</Name>
    <Assembly>$AssemblyDllPath</Assembly>
    <AddInId>12345678-ABCD-1234-ABCD-1234567890AB</AddInId>
    <FullClassName>BahrHidro.Revit.MainCommand</FullClassName>
    <VendorId>BAHR</VendorId>
    <VendorDescription>Bahr Engenharia - Hidraulica e LSF (ABNT NBR 5626 / 8160)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path $AddinManifestPath -Value $AddinXml -Encoding UTF8

Write-Host "[4/4] Registrando versao instalada..." -ForegroundColor Green
$Sha = ""
if (Test-Path (Join-Path $RepoDir ".git")) {
    $Sha = (git rev-parse --short HEAD).Trim()
}
$StateObj = @{
    InstalledSha = $Sha
    InstalledDate = (Get-Date).ToString("dd/MM/yyyy HH:mm")
    RepoLocalPath = $RepoDir
} | ConvertTo-Json
Set-Content -Path (Join-Path $AppDataBahr "version_state.json") -Value $StateObj -Encoding UTF8

Write-Host ""
Write-Host "CONCLUIDO COM SUCESSO! Classe [BahrHidro.Revit.MainCommand] registrada no Revit 2026." -ForegroundColor Green
