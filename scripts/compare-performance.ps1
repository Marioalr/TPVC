# Junta las mediciones de rendimiento de dos versiones ("antes" y "despues"), cada una en varias rondas
# (carpetas <Root>\antes\<ronda>\*.md y <Root>\despues\<ronda>\*.md), y escribe una tabla con la mediana
# de cada medida y el cambio. Lo usa el flujo "Rendimiento / Comparar" de la CI.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $mid = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

# Medidas de una ronda: "Sección · nombre" → número (ms o MB) y su unidad
function Read-Round([string]$dir) {
    $values = [ordered]@{}
    $perf = Join-Path $dir 'rendimiento.md'
    if (Test-Path $perf) {
        $section = ''
        foreach ($line in Get-Content $perf) {
            if ($line -match '^\| ([^|]+) \| \|$') { $section = $Matches[1].Trim(); continue }
            if ($line -notmatch 'la primera vez' -and $line -match '^\| ([^|]+) \| ([\d\.]+) (ms|MB)') {
                $values["$section · $($Matches[1].Trim())"] = @([double]$Matches[2], $Matches[3])
            }
            if ($line -match '^\| ([^|]+) \| ([\d\.]+) ms la primera vez · ([\d\.]+) ms después') {
                $values["$section · $($Matches[1].Trim()) (primera vez)"] = @([double]$Matches[2], 'ms')
                $values["$section · $($Matches[1].Trim()) (después)"] = @([double]$Matches[3], 'ms')
            }
        }
    }
    $startup = Join-Path $dir 'arranque.md'
    if (Test-Path $startup) {
        $normal = @(); $memory = @(); $install = @()
        foreach ($line in Get-Content $startup) {
            if ($line -match '^\| \d+ \| instalación nueva \| (\d+) ms') { $install += [double]$Matches[1] }
            if ($line -match '^\| \d+ \| normal \| (\d+) ms \| [^|]+ \| (\d+) MB') { $normal += [double]$Matches[1]; $memory += [double]$Matches[2] }
        }
        if ($install.Count) { $values['Programa real · Arranque, instalación nueva'] = @((Get-Median $install), 'ms') }
        if ($normal.Count) { $values['Programa real · Arranque normal'] = @((Get-Median $normal), 'ms') }
        if ($memory.Count) { $values['Programa real · Memoria en reposo'] = @((Get-Median $memory), 'MB') }
    }
    return $values
}

$sides = @{}
foreach ($side in 'antes', 'despues') {
    $rounds = Get-ChildItem (Join-Path $Root $side) -Directory | ForEach-Object { Read-Round $_.FullName }
    $merged = [ordered]@{}
    foreach ($round in $rounds) {
        foreach ($key in $round.Keys) {
            if (-not $merged.Contains($key)) { $merged[$key] = @{ Values = @(); Unit = $round[$key][1] } }
            $merged[$key].Values += $round[$key][0]
        }
    }
    $sides[$side] = $merged
}

$lines = @('## Rendimiento: antes y después (mediana de las rondas, mismo equipo)', '',
    '| Medida | Antes | Después | Cambio |', '|---|---|---|---|')
foreach ($key in $sides['antes'].Keys) {
    $before = $sides['antes'][$key]
    $after = $sides['despues'][$key]
    $b = Get-Median $before.Values
    if ($null -eq $after) { $lines += "| $key | $b $($before.Unit) | — | — |"; continue }
    $a = Get-Median $after.Values
    $change = if ($b -gt 0) { '{0:+0;-0;0} %' -f ((($a - $b) / $b) * 100) } else { '—' }
    $lines += ('| {0} | {1:0.#} {2} | {3:0.#} {2} | {4} |' -f $key, $b, $before.Unit, $a, $change)
}
$lines += ''
$lines | Set-Content -Encoding utf8 $Out
$lines | ForEach-Object { Write-Host $_ }
