$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   INSTALADOR AUTOMATICO - BAHR HIDRO BIM (REVIT 2026)    " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host "[AVISO] O Autodesk Revit esta aberto no momento." -ForegroundColor Yellow
    $resp = Read-Host "Deseja fechar o Revit automaticamente para continuar? (S/N)"
    if ($resp -match "^[Ss]") {
        Stop-Process -Name Revit -Force
        Start-Sleep -Seconds 2
    } else {
        throw "Feche o Revit 2026 antes de instalar ou atualizar o plugin."
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$localBin = Join-Path $scriptDir "Bin"
$installRoot = Join-Path $env:APPDATA "BahrHidro"
$targetBin = Join-Path $installRoot "Bin"
$addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026"
$addinFile = Join-Path $addinDir "BahrHidro.addin"

New-Item -ItemType Directory -Path $targetBin -Force | Out-Null
New-Item -ItemType Directory -Path $addinDir -Force | Out-Null

if (Test-Path (Join-Path $localBin "BahrHidro.Revit.dll")) {
    Write-Host "[1/3] Copiando binarios pre-compilados locais..." -ForegroundColor White
    Remove-Item "$targetBin\*" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item "$localBin\*" $targetBin -Recurse -Force
} else {
    Write-Host "[1/3] Baixando ultima versao compilada do GitHub..." -ForegroundColor White
    $tmpDir = Join-Path $env:TEMP ("BahrHidro_Setup_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
    $releaseZip = Join-Path $tmpDir "BahrHidro-Revit2026-Win64.zip"
    $releaseUrl = "https://github.com/ChefeIndio/BAHRHIDRO/releases/latest/download/BahrHidro-Revit2026-Win64.zip"

    try {
        Invoke-WebRequest -Uri $releaseUrl -OutFile $releaseZip -UseBasicParsing
        Expand-Archive -Path $releaseZip -DestinationPath $tmpDir -Force
        $downloadedBin = Join-Path $tmpDir "Bin"
        if (-not (Test-Path (Join-Path $downloadedBin "BahrHidro.Revit.dll"))) {
            throw "Pacote de release incompleto."
        }
        Remove-Item "$targetBin\*" -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item "$downloadedBin\*" $targetBin -Recurse -Force
    } catch {
        Write-Host "[INFO] Release binaria indisponivel. Baixando codigo-fonte e compilando..." -ForegroundColor Yellow
        $srcZip = Join-Path $tmpDir "main.zip"
        Invoke-WebRequest -Uri "https://github.com/ChefeIndio/BAHRHIDRO/archive/refs/heads/main.zip" -OutFile $srcZip -UseBasicParsing
        Expand-Archive -Path $srcZip -DestinationPath $tmpDir -Force
        $srcFolder = (Get-ChildItem $tmpDir -Directory | Where-Object { $_.Name -like "BAHRHIDRO*" } | Select-Object -First 1).FullName
        dotnet build "$srcFolder\BahrHidro.Revit\BahrHidro.Revit.csproj" -c Release --no-incremental
        $builtOut = (Get-ChildItem "$srcFolder\BahrHidro.Revit\bin\Release" -Directory | Select-Object -First 1).FullName
        Remove-Item "$targetBin\*" -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item "$builtOut\*" $targetBin -Recurse -Force
    } finally {
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "[2/3] Registrando manifesto BahrHidro.addin no Revit 2026..." -ForegroundColor White
$dllPath = Join-Path $targetBin "BahrHidro.Revit.dll"
$addinXml = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Bahr Hidro - Dimensionamento BIM</Name>
    <Assembly>$dllPath</Assembly>
    <AddInId>12345678-ABCD-1234-ABCD-1234567890AB</AddInId>
    <FullClassName>BahrHidro.Revit.BahrHidroApp</FullClassName>
    <VendorId>BAHR</VendorId>
    <VendorDescription>Bahr Engenharia, www.bahrengenharia.com</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path $addinFile -Value $addinXml -Encoding UTF8

Write-Host "[3/3] Desbloqueando arquivos e validando instalacao..." -ForegroundColor White
Get-ChildItem $targetBin -Recurse | Unblock-File -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "[SUCESSO] Bahr Hidro instalado e registrado para o Revit 2026!" -ForegroundColor Green
Write-Host "         Binarios : $targetBin" -ForegroundColor Gray
Write-Host "         Manifesto: $addinFile" -ForegroundColor Gray
Write-Host ""
