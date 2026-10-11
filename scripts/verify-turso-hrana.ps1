<#
.SYNOPSIS
  Comprobaciones V12, V14 y V15 del plan de la Fase 8 (docs/PLAN_FASE8_VARIOS_EQUIPOS.md, §4.2) en una base de
  PRUEBA de Turso, antes del paso 8.6.

.DESCRIPTION
  - V15: EXPLAIN QUERY PLAN y filas leídas de la consulta de páginas de la descarga (F1, la de hoy y la del paso 8.6)
    y de la revisión de consultas entre equipos (F2, sin y con el índice parcial), sobre ~20k filas sintéticas con la
    misma forma que las tablas de TPVC. No sube datos del consultorio.
  - V12: el tope de tamaño de una respuesta Hrana (una instrucción y varias en la misma petición).
  - V14: si una petición "batch" con BEGIN/COMMIT es atómica (un error a la mitad no deja nada escrito).

  Usa solo tablas que empiezan con "vtest_" y las borra al terminar. Se niega a correr en una base que ya tenga otras
  tablas (por ejemplo, la del consultorio). El token se pide sin mostrarlo y no se guarda.

  Costo aproximado: ~22k filas escritas y menos de 100k leídas (menos del 0.3% y del 0.02% del mes del plan gratuito),
  más unos 200 MB de descarga para V12.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io
#>
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [int]$Rows = 20000
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$base = $Url.Trim().TrimEnd('/')
if ($base -like 'libsql://*') { $base = 'https://' + $base.Substring(9) }
if ($base -notlike 'https://*') { throw 'La URL debe empezar con libsql:// o https://' }
if ($base -like '*/v2/pipeline') { $base = $base.Substring(0, $base.Length - '/v2/pipeline'.Length) }
$pipeline = "$base/v2/pipeline"
$hostName = ([Uri]$base).Host

$secure = Read-Host -AsSecureString "Token de la base de PRUEBA ($hostName)"
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
if ([string]::IsNullOrWhiteSpace($token)) { throw 'Falta el token.' }

$report = New-Object System.Collections.Generic.List[string]
function Say([string]$text = '') {
    Write-Host $text
    $report.Add($text)
}

# ------------------------------------------------------------------ Hrana

function HranaArg($value) {
    if ($null -eq $value) { return @{ type = 'null' } }
    if ($value -is [int] -or $value -is [long]) { return @{ type = 'integer'; value = "$value" } }
    return @{ type = 'text'; value = [string]$value }
}

function Stmt([string]$sql, [object[]]$values = @()) {
    $s = @{ sql = $sql }
    if ($values.Count -gt 0) { $s.args = @($values | ForEach-Object { HranaArg $_ }) }
    return $s
}

function Exec([string]$sql, [object[]]$values = @()) { return @{ type = 'execute'; stmt = (Stmt $sql $values) } }

