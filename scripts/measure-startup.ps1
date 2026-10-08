# Mide el arranque real de TPVC.exe: lo abre varias veces, espera la línea "Arranque" que la propia
# aplicación deja en su bitácora (ventana dibujada) y anota el tiempo y la memoria. La primera vez es
# la de una instalación nueva (crea la base); las demás, un arranque normal.
#
# Uso (CI): .\scripts\measure-startup.ps1 -Exe bin\Release\net10.0-windows\TPVC.exe -OutDir rendimiento
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Launches = 4,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:LOCALAPPDATA 'TPVC'
$logDir = Join-Path $dataDir 'logs'

# Solo en la CI se borran los datos de TPVC para medir una instalación nueva; nunca en un equipo real
if ($env:GITHUB_ACTIONS -eq 'true' -and (Test-Path $dataDir)) {
    Remove-Item -Recurse -Force $dataDir
}

function Get-StartupLines {
    if (-not (Test-Path $logDir)) { return @() }
    Get-ChildItem $logDir -Filter 'tpvc-*.log' | Get-Content | Where-Object { $_ -match '\[INFO\] Arranque: ventana lista en (\d+) ms' }
}

$results = @()
for ($i = 1; $i -le $Launches; $i++) {
    $before = @(Get-StartupLines).Count
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Exe -PassThru
    $line = $null
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $lines = @(Get-StartupLines)
        if ($lines.Count -gt $before) { $line = $lines[-1]; break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    $watch.Stop()

    $entry = [ordered]@{ Vez = $i; Tipo = $(if ($i -eq 1) { 'instalación nueva' } else { 'normal' }); Ventana = 'sin dato'; Memoria = 'sin dato' }
    if ($line -and $line -match 'ventana lista en (\d+) ms · memoria (\d+) MB') {
        $entry.Ventana = "$($Matches[1]) ms"
        $entry.Memoria = "$($Matches[2]) MB"
    }
    # Memoria ya en reposo (pasados unos segundos con la ventana abierta)
    Start-Sleep -Seconds 3
    $entry.Reposo = 'sin dato'
    if (-not $process.HasExited) {
        $process.Refresh()
        $entry.Reposo = "{0:0} MB" -f ($process.WorkingSet64 / 1MB)
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) { $process.Kill() }
    }
    $results += [pscustomobject]$entry
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$md = @('## Arranque real de TPVC.exe', '', '| Vez | Tipo | Ventana dibujada | Memoria al dibujar | Memoria en reposo |', '|---|---|---|---|---|')
foreach ($r in $results) { $md += "| $($r.Vez) | $($r.Tipo) | $($r.Ventana) | $($r.Memoria) | $($r.Reposo) |" }
$md += ''
$md | Set-Content -Encoding utf8 (Join-Path $OutDir 'arranque.md')
$md | ForEach-Object { Write-Host $_ }
