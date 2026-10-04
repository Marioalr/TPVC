param(
    [string]$Version = "1.3.8"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Construyendo TPVC e Instalador v$Version " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Detener instancias de TPVC si están corriendo
Stop-Process -Name "TPVC" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "TPVC-Setup" -Force -ErrorAction SilentlyContinue

$rootDir = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $rootDir "publish"
$appPublishDir = Join-Path $publishDir "TPVC-v$Version-win-x64"
$appZipPath = Join-Path $publishDir "TPVC-v$Version-win-x64.zip"
$installerResourceDir = Join-Path $rootDir "installer\TPVC.Installer\Resources"
$payloadZipPath = Join-Path $installerResourceDir "Payload.zip"
$setupPublishDir = Join-Path $publishDir "Installer-Output"
$finalSetupExe = Join-Path $publishDir "TPVC-Setup-v$Version.exe"

# Asegurar directorios
if (-not (Test-Path $publishDir)) { New-Item -ItemType Directory -Path $publishDir | Out-Null }
if (-not (Test-Path $installerResourceDir)) { New-Item -ItemType Directory -Path $installerResourceDir | Out-Null }
if (Test-Path $appPublishDir) { Remove-Item -Recurse -Force $appPublishDir }
if (Test-Path $appZipPath) { Remove-Item -Force $appZipPath }
if (Test-Path $payloadZipPath) { Remove-Item -Force $payloadZipPath }

# 2. Publicar la aplicación principal TPVC
Write-Host "`n[1/4] Publicando aplicación TPVC..." -ForegroundColor Yellow
dotnet publish "$rootDir\TPVC.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $appPublishDir

if (-not (Test-Path "$appPublishDir\TPVC.exe")) {
    throw "Error: No se encontró TPVC.exe en el directorio de publicación."
}

# 3. Comprimir la aplicación para la descarga y como carga útil del instalador
Write-Host "`n[2/4] Creando archivo zip con la carga útil..." -ForegroundColor Yellow
Compress-Archive -Path "$appPublishDir\*" -DestinationPath $appZipPath -Force
Copy-Item -Path $appZipPath -Destination $payloadZipPath -Force

$zipSizeMb = [Math]::Round((Get-Item $payloadZipPath).Length / 1MB, 2)
Write-Host "Payload generado: $payloadZipPath ($zipSizeMb MB)" -ForegroundColor Green

# 4. Publicar el instalador como un único archivo ejecutable autónomo
Write-Host "`n[3/4] Compilando el ejecutable del Instalador..." -ForegroundColor Yellow
dotnet publish "$rootDir\installer\TPVC.Installer\TPVC.Installer.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $setupPublishDir

$builtSetupExe = Join-Path $setupPublishDir "TPVC-Setup.exe"
if (-not (Test-Path $builtSetupExe)) {
    throw "Error: No se encontró el ejecutable del instalador generado."
}

# Copiar el instalador final a publish/TPVC-Setup-v1.3.0.exe
Copy-Item -Path $builtSetupExe -Destination $finalSetupExe -Force
Remove-Item -Recurse -Force $setupPublishDir

$setupSizeMb = [Math]::Round((Get-Item $finalSetupExe).Length / 1MB, 2)
Write-Host "`n[4/4] ¡Instalador completado exitosamente!" -ForegroundColor Green
Write-Host "--------------------------------------------------" -ForegroundColor Cyan
Write-Host "Instalador Oficial: $finalSetupExe ($setupSizeMb MB)" -ForegroundColor White
Write-Host "Paquete Portátil:   $appZipPath ($zipSizeMb MB)" -ForegroundColor White
Write-Host "--------------------------------------------------" -ForegroundColor Cyan
