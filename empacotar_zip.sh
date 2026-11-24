#!/usr/bin/env bash
set -e
dotnet build BahrHidro.Revit/BahrHidro.Revit.csproj -c Release -o dist/bin
dotnet publish SmartHidro.Installer/SmartHidro.Installer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/exe
rm -rf dist/pacote SmartHidro_Revit2026.zip
mkdir -p dist/pacote/bin
cp -r dist/bin/* dist/pacote/bin/
cp dist/exe/AtualizarSmartHidro.exe dist/pacote/
python3 - << 'PY'
import os, zipfile
with zipfile.ZipFile("SmartHidro_Revit2026.zip", "w", zipfile.ZIP_DEFLATED) as zf:
    for root, _, files in os.walk("dist/pacote"):
        for file in files:
            full = os.path.join(root, file)
            zf.write(full, os.path.relpath(full, "dist/pacote"))
PY
rm -rf dist
echo "✅ Pacote gerado: $(pwd)/SmartHidro_Revit2026.zip"