# Una petición /v2/pipeline. Devuelve el estado HTTP, los bytes de la respuesta y, si no es enorme, el JSON.
function Send([object[]]$requests, [int]$timeoutSec = 120, [switch]$NoParse) {
    $all = @($requests) + @(@{ type = 'close' })
    $body = @{ requests = $all } | ConvertTo-Json -Depth 30 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $status = 0
    $content = ''
    try {
        $resp = Invoke-WebRequest -Uri $pipeline -Method Post -UseBasicParsing -TimeoutSec $timeoutSec `
            -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $bytes
        $status = [int]$resp.StatusCode
        $content = $resp.Content
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return [pscustomobject]@{ Status = 0; Bytes = 0; Content = $_.Exception.Message; Json = $null } }
        $status = [int]$r.StatusCode
        $content = (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd()
    }
    $json = $null
    if (-not $NoParse -and $content.Length -lt 5MB) { try { $json = $content | ConvertFrom-Json } catch { } }
    return [pscustomobject]@{ Status = $status; Bytes = [Text.Encoding]::UTF8.GetByteCount($content); Content = $content; Json = $json }
}

function ResultAt($response, [int]$index) {
    if ($null -eq $response.Json) { return $null }
    return $response.Json.results[$index]
}

function ErrorText($result) {
    if ($null -eq $result) { return 'sin respuesta' }
    if ($result.type -eq 'error') { return "$($result.error.code) $($result.error.message)".Trim() }
    return $null
}

function Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# La coma evita que PowerShell desenrolle la lista: sin ella, una sola fila llegaría como sus valores sueltos
function Rows($result) { return , @($result.response.result.rows) }

function Cell($row, [int]$i) { return (Prop $row[$i] 'value') }

# Ejecuta una o varias instrucciones y falla con el mensaje de Turso si alguna da error
function Run([object[]]$requests, [string]$what) {
    $r = Send $requests
    if ($r.Status -ne 200) { throw "$what`: HTTP $($r.Status) $($r.Content)" }
    for ($i = 0; $i -lt @($requests).Count; $i++) {
        $err = ErrorText (ResultAt $r $i)
        if ($err) { throw "$what (instrucción $($i + 1)): $err" }
    }
    return $r
}

function Plan([string]$sql, [object[]]$values) {
    $r = Run @(Exec ("EXPLAIN QUERY PLAN " + $sql) $values) 'EXPLAIN QUERY PLAN'
    $planRows = Rows (ResultAt $r 0)
    foreach ($row in $planRows) {
        $parent = [int](Cell $row 1)
        $indent = if ($parent -eq 0) { '    ' } else { '      ' }
        Say ($indent + (Cell $row 3))
    }
}

# Ejecuta la consulta y anota cuántas filas devolvió y cuántas dice Turso que leyó
function MeasureQuery([string]$label, [string]$sql, [object[]]$values) {
    $r = Run @(Exec $sql $values) $label
    $res = (ResultAt $r 0).response.result
    $read = Prop $res 'rows_read'
    if ($null -eq $read) { $read = 'n/d (Turso no lo reporta)' }
    Say ("    {0}: {1} filas devueltas, filas leídas = {2}" -f $label, @($res.rows).Count, $read)
}

# ------------------------------------------------------------------ inicio

Say "Comprobaciones V12, V14 y V15 · $(Get-Date -Format 'yyyy-MM-dd HH:mm') · base $hostName"
Say ''

$probe = Send @(Exec "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'vtest_%' AND name NOT LIKE '_litestream%' AND name NOT LIKE 'libsql_%'")
if ($probe.Status -eq 401 -or $probe.Status -eq 403) { throw "Turso rechazó el token (HTTP $($probe.Status)). Revise que sea de esta base." }
if ($probe.Status -ne 200) { throw "No se pudo consultar la base: HTTP $($probe.Status) $($probe.Content)" }
$probeErr = ErrorText (ResultAt $probe 0)
if ($probeErr) { throw "No se pudo consultar la base: $probeErr" }
$probeRows = Rows (ResultAt $probe 0)
$existing = @($probeRows | ForEach-Object { Cell $_ 0 })
if ($existing.Count -gt 0) {
    Write-Host ''
    Write-Host "Esta base ya tiene tablas: $($existing -join ', ')." -ForegroundColor Red
    Write-Host 'Parece una base en uso (quizá la del consultorio). Use una base de prueba nueva y vacía.' -ForegroundColor Red
    exit 1
}

Write-Host "Se crearán tablas vtest_* con $Rows filas sintéticas y se borrarán al final (~22k filas escritas, ~200 MB de descarga)."
$answer = Read-Host '¿Continuar? (s/n)'
if ($answer -notmatch '^[sS]') { exit 0 }

try {
    # ============================================================== V15 · F1, consulta de páginas de la descarga
    Say '== V15 · F1: consulta de páginas de la descarga'
    Run @(
        (Exec 'DROP TABLE IF EXISTS vtest_Rows'),
        (Exec 'CREATE TABLE vtest_Rows ("SyncId" TEXT PRIMARY KEY NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "SyncedAtUtc" TEXT NOT NULL, "OriginDeviceId" TEXT, "Payload" TEXT)'),
        (Exec 'CREATE INDEX "IX_vtest_Rows_SyncedAtUtc" ON vtest_Rows ("SyncedAtUtc")'),
        # 40 filas por marca, como una subida en lotes; un tercio de la tablet
        (Exec ("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?) " +
               "INSERT INTO vtest_Rows SELECT lower(hex(randomblob(16))), " +
               "strftime('%Y-%m-%d %H:%M:%f', '2026-10-01', '+' || (i / 40) || ' seconds'), " +
               "strftime('%Y-%m-%d %H:%M:%f', '2026-10-01', '+' || (i / 40) || ' seconds'), " +
               "CASE WHEN i % 3 = 0 THEN 'device-tablet' ELSE 'device-pc' END, hex(zeroblob(150)) FROM n") @($Rows))
    ) 'Crear vtest_Rows' | Out-Null
    Say "    vtest_Rows con $Rows filas"

    $midRow = Rows (ResultAt (Run @(Exec 'SELECT "SyncedAtUtc", "SyncId" FROM vtest_Rows ORDER BY "SyncedAtUtc", "SyncId" LIMIT 1 OFFSET ?' @([int]($Rows / 2))) 'Fila de en medio') 0)
    $midSynced = Cell $midRow[0] 0
    $midKey = Cell $midRow[0] 1

    $today = 'SELECT * FROM vtest_Rows WHERE ("SyncedAtUtc" > ? OR ("SyncedAtUtc" = ? AND "SyncId" > ?)) AND ("OriginDeviceId" IS NULL OR "OriginDeviceId" <> ?) ORDER BY "SyncedAtUtc", "SyncId" LIMIT 500'
    $step86 = 'SELECT * FROM vtest_Rows WHERE "SyncedAtUtc" >= ? AND ("SyncedAtUtc" > ? OR ("SyncedAtUtc" = ? AND "SyncId" > ?)) AND ("OriginDeviceId" IS NULL OR "OriginDeviceId" <> ?) ORDER BY "SyncedAtUtc", "SyncId" LIMIT 500'
    $firstToday = @('', '', '', 'device-pc')
    $first86 = @('', '', '', '', 'device-pc')
    $midToday = @($midSynced, $midSynced, $midKey, 'device-pc')
    $mid86 = @($midSynced, $midSynced, $midSynced, $midKey, 'device-pc')

    Say '  Consulta de hoy (TursoSyncService.ReadPagesAsync):'
    Plan $today $firstToday
    MeasureQuery 'primera página' $today $firstToday
    MeasureQuery 'página de en medio' $today $midToday
    Say '  Consulta del paso 8.6 ("SyncedAtUtc" >= ? adelante):'
    Plan $step86 $first86
    MeasureQuery 'primera página' $step86 $first86
    MeasureQuery 'página de en medio' $step86 $mid86
    Say ''

    # ============================================================== V15 · F2, revisión de consultas entre equipos
    Say '== V15 · F2: revisión de consultas entre equipos'
    Run @(
        (Exec 'DROP TABLE IF EXISTS vtest_Handoffs'),
        (Exec ('CREATE TABLE vtest_Handoffs (Id TEXT PRIMARY KEY NOT NULL, FromDeviceId TEXT NOT NULL, ToDeviceId TEXT NOT NULL, ' +
               'Payload TEXT NOT NULL, State TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL)')),
        (Exec 'CREATE INDEX IX_vtest_Handoffs_To ON vtest_Handoffs (ToDeviceId, State)'),
        # Lo que se conserva para la PC: 2,000 ya resueltas (sin copia) y 3 enviadas
        (Exec ("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) " +
               "INSERT INTO vtest_Handoffs SELECT lower(hex(randomblob(16))), 'device-tablet', 'device-pc', '', " +
               "CASE WHEN i % 2 = 0 THEN 'aceptada' ELSE 'cancelada' END, strftime('%Y-%m-%d %H:%M:%f', '2026-01-01', '+' || i || ' hours') FROM n")),
        (Exec ("INSERT INTO vtest_Handoffs SELECT lower(hex(randomblob(16))), 'device-tablet', 'device-pc', 'copia', 'enviada', " +
               "strftime('%Y-%m-%d %H:%M:%f', 'now', '-' || value || ' minutes') FROM (SELECT 1 AS value UNION ALL SELECT 2 UNION ALL SELECT 3)"))
    ) 'Crear vtest_Handoffs' | Out-Null

    $poll = "SELECT * FROM vtest_Handoffs WHERE ToDeviceId = ? AND (State = 'enviada' OR (State = 'aceptada' AND Payload <> '')) ORDER BY CreatedAtUtc DESC LIMIT 10"
    Say '  Sin índice parcial (hoy):'
    Plan $poll @('device-pc')
    MeasureQuery 'revisión' $poll @('device-pc')
    Run @(Exec "CREATE INDEX IX_vtest_Handoffs_Pending ON vtest_Handoffs (ToDeviceId) WHERE State = 'enviada' OR (State = 'aceptada' AND Payload <> '')") 'Índice parcial' | Out-Null
    Say '  Con el índice parcial del paso 8.24:'
    Plan $poll @('device-pc')
    MeasureQuery 'revisión' $poll @('device-pc')
    Say ''

    # ============================================================== V12 · tope de tamaño de una respuesta
    Say '== V12: tope de tamaño de una respuesta Hrana'
    $lastOk = 0
    $firstFail = 0
    $failText = ''
    # El error de una instrucción en una respuesta sin interpretar (puede pesar decenas de MB)
    function ErrorInRaw($r) {
        if ($r.Status -ne 200) { return "HTTP $($r.Status) " + $r.Content.Substring(0, [Math]::Min(300, $r.Content.Length)) }
        if ($r.Content -notmatch '"type":"error"') { return $null }
        $message = if ($r.Content -match '"message":"([^"]*)"') { $Matches[1] } else { 'error sin mensaje' }
        $code = if ($r.Content -match '"code":"([^"]*)"') { $Matches[1] } else { '' }
        return "$code $message".Trim()
    }
    function TryBig([long]$size) {
        $r = Send @(Exec 'SELECT hex(zeroblob(?)) AS big' @([long]($size / 2))) 300 -NoParse
        $err = ErrorInRaw $r
        return [pscustomobject]@{ Ok = ($null -eq $err); Bytes = $r.Bytes; Error = $err }
    }
    foreach ($mb in 1, 2, 4, 8, 12, 16, 24, 32, 48, 64) {
        $size = [long]$mb * 1MB
        $t = TryBig $size
        if ($t.Ok) {
            Say ("    {0,3} MB en una instrucción: bien ({1:N0} bytes de respuesta)" -f $mb, $t.Bytes)
            $lastOk = $size
        }
        else {
            Say ("    {0,3} MB en una instrucción: ERROR · {1}" -f $mb, $t.Error)
            $firstFail = $size
            $failText = $t.Error
            break
        }
    }
    if ($firstFail -gt 0) {
        # Afinar entre lo último que pasó y lo primero que falló (a 256 KB)
        $lo = $lastOk; $hi = $firstFail
        while ($hi - $lo -gt 256KB) {
            $midSize = [long](($lo + $hi) / 2)
            if ((TryBig $midSize).Ok) { $lo = $midSize } else { $hi = $midSize }
        }
        Say ("    Tope de una instrucción: entre {0:N1} y {1:N1} MB ({2})" -f ($lo / 1MB), ($hi / 1MB), $failText)

        # ¿Hay además un tope para toda la petición? Varias instrucciones de 80% del tope cada una
        $each = [long]($lo * 0.8)
        foreach ($count in 2, 3, 4, 6) {
            $requests = @(1..$count | ForEach-Object { Exec 'SELECT hex(zeroblob(?)) AS big' @([long]($each / 2)) })
            $r = Send $requests 300 -NoParse
            $err = ErrorInRaw $r
            Say ("    {0} instrucciones de {1:N1} MB en una petición ({2:N1} MB): {3}" -f $count, ($each / 1MB), ($count * $each / 1MB), $(if ($err) { "ERROR · $err" } else { 'bien' }))
            if ($err) { break }
        }
    }
    else {
        Say '    Sin error hasta 64 MB en una instrucción.'
    }
    Say ''

    # ============================================================== V14 · batch con BEGIN/COMMIT
    Say '== V14: ¿una petición "batch" con BEGIN/COMMIT es atómica?'
    function AtomicTry([string]$secondInsert) {
        Run @((Exec 'DROP TABLE IF EXISTS vtest_Atomic'), (Exec 'CREATE TABLE vtest_Atomic (Id INTEGER PRIMARY KEY, Note TEXT NOT NULL)')) 'Crear vtest_Atomic' | Out-Null
        $steps = @(
            @{ stmt = (Stmt 'BEGIN') },
            @{ condition = @{ type = 'ok'; step = 0 }; stmt = (Stmt "INSERT INTO vtest_Atomic (Id, Note) VALUES (1, 'primera')") },
            @{ condition = @{ type = 'ok'; step = 1 }; stmt = (Stmt $secondInsert) },
            @{ condition = @{ type = 'ok'; step = 2 }; stmt = (Stmt 'COMMIT') },
            @{ condition = @{ type = 'not'; cond = @{ type = 'ok'; step = 3 } }; stmt = (Stmt 'ROLLBACK') }
        )
        $r = Send @(@{ type = 'batch'; batch = @{ steps = $steps } })
        $res = ResultAt $r 0
        $stepErrors = @()
        $batchError = ErrorText $res
        if (-not $batchError) {
            $errs = @(Prop $res.response.result 'step_errors')
            for ($i = 0; $i -lt $errs.Count; $i++) { if ($null -ne $errs[$i]) { $stepErrors += "paso $($i + 1): $($errs[$i].message)" } }
        }
        $count = Cell (Rows (ResultAt (Run @(Exec 'SELECT COUNT(*) FROM vtest_Atomic') 'Contar') 0))[0] 0
        return [pscustomobject]@{ Http = $r.Status; BatchError = $batchError; StepErrors = $stepErrors; Rows = $count }
    }

    $ok = AtomicTry "INSERT INTO vtest_Atomic (Id, Note) VALUES (2, 'segunda')"
    Say ("    Todo bien: HTTP {0}, filas al final = {1} (se esperan 2){2}" -f $ok.Http, $ok.Rows, $(if ($ok.BatchError) { " · error: $($ok.BatchError)" } else { '' }))
    $bad = AtomicTry 'INSERT INTO vtest_Atomic (Id, Note) VALUES (2, NULL)'
    Say ("    Con error en la segunda: HTTP {0}, filas al final = {1} (0 = atómica, 1 = no){2}" -f $bad.Http, $bad.Rows, $(if ($bad.BatchError) { " · error: $($bad.BatchError)" } else { '' }))
    foreach ($e in $bad.StepErrors) { Say "      $e" }
    Say ''
}
finally {
    $cleanup = Send @((Exec 'DROP TABLE IF EXISTS vtest_Rows'), (Exec 'DROP TABLE IF EXISTS vtest_Handoffs'), (Exec 'DROP TABLE IF EXISTS vtest_Atomic'))
    if ($cleanup.Status -eq 200) { Say 'Tablas vtest_* borradas.' } else { Say "No se pudieron borrar las tablas vtest_* (HTTP $($cleanup.Status)); bórrelas a mano o borre la base de prueba." }

    $file = Join-Path (Get-Location) ("turso-verificacion-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmm'))
    [IO.File]::WriteAllLines($file, $report, (New-Object Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host "Reporte guardado en $file (no lleva el token)." -ForegroundColor Green
}
