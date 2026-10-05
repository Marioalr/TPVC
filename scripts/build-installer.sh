#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# Por defecto se toma la versión declarada en TPVC.csproj (única fuente de verdad)
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT_DIR/TPVC.csproj" | head -n1)}"
PUBLISH_DIR="$ROOT_DIR/publish"
APP_PUBLISH_DIR="$PUBLISH_DIR/TPVC-v$VERSION-win-x64"
APP_ZIP_PATH="$PUBLISH_DIR/TPVC-v$VERSION-win-x64.zip"
INSTALLER_RESOURCE_DIR="$ROOT_DIR/installer/TPVC.Installer/Resources"
PAYLOAD_ZIP_PATH="$INSTALLER_RESOURCE_DIR/Payload.zip"
SETUP_PUBLISH_DIR="$PUBLISH_DIR/Installer-Output"
FINAL_SETUP_EXE="$PUBLISH_DIR/TPVC-Setup-v$VERSION.exe"

# Use local user-level dotnet if available
DOTNET_BIN="dotnet"
if [ -x "$HOME/.dotnet/dotnet" ]; then
    DOTNET_BIN="$HOME/.dotnet/dotnet"
fi

echo "=========================================="
echo " Construyendo TPVC e Instalador v$VERSION "
echo "=========================================="

mkdir -p "$PUBLISH_DIR"
mkdir -p "$INSTALLER_RESOURCE_DIR"
rm -rf "$APP_PUBLISH_DIR" "$APP_ZIP_PATH" "$PAYLOAD_ZIP_PATH" "$SETUP_PUBLISH_DIR"

echo -e "\n[1/4] Publicando aplicación TPVC (win-x64)..."
"$DOTNET_BIN" publish "$ROOT_DIR/TPVC.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained false \
    -o "$APP_PUBLISH_DIR"

if [ ! -f "$APP_PUBLISH_DIR/TPVC.exe" ]; then
    echo "Error: No se encontró TPVC.exe en el directorio de publicación." >&2
    exit 1
fi

echo -e "\n[2/4] Creando archivo zip con la carga útil..."
python3 -c "
import shutil
base_name = '$APP_ZIP_PATH'
if base_name.endswith('.zip'):
    base_name = base_name[:-4]
shutil.make_archive(base_name, 'zip', '$APP_PUBLISH_DIR')
"

cp "$APP_ZIP_PATH" "$PAYLOAD_ZIP_PATH"
ZIP_SIZE=$(du -h "$PAYLOAD_ZIP_PATH" | cut -f1)
echo "Payload generado: $PAYLOAD_ZIP_PATH ($ZIP_SIZE)"

echo -e "\n[3/4] Compilando el ejecutable del Instalador..."
"$DOTNET_BIN" publish "$ROOT_DIR/installer/TPVC.Installer/TPVC.Installer.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained false \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$SETUP_PUBLISH_DIR"

BUILT_SETUP_EXE="$SETUP_PUBLISH_DIR/TPVC-Setup.exe"
if [ ! -f "$BUILT_SETUP_EXE" ]; then
    echo "Error: No se encontró el ejecutable del instalador generado." >&2
    exit 1
fi

cp "$BUILT_SETUP_EXE" "$FINAL_SETUP_EXE"
rm -rf "$SETUP_PUBLISH_DIR"

SETUP_SIZE=$(du -h "$FINAL_SETUP_EXE" | cut -f1)

# Sumas SHA-256 para verificar la integridad de lo publicado
CHECKSUMS_PATH="$PUBLISH_DIR/SHA256SUMS-v$VERSION.txt"
(cd "$PUBLISH_DIR" && sha256sum "TPVC-Setup-v$VERSION.exe" "TPVC-v$VERSION-win-x64.zip" > "$CHECKSUMS_PATH")
echo -e "\n[4/4] ¡Instalador completado exitosamente!"
echo "--------------------------------------------------"
echo "Instalador Oficial: $FINAL_SETUP_EXE ($SETUP_SIZE)"
echo "Paquete Portátil:   $APP_ZIP_PATH ($ZIP_SIZE)"
echo "Checksums:          $CHECKSUMS_PATH"
echo "--------------------------------------------------"
