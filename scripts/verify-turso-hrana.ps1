<#
.SYNOPSIS
  Comprobaciones V12, V14, V15 (antes del paso 8.6) y V18 (antes del 8.12) del plan de la Fase 8
  (docs/PLAN_FASE8_VARIOS_EQUIPOS.md, §4.2) en una base de PRUEBA de Turso.

.DESCRIPTION
  - V15: EXPLAIN QUERY PLAN y filas leídas de la consulta de páginas de la descarga (F1, la de hoy y la del paso 8.6)
    y de la revisión de consultas entre equipos (F2, sin y con el índice parcial), sobre ~20k filas sintéticas con la
    misma forma que las tablas de TPVC. No sube datos del consultorio.
  - V12: el tope de tamaño de una respuesta Hrana (una instrucción y varias en la misma petición).
  - V14: si una petición "batch" con BEGIN/COMMIT es atómica (un error a la mitad no deja nada escrito).
  - V18: cómo regresa Turso un BLOB (se espera {"type":"blob","base64":…} sin el relleno '=') y si acepta el base64
    de un argumento con y sin relleno.
  - V3 (antes del paso 8.14): qué responde Turso a un token inventado y a uno con la firma alterada (estado HTTP y
    cuerpo). No anula nada.
  - V16 (antes del 8.16): el contador de folios en una sola instrucción (INSERT … ON CONFLICT DO UPDATE … RETURNING),
    también con 40 peticiones a la vez: cada una debe recibir un número distinto y sin huecos.
  - V4 y V3 con una rotación real, solo con -Rotate: anula los tokens de la base de prueba (lo mismo que "turso db tokens
    invalidate") con la API de la plataforma de Turso, sin la CLI, mide cuánto sigue sirviendo el token viejo, crea uno
    nuevo y mide cuánto tarda en servir. Pide un token de la API de la plataforma, distinto del de la base (app.turso.tech,
    menú del avatar › Settings › API Tokens, o "turso auth api-tokens mint"; de preferencia
    limitado al grupo de la base de prueba) sin mostrarlo, y se niega si otra base comparte el grupo: anular corta a
    todas las del grupo. Al final puede copiar el token nuevo al portapapeles para pegarlo en TPVC. Sin token de la API,
    pide correr "turso db tokens invalidate" a mano con -DbName.

  Usa solo tablas que empiezan con "vtest_" y las borra al terminar. Se niega a correr en una base que ya tenga otras
  tablas (por ejemplo, la del consultorio). El token se pide sin mostrarlo y no se guarda.

  Costo aproximado de todo: ~22k filas escritas y menos de 100k leídas (menos del 0.3% y del 0.02% del mes del plan
  gratuito), más unos 200 MB de descarga para V12. Con -Only se corre solo lo indicado: V18 escribe unas 10 filas.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io -Only V18

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io -Only V3,V16

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io -Only V3 -Rotate

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-hrana.ps1 -Url libsql://tpvc-pruebas-usuario.turso.io -Only V3 -Rotate -Org usuario
#>
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [int]$Rows = 20000,
    [string[]]$Only = @('V15', 'V12', 'V14', 'V18', 'V3', 'V16'),
    [switch]$Rotate,
    # La organización o el usuario de Turso (por omisión, lo que sigue al último guion del nombre en la URL)
    [string]$Org = '',
    # Solo para probar el script contra el servidor simulado (docs/FAKE_HRANA_SPEC.md)
    [string]$ApiBase = 'https://api.turso.tech',
    # Sin token de la API: el nombre de la base para "turso db tokens invalidate" a mano
    [string]$DbName = '<base-de-prueba>'
)

$ErrorActionPreference = 'Stop'

# Con "powershell -File", "-Only V3,V16" llega como un solo texto: se separa por comas
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
$known = @('V3', 'V12', 'V14', 'V15', 'V16', 'V18')
$unknown = @($Only | Where-Object { $known -notcontains $_ })
if ($unknown.Count -gt 0) { throw "Comprobación desconocida: $($unknown -join ', '). Use: $($known -join ', ')." }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Wants([string]$check) { return $Only -contains $check }

# http:// solo en este equipo (un servidor libSQL local, como el de "turso dev"): el token viajaría en claro
$base = $Url.Trim().TrimEnd('/')
if ($base -like 'libsql://*') { $base = 'https://' + $base.Substring(9) }
if ($base -notlike 'https://*' -and $base -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?(/|$)') { throw 'La URL debe empezar con libsql:// o https://' }
if ($base -like '*/v2/pipeline') { $base = $base.Substring(0, $base.Length - '/v2/pipeline'.Length) }
$pipeline = "$base/v2/pipeline"
$hostName = ([Uri]$base).Host
$ApiBase = $ApiBase.Trim().TrimEnd('/')
if ($ApiBase -notlike 'https://*' -and $ApiBase -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?$') { throw 'La API debe ser https:// (http:// solo en este equipo)' }

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
    if ($value -is [hashtable]) { return $value } # ya es un valor Hrana (V18 manda el base64 tal cual)
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
function Send([object[]]$requests, [int]$timeoutSec = 120, [switch]$NoParse, [string]$WithToken = '') {
    $all = @($requests) + @(@{ type = 'close' })
    $body = @{ requests = $all } | ConvertTo-Json -Depth 30 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $status = 0
    $content = ''
    $bearer = if ($WithToken) { $WithToken } else { $token }
    try {
        $resp = Invoke-WebRequest -Uri $pipeline -Method Post -UseBasicParsing -TimeoutSec $timeoutSec `
            -Headers @{ Authorization = "Bearer $bearer" } -ContentType 'application/json' -Body $bytes
        $status = [int]$resp.StatusCode
        $content = $resp.Content
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return [pscustomobject]@{ Status = 0; Bytes = 0; Content = $_.Exception.Message; Json = $null } }
        $status = [int]$r.StatusCode
        # PowerShell 5.1 ya leyó el cuerpo del error: sin regresar al inicio, el texto de Turso sale vacío
        $stream = $r.GetResponseStream()
        if ($stream.CanSeek) { $stream.Position = 0 }
        $content = (New-Object IO.StreamReader($stream)).ReadToEnd()
        if (-not $content -and $_.ErrorDetails) { $content = $_.ErrorDetails.Message }
    }
    $json = $null
    if (-not $NoParse -and $content.Length -lt 5MB) { try { $json = $content | ConvertFrom-Json } catch { } }
    return [pscustomobject]@{ Status = $status; Bytes = [Text.Encoding]::UTF8.GetByteCount($content); Content = $content; Json = $json }
}

# Una llamada a la API de la plataforma de Turso (solo la rotación). Devuelve el estado HTTP y el texto.
function Api([string]$method, [string]$path, [string]$apiToken) {
    try {
        $resp = Invoke-WebRequest -Uri "$ApiBase$path" -Method $method -UseBasicParsing -TimeoutSec 60 -Headers @{ Authorization = "Bearer $apiToken" }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Content = [string]$resp.Content }
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return [pscustomobject]@{ Status = 0; Content = $_.Exception.Message } }
        $stream = $r.GetResponseStream()
        if ($stream.CanSeek) { $stream.Position = 0 }
        $content = (New-Object IO.StreamReader($stream)).ReadToEnd()
        if (-not $content -and $_.ErrorDetails) { $content = $_.ErrorDetails.Message }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Content = $content }
    }
}

# Un token de la base (el que se pega en TPVC) lleva en su JWT el permiso "a" ("rw" o "ro"); uno de la API de la
# plataforma no. Turso responde 401 "could not parse jwt id" si se usa uno de la base con la API
function IsDatabaseToken([string]$jwt) {
    $parts = $jwt.Trim().Split('.')
    if ($parts.Count -ne 3) { return $false }
    try {
        $payload = $parts[1].Replace('-', '+').Replace('_', '/')
        $payload += '=' * ((4 - $payload.Length % 4) % 4)
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
        return $null -ne $claims.PSObject.Properties['a']
    }
    catch { return $false }
}

function Short([string]$text, [int]$max = 300) { if ($null -eq $text) { return '' } return $text.Substring(0, [Math]::Min($max, $text.Length)) }

# Espera hasta que una petición con el token de ahora cumpla la condición (3 min como máximo); regresa la última respuesta
function WaitFor([scriptblock]$done, [Diagnostics.Stopwatch]$clock) {
    $last = $null
    while ($clock.Elapsed.TotalSeconds -lt 180) {
        $last = Send @(Exec 'SELECT 1') -timeoutSec 30
        if (& $done $last) { return $last }
        Start-Sleep -Seconds 2
    }
    return $null
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

Say "Comprobaciones $($Only -join ', ') · $(Get-Date -Format 'yyyy-MM-dd HH:mm') · base $hostName"
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

$costs = @{ V15 = "~$($Rows + 2003) filas escritas"; V12 = '~200 MB de descarga'; V14 = '~5 filas escritas'; V18 = '~10 filas escritas';
    V3 = 'nada (2 peticiones con un token inválido)'; V16 = '~45 filas escritas' }
Write-Host "Se crearán tablas vtest_* y se borrarán al final. Costo: $(@($Only | ForEach-Object { "$_, $($costs[$_])" }) -join '; ')."
$answer = Read-Host '¿Continuar? (s/n)'
if ($answer -notmatch '^[sS]') { exit 0 }

try {
    # ============================================================== V15 · F1, consulta de páginas de la descarga
    if (Wants 'V15') {
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
    }

    # ============================================================== V12 · tope de tamaño de una respuesta
    if (Wants 'V12') {
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
    }

    # ============================================================== V14 · batch con BEGIN/COMMIT
    if (Wants 'V14') {
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

    # ============================================================== V18 · BLOB y el relleno de base64
    if (Wants 'V18') {
        Say '== V18: BLOB con y sin relleno de base64, y la forma en que regresa'
        $asJson = { param($cell) $cell | ConvertTo-Json -Compress }

        # Cómo regresa: según libSQL (libsql-hrana/src/proto.rs), {"type":"blob","base64":…} sin "value" ni relleno '='
        $literals = @(
            @{ Sql = "X'89504E47'"; Note = '4 bytes'; Base64 = 'iVBORw' },
            @{ Sql = "X'89504E470D'"; Note = '5 bytes'; Base64 = 'iVBORw0' },
            @{ Sql = "X'FBEFFF'"; Note = '+ y /'; Base64 = '++//' },
            @{ Sql = "X''"; Note = 'vacío'; Base64 = '' }
        )
        $r = Run @(Exec ('SELECT ' + (@($literals | ForEach-Object { $_.Sql }) -join ', '))) 'Leer BLOB'
        $row = (Rows (ResultAt $r 0))[0]
        Say '  Lo que regresa Turso al leer un BLOB:'
        $returned = @()
        for ($i = 0; $i -lt $literals.Count; $i++) {
            $returned += Prop $row[$i] 'base64'
            Say ("    {0,-14} ({1}): {2}" -f $literals[$i].Sql, $literals[$i].Note, (& $asJson $row[$i]))
        }
        $hasValue = $null -ne $row[0].PSObject.Properties['value']

        # Lo que acepta: cada argumento en su propia petición, para que un HTTP 400 no tumbe a los demás
        Run @((Exec 'DROP TABLE IF EXISTS vtest_Blob'), (Exec 'CREATE TABLE vtest_Blob (Id INTEGER PRIMARY KEY, Content BLOB)')) 'Crear vtest_Blob' | Out-Null
        $sent = @(
            @{ Id = 1; Base64 = 'iVBORw=='; Note = '4 bytes, con relleno, como manda la PC'; Hex = '89504E47' },
            @{ Id = 2; Base64 = 'iVBORw'; Note = '4 bytes, sin relleno'; Hex = '89504E47' },
            @{ Id = 3; Base64 = 'iVBORw0='; Note = '5 bytes, con relleno'; Hex = '89504E470D' },
            @{ Id = 4; Base64 = 'iVBORw0'; Note = '5 bytes, sin relleno'; Hex = '89504E470D' },
            @{ Id = 5; Base64 = '++//'; Note = '+ y /'; Hex = 'FBEFFF' },
            @{ Id = 6; Base64 = ''; Note = 'vacío'; Hex = '' },
            @{ Id = 7; Base64 = 'iVBOR'; Note = 'no es base64'; Hex = $null }
        )
        Say '  Lo que acepta Turso en un argumento BLOB:'
        $accepted = @{}
        foreach ($s in $sent) {
            $r = Send @(Exec 'INSERT INTO vtest_Blob (Id, Content) VALUES (?, ?)' @($s.Id, @{ type = 'blob'; base64 = $s.Base64 }))
            $err = if ($r.Status -ne 200) { "HTTP $($r.Status) " + $r.Content.Substring(0, [Math]::Min(200, $r.Content.Length)) } else { ErrorText (ResultAt $r 0) }
            $accepted[$s.Id] = -not $err
            Say ("    {0,-12} ({1}): {2}" -f """$($s.Base64)""", $s.Note, $(if ($err) { "rechazado · $err" } else { 'aceptado' }))
        }

        # Lo que quedó guardado: los bytes correctos, y un BLOB vacío que no se volvió NULL
        $r = Run @(Exec 'SELECT Id, typeof(Content), hex(Content) FROM vtest_Blob ORDER BY Id') 'Leer vtest_Blob'
        $storedOk = $true
        Say '  Lo que quedó guardado (typeof y hex):'
        foreach ($stored in (Rows (ResultAt $r 0))) {
            $id = [int](Cell $stored 0)
            $expected = ($sent | Where-Object { $_.Id -eq $id }).Hex
            $kind = Cell $stored 1
            $hex = Cell $stored 2
            $good = ($null -ne $expected) -and $kind -eq 'blob' -and $hex -eq $expected
            if (-not $good) { $storedOk = $false }
            Say ("    Id {0}: {1}, hex '{2}' · {3}" -f $id, $kind, $hex, $(if ($good) { 'correcto' } elseif ($null -eq $expected) { 'se esperaba que lo rechazara' } else { "se esperaba blob '$expected'" }))
        }

        $form = "de otra forma: '$($returned[0])'"
        if ($returned[0] -eq 'iVBORw' -and $returned[1] -eq 'iVBORw0') { $form = 'sin relleno, como espera la PC' }
        elseif ($returned[0] -eq 'iVBORw==') { $form = 'CON relleno (la PC lo lee igual)' }
        $yesNo = { param($b) if ($b) { 'sí' } else { 'NO' } }
        Say ("  Resumen: Turso regresa el base64 {0}{1}. Acepta con relleno: {2}; sin relleno: {3}; lo que no es base64: {4}. Bytes guardados: {5}." -f
            $form, $(if ($hasValue) { ' y el BLOB trae también "value"' } else { '' }),
            (& $yesNo ($accepted[1] -and $accepted[3])), (& $yesNo ($accepted[2] -and $accepted[4])),
            $(if ($accepted[7]) { 'lo acepta' } else { 'lo rechaza' }), $(if ($storedOk) { 'correctos' } else { 'REVISAR' }))
        Say ''
    }

    # ============================================================== V3 · token desconocido (sin anular nada)
    if (Wants 'V3') {
        Say '== V3: qué responde Turso a un token que no sirve (no se anula nada)'
        $fake = Send @(Exec 'SELECT 1') -WithToken 'tpvc-v3-no-es-un-token'
        Say ("    Token inventado: HTTP {0} · {1}" -f $fake.Status, $fake.Content.Substring(0, [Math]::Min(300, $fake.Content.Length)))
        $parts = $token.Split('.')
        if ($parts.Count -eq 3 -and $parts[2].Length -gt 4) {
            # La misma cabecera y los mismos datos, con la firma cambiada en un carácter: como un token de otra llave
            $sig = $parts[2]
            $flip = if ($sig[$sig.Length - 2] -eq 'A') { 'B' } else { 'A' }
            $altered = $parts[0] + '.' + $parts[1] + '.' + $sig.Substring(0, $sig.Length - 2) + $flip + $sig.Substring($sig.Length - 1)
            $bad = Send @(Exec 'SELECT 1') -WithToken $altered
            Say ("    Token con la firma alterada: HTTP {0} · {1}" -f $bad.Status, $bad.Content.Substring(0, [Math]::Min(300, $bad.Content.Length)))
        }
        else {
            Say '    (El token no tiene forma de JWT; no se probó la firma alterada.)'
        }
        Say '  Cómo leerlo: TPVC espera HTTP 401; el texto del cuerpo es el que traducirá a "Turso rechazó el token: pegue el token nuevo".'
        Say ''
    }

    # ============================================================== V16 · contador de folios en una instrucción
    if (Wants 'V16') {
        Say '== V16: el contador de folios en una sola instrucción, también con 40 peticiones a la vez'
        Run @((Exec 'DROP TABLE IF EXISTS vtest_FolioCounters'),
            (Exec 'CREATE TABLE vtest_FolioCounters (Series TEXT PRIMARY KEY NOT NULL, LastNumber INTEGER NOT NULL)')) 'Crear vtest_FolioCounters' | Out-Null
        $counterSql = "INSERT INTO vtest_FolioCounters (Series, LastNumber) VALUES ('EXP-2026', 1) " +
                      'ON CONFLICT(Series) DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber'
        $first = Run @(Exec $counterSql) 'Primer folio'
        $second = Run @(Exec $counterSql) 'Segundo folio'
        $n1 = Cell (Rows (ResultAt $first 0))[0] 0
        $n2 = Cell (Rows (ResultAt $second 0))[0] 0
        Say ("    Una a la vez: regresa {0} y luego {1} (se esperan 1 y 2)" -f $n1, $n2)

        Add-Type -AssemblyName System.Net.Http
        $http = New-Object System.Net.Http.HttpClient
        $http.Timeout = [TimeSpan]::FromSeconds(60)
        $http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
        $payload = @{ requests = @((Exec $counterSql), @{ type = 'close' }) } | ConvertTo-Json -Depth 10 -Compress
        $tasks = @()
        for ($i = 0; $i -lt 40; $i++) {
            $tasks += $http.PostAsync($pipeline, (New-Object System.Net.Http.StringContent($payload, [Text.Encoding]::UTF8, 'application/json')))
        }
        [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks)
        $numbers = New-Object System.Collections.Generic.List[int]
        $failures = 0
        foreach ($t in $tasks) {
            $json = $t.Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json
            $res = $json.results[0]
            if ($t.Result.StatusCode -ne 200 -or $res.type -ne 'ok') { $failures++; continue }
            $numbers.Add([int]$res.response.result.rows[0][0].value)
        }
        $http.Dispose()
        $unique = @($numbers | Sort-Object -Unique)
        $expected = 40 - $failures
        $contiguous = $unique.Count -gt 0 -and ($unique[-1] - $unique[0] + 1) -eq $unique.Count
        Say ("    40 a la vez: {0} respondieron, {1} números distintos, del {2} al {3}; {4}" -f $numbers.Count, $unique.Count,
            $(if ($unique.Count) { $unique[0] } else { '-' }), $(if ($unique.Count) { $unique[-1] } else { '-' }),
            $(if ($unique.Count -eq $expected -and $contiguous -and $failures -eq 0) { 'sin repetidos ni huecos: sirve como contador' } else { "REVISAR ($failures con error)" }))
        Say ''
    }

    # ============================================================== V3 y V4 · rotación real (solo con -Rotate)
    if ($Rotate) {
        Say '== V4 (y V3 con un token anulado de verdad): anular los tokens de la base de prueba'
        Write-Host ''
        Write-Host 'Anular los tokens corta a TODAS las bases del mismo grupo de Turso. Con el token de la API, el script revisa el' -ForegroundColor Yellow
        Write-Host 'grupo y se niega si otra base lo comparte.' -ForegroundColor Yellow
        Write-Host 'Es un token de la API de la PLATAFORMA, distinto del de la base que se pega en TPVC: en app.turso.tech, menú de su' -ForegroundColor Yellow
        Write-Host 'avatar › Settings › API Tokens (de preferencia limitado al grupo de la base de prueba). Bórrelo al terminar.' -ForegroundColor Yellow
        $apiSecure = Read-Host -AsSecureString 'Token de la API de Turso (no se muestra; Enter para hacerlo a mano con la CLI)'
        $apiToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($apiSecure))

        $rotateDb = $null
        $canRotate = $false
        if ($apiToken -and (IsDatabaseToken $apiToken)) {
            Say '    Ese es un token de la BASE (lleva el permiso "rw" o "ro"), no uno de la API de la plataforma. No se anuló nada.'
            Say '    Cree uno en app.turso.tech › avatar › Settings › API Tokens y vuelva a correr el script.'
        }
        elseif ($apiToken) {
            # La organización: -Org, o lo que sigue al último guion de "base-organizacion" en la URL
            $orgSlug = $Org
            if (-not $orgSlug) { $first = $hostName.Split('.')[0]; $orgSlug = $first.Substring($first.LastIndexOf('-') + 1) }
            $list = Api 'GET' "/v1/organizations/$orgSlug/databases" $apiToken
            if ($list.Status -ne 200) {
                $hint = if ($list.Status -eq 401) { 'Turso no aceptó el token: debe ser un token de la API de la plataforma (avatar › Settings › API Tokens), no el de la base.' }
                        else { 'Revise el token de la API, o pase -Org con su organización.' }
                Say ("    No se pudo leer la lista de bases de '{0}' (HTTP {1} · {2}). {3} No se anuló nada." -f $orgSlug, $list.Status, (Short $list.Content), $hint)
            }
            else {
                $databases = @(($list.Content | ConvertFrom-Json).databases)
                $rotateDb = $databases | Where-Object { [string]::Equals([string]$_.Hostname, $hostName, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                if ($null -eq $rotateDb) {
                    Say "    Ninguna base de '$orgSlug' tiene la dirección $hostName. Rotación omitida."
                }
                else {
                    $peers = @($databases | Where-Object { $_.group -eq $rotateDb.group -and $_.Name -ne $rotateDb.Name } | ForEach-Object { $_.Name })
                    if ($peers.Count -gt 0) {
                        Say ("    Rotación omitida: en el grupo '{0}' también está(n) {1}. Anular los tokens las cortaría. Cree la base de prueba en otro grupo (o en otra cuenta de Turso)." -f $rotateDb.group, ($peers -join ', '))
                    }
                    else {
                        Say ("    Base '{0}', sola en el grupo '{1}'." -f $rotateDb.Name, $rotateDb.group)
                        $canRotate = $true
                    }
                }
            }
        }
        else {
            Write-Host 'CUIDADO: revise la columna Group con "turso db list": si la base del consultorio está en el mismo grupo, deja de sincronizar.' -ForegroundColor Red
            $canRotate = $true
        }

        if ($canRotate) {
            $confirm = Read-Host 'Escriba ROTAR para anular los tokens (otra cosa lo omite)'
            if ($confirm -cne 'ROTAR') {
                Say '    Rotación omitida.'
            }
            else {
                $clock = [Diagnostics.Stopwatch]::StartNew()
                if ($rotateDb) {
                    $rotated = Api 'POST' "/v1/organizations/$orgSlug/databases/$($rotateDb.Name)/auth/rotate" $apiToken
                    Say ("    La API anuló los tokens en {0:N1} s (HTTP {1}{2})." -f $clock.Elapsed.TotalSeconds, $rotated.Status, $(if ($rotated.Content) { ' · ' + (Short $rotated.Content) } else { '' }))
                    if ($rotated.Status -ne 200) { throw "No se pudieron anular los tokens: HTTP $($rotated.Status)." }
                }
                else {
                    Write-Host "En OTRA terminal corra:  turso db tokens invalidate $DbName -y"
                    Read-Host 'Pulse Enter aquí en cuanto termine' | Out-Null
                }

                $clock.Restart()
                $rejected = WaitFor { param($r) $r.Status -ne 200 } $clock
                if ($rejected) {
                    Say ("    El token viejo dejó de servir a los {0:N0} s: HTTP {1} · {2}" -f $clock.Elapsed.TotalSeconds, $rejected.Status, (Short $rejected.Content))
                }
                else {
                    Say '    El token viejo siguió sirviendo 3 minutos después de anularlo (REVISAR).'
                }

                # El token nuevo: lo crea la API (o se pega uno creado a mano)
                if ($rotateDb) {
                    $created = Api 'POST' "/v1/organizations/$orgSlug/databases/$($rotateDb.Name)/auth/tokens?expiration=never&authorization=full-access" $apiToken
                    if ($created.Status -ne 200) { throw "No se pudo crear el token nuevo: HTTP $($created.Status) · $(Short $created.Content)" }
                    $token = ($created.Content | ConvertFrom-Json).jwt
                }
                else {
                    Write-Host "Ahora cree uno nuevo:  turso db tokens create $DbName"
                    $secureNew = Read-Host -AsSecureString 'Token NUEVO (no se muestra)'
                    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureNew))
                }
                $clock.Restart()
                $working = WaitFor { param($r) $r.Status -eq 200 } $clock
                Say ("    El token nuevo {0}" -f $(if ($working) { "sirvió a los {0:N0} s de crearlo" -f $clock.Elapsed.TotalSeconds } else { 'no sirvió en 3 minutos (REVISAR)' }))
                Say '  Cómo leerlo: la interrupción para los equipos dura lo que tarde el médico en pegar el token nuevo, más lo que tarde Turso en aceptarlo.'

                # Para la prueba del paso 8.15 en TPVC (una PC conectada a la base de prueba): el token nuevo, solo si se pide
                if ($working -and $rotateDb) {
                    $copy = Read-Host 'Escriba COPIAR para llevar el token nuevo al portapapeles y pegarlo en TPVC (Enter lo omite)'
                    if ($copy -ceq 'COPIAR') {
                        Set-Clipboard -Value $token
                        Write-Host 'Token copiado. Péguelo en TPVC y copie después cualquier otro texto para quitarlo del portapapeles.' -ForegroundColor Green
                    }
                }
            }
        }
        $apiToken = $null
        Say ''
    }
}
finally {
    $cleanup = Send @((Exec 'DROP TABLE IF EXISTS vtest_Rows'), (Exec 'DROP TABLE IF EXISTS vtest_Handoffs'), (Exec 'DROP TABLE IF EXISTS vtest_Atomic'), (Exec 'DROP TABLE IF EXISTS vtest_Blob'),
        (Exec 'DROP TABLE IF EXISTS vtest_FolioCounters'))
    if ($cleanup.Status -eq 200) { Say 'Tablas vtest_* borradas.' } else { Say "No se pudieron borrar las tablas vtest_* (HTTP $($cleanup.Status)); bórrelas a mano o borre la base de prueba." }

    $file = Join-Path (Get-Location) ("turso-verificacion-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmm'))
    [IO.File]::WriteAllLines($file, $report, (New-Object Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host "Reporte guardado en $file (no lleva el token)." -ForegroundColor Green
}
