<#
.SYNOPSIS
  Comprobaciones V17 y V19 a V23 del paso 8.12b (resultados de laboratorio en la nube) en una base de PRUEBA de Turso.
  docs/EVALUACION_RESULTADOS_LABORATORIO.md, §7.

.DESCRIPTION
  - V17: INSERT con un BLOB de argumento de 0.5, 1, 2, 4, 8, 16 y 32 MB: tope de la PETICIÓN, su error y su tiempo.
  - V19: filas escritas al insertar un trozo de 512 KiB (se esperan 2) y filas leídas al leer 4 trozos por su llave, al
    preguntar qué trozos existen, al cerrar un archivo y en COUNT/SUM de los metadatos.
  - V20 (CRÍTICA): ~100 MB con randomblob; mide el espacio (PRAGMA, dbstat y, si está la herramienta de Turso,
    "turso db inspect") antes, con los 100 MB, después de borrar la mitad y unos minutos después. Si borrar no libera
    espacio, desalojar no libera cuota.
  - V21: si Turso comprime con gzip la respuesta de un BLOB de 2 MB (se pide Accept-Encoding: gzip) y cuántos bytes viajan.
  - V22: tiempos de subir y bajar 512 KiB y 2 MB por la red donde se corre (Wi-Fi del consultorio, LTE…; ver -Network).
  - V23: un batch con BEGIN/COMMIT que lleva un BLOB de 1 MB: si es atómico (V14 con BLOB).

  Usa solo tablas que empiezan con "vtest_" y las borra al terminar. Se niega a correr en una base que ya tenga otras
  tablas (por ejemplo, la del consultorio). El token se pide sin mostrarlo y no se guarda. Los BLOB de V19 a V21 se
  generan en el servidor con randomblob; los de V17, V22 y V23 se mandan desde aquí.

  Costo aproximado de todo: ~70 MB de subida (V17), unos 300 filas escritas y ~100 MB de espacio temporal (V20; ver
  la nota al final del reporte). Con -Only se corre solo lo indicado.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-blobs.ps1 -Url libsql://prueba-estres-usuario.turso.io -DbName prueba-estres

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-turso-blobs.ps1 -Url libsql://prueba-estres-usuario.turso.io -Only V22 -Network 'LTE del celular'
#>
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [ValidateSet('V17', 'V19', 'V20', 'V21', 'V22', 'V23')][string[]]$Only = @('V17', 'V19', 'V20', 'V21', 'V22', 'V23'),
    # El nombre de la base para "turso db inspect" (V20); sin él, V20 mide solo con PRAGMA y dbstat
    [string]$DbName = '',
    # V20: minutos de espera antes de la última medición (el panel de Turso puede tardar en actualizarse)
    [int]$WaitMinutes = 3,
    # V20: cuántos MB se escriben (la mitad se borra después)
    [int]$StorageMB = 100,
    # V22: cómo se llama la red donde se corre (va en el reporte)
    [string]$Network = ''
)

$ErrorActionPreference = 'Stop'
# Sin la barra de progreso, Invoke-WebRequest de PowerShell 5.1 no se vuelve lentísimo con peticiones grandes
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Wants([string]$check) { return $Only -contains $check }

# http:// solo en este equipo (un servidor libSQL local, como el de "turso dev"): el token viajaría en claro
$base = $Url.Trim().TrimEnd('/')
if ($base -like 'libsql://*') { $base = 'https://' + $base.Substring(9) }
if ($base -notlike 'https://*' -and $base -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?(/|$)') { throw 'La URL debe empezar con libsql:// o https://' }
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
    if ($value -is [hashtable]) { return $value }
    if ($value -is [int] -or $value -is [long]) { return @{ type = 'integer'; value = "$value" } }
    return @{ type = 'text'; value = [string]$value }
}

function Stmt([string]$sql, [object[]]$values = @()) {
    $s = @{ sql = $sql }
    if ($values.Count -gt 0) { $s.args = @($values | ForEach-Object { HranaArg $_ }) }
    return $s
}

function Exec([string]$sql, [object[]]$values = @()) { return @{ type = 'execute'; stmt = (Stmt $sql $values) } }

# Marca de un BLOB dentro de una petición: se arma el JSON con la marca y luego se cambia por el base64 (un BLOB de
# decenas de MB no pasa por ConvertTo-Json de PowerShell 5.1)
$BlobMark = '__VTEST_BLOB__'
function BlobArg() { return @{ type = 'blob'; base64 = $BlobMark } }

function RandomBytes([long]$size, [int]$seed) {
    $bytes = New-Object byte[] $size
    (New-Object Random $seed).NextBytes($bytes)
    return , $bytes
}

# Manda el cuerpo de una petición /v2/pipeline. Devuelve el estado HTTP, los bytes de ida y de vuelta, el tiempo y, si
# la respuesta no es grande, el JSON.
function SendBody([string]$body, [int]$timeoutSec = 120, [switch]$NoParse) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $status = 0
    $content = ''
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-WebRequest -Uri $pipeline -Method Post -UseBasicParsing -TimeoutSec $timeoutSec `
            -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $bytes
        $status = [int]$resp.StatusCode
        $content = $resp.Content
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) {
            $watch.Stop()
            return [pscustomobject]@{ Status = 0; Sent = $bytes.Length; Bytes = 0; Ms = $watch.ElapsedMilliseconds; Content = $_.Exception.Message; Json = $null }
        }
        $status = [int]$r.StatusCode
        # PowerShell 5.1 ya leyó el cuerpo del error: sin regresar al inicio, el texto de Turso sale vacío
        $stream = $r.GetResponseStream()
        if ($stream.CanSeek) { $stream.Position = 0 }
        $content = (New-Object IO.StreamReader($stream)).ReadToEnd()
        if (-not $content -and $_.ErrorDetails) { $content = $_.ErrorDetails.Message }
    }
    $watch.Stop()
    $json = $null
    if (-not $NoParse -and $content.Length -lt 1MB) { try { $json = $content | ConvertFrom-Json } catch { } }
    return [pscustomobject]@{ Status = $status; Sent = $bytes.Length; Bytes = [Text.Encoding]::UTF8.GetByteCount($content); Ms = $watch.ElapsedMilliseconds; Content = $content; Json = $json }
}

function RequestBody([object[]]$requests, [byte[]]$blob = $null) {
    $all = @($requests) + @(@{ type = 'close' })
    $body = @{ requests = $all } | ConvertTo-Json -Depth 30 -Compress
    if ($null -ne $blob) { $body = $body.Replace($BlobMark, [Convert]::ToBase64String($blob)) }
    return $body
}

function Send([object[]]$requests, [int]$timeoutSec = 120, [switch]$NoParse, [byte[]]$Blob = $null) {
    return SendBody (RequestBody $requests $Blob) $timeoutSec -NoParse:$NoParse
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

# El error de una petición, también si la respuesta es grande y no se interpretó
function ErrorInRaw($r) {
    if ($r.Status -ne 200) { return "HTTP $($r.Status) " + $r.Content.Substring(0, [Math]::Min(300, $r.Content.Length)) }
    if ($r.Content -notmatch '"type":"error"') { return $null }
    $message = if ($r.Content -match '"message":"([^"]*)"') { $Matches[1] } else { 'error sin mensaje' }
    $code = if ($r.Content -match '"code":"([^"]*)"') { $Matches[1] } else { '' }
    return "$code $message".Trim()
}

# Ejecuta una o varias instrucciones y falla con el mensaje de Turso si alguna da error
function Run([object[]]$requests, [string]$what, [byte[]]$Blob = $null) {
    $r = Send $requests 300 -Blob $Blob
    if ($r.Status -ne 200) { throw "$what`: HTTP $($r.Status) $($r.Content)" }
    for ($i = 0; $i -lt @($requests).Count; $i++) {
        $err = ErrorText (ResultAt $r $i)
        if ($err) { throw "$what (instrucción $($i + 1)): $err" }
    }
    return $r
}

# Filas leídas y escritas de una instrucción, como las reporta Turso (n/d si no las reporta)
function Stat($r, [int]$index, [string]$name) {
    $res = (ResultAt $r $index)
    if ($null -ne $res) {
        $value = Prop $res.response.result $name
        if ($null -ne $value) { return $value }
    }
    # Respuesta grande sin interpretar: la primera aparición (la petición lleva una sola instrucción)
    if ($r.Content -match ('"' + $name + '":(\d+)')) { return [long]$Matches[1] }
    return 'n/d'
}

function Mb([double]$bytes) { return $bytes / 1MB }

# ------------------------------------------------------------------ inicio

Say "Comprobaciones $($Only -join ', ') · $(Get-Date -Format 'yyyy-MM-dd HH:mm') · base $hostName$(if ($Network) { " · red: $Network" })"
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

$turso = Get-Command turso -ErrorAction SilentlyContinue
if ((Wants 'V20') -and -not $DbName -and $turso) {
    $DbName = (Read-Host 'Nombre de la base de prueba para "turso db inspect" (V20; Enter para medir solo con PRAGMA)').Trim()
}

$costs = @{
    V17 = '~70 MB de subida, 7 filas'
    V19 = '~3 MB, ~60 filas'
    V20 = "~$StorageMB MB temporales, $($StorageMB * 2) filas y $WaitMinutes min de espera"
    V21 = '~2 MB'
    V22 = '~15 MB de subida y de bajada'
    V23 = '~2 MB'
}
Write-Host "Se crearán tablas vtest_* y se borrarán al final. Costo: $(@($Only | ForEach-Object { "$_, $($costs[$_])" }) -join '; ')."
$answer = Read-Host '¿Continuar? (s/n)'
if ($answer -notmatch '^[sS]') { exit 0 }

$tables = 'vtest_Big', 'vtest_Blobs', 'vtest_Chunks', 'vtest_Store', 'vtest_Gz', 'vtest_Speed', 'vtest_Tx'

function StorageNow([string]$label) {
    Say "  $label`:"
    foreach ($pragma in 'page_size', 'page_count', 'freelist_count') {
        $r = Send @(Exec "PRAGMA $pragma")
        $err = ErrorText (ResultAt $r 0)
        if ($r.Status -ne 200) { $err = "HTTP $($r.Status)" }
        if ($err) { Say "    PRAGMA $pragma`: no se pudo ($err)" } else { Say "    PRAGMA $pragma = $(Cell (Rows (ResultAt $r 0))[0] 0)" }
    }
    $r = Send @(Exec "SELECT SUM(pgsize) FROM dbstat")
    $err = ErrorText (ResultAt $r 0)
    if ($r.Status -ne 200) { $err = "HTTP $($r.Status)" }
    if ($err) { Say "    dbstat: no disponible ($err)" } else { Say ("    dbstat, SUM(pgsize) = {0:N1} MB" -f (Mb ([double](Cell (Rows (ResultAt $r 0))[0] 0)))) }
    if ($turso -and $DbName) {
        # Lo que la herramienta escriba en stderr no debe detener el script (ErrorActionPreference = Stop)
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $out = & $turso.Source db inspect $DbName 2>&1 | ForEach-Object { "$_" }
            foreach ($line in $out) { if ($line.Trim()) { Say "    turso db inspect: $($line.Trim())" } }
        }
        catch {
            Say "    turso db inspect: no se pudo ($($_.Exception.Message))"
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
    }
    elseif (-not $script:storageHintShown) {
        $script:storageHintShown = $true
        Say '    (sin "turso db inspect": en cada medición anote también el espacio que muestra el panel de Turso)'
    }
}

try {
    # ============================================================== V17 · tope de la petición
    if (Wants 'V17') {
        Say '== V17: tope de la PETICIÓN con un BLOB de argumento (INSERT)'
        Run @((Exec 'DROP TABLE IF EXISTS vtest_Big'), (Exec 'CREATE TABLE vtest_Big (Id INTEGER PRIMARY KEY, Data BLOB NOT NULL)')) 'Crear vtest_Big' | Out-Null
        $lastOk = 0.0
        $failure = $null
        $id = 0
        foreach ($mb in 0.5, 1, 2, 4, 8, 16, 32) {
            $id++
            $size = [long]($mb * 1MB)
            $bytes = RandomBytes $size $id
            $r = Send @(Exec 'INSERT INTO vtest_Big (Id, Data) VALUES (?, ?)' @($id, (BlobArg))) 300 -Blob $bytes
            $err = ErrorInRaw $r
            if (-not $err) {
                $stored = Cell (Rows (ResultAt (Run @(Exec 'SELECT length(Data) FROM vtest_Big WHERE Id = ?' @($id)) 'Revisar') 0))[0] 0
                if ([long]$stored -ne $size) { $err = "guardó $stored bytes en lugar de $size" }
                Run @(Exec 'DELETE FROM vtest_Big WHERE Id = ?' @($id)) 'Borrar' | Out-Null
            }
            Say ("    {0,5:N1} MB ({1,5:N1} MB de JSON): {2} en {3:N1} s" -f $mb, (Mb $r.Sent), $(if ($err) { "ERROR · $err" } else { 'bien' }), ($r.Ms / 1000))
            if ($err) { $failure = "$mb MB: $err"; break }
            $lastOk = $mb
        }
        if ($failure) { Say "  Resumen: la petición más grande que pasó llevaba un BLOB de $lastOk MB; falló con $failure." }
        else { Say '  Resumen: sin tope hasta 32 MB de BLOB (~43 MB de JSON) en una petición.' }
        Say ''
    }

    # ============================================================== V19 · filas leídas y escritas
    if (Wants 'V19') {
        Say '== V19: filas escritas y leídas con las tablas del diseño (LabBlobs y LabBlobChunks)'
        Run @(
            (Exec 'DROP TABLE IF EXISTS vtest_Blobs'),
            (Exec 'DROP TABLE IF EXISTS vtest_Chunks'),
            (Exec 'CREATE TABLE vtest_Blobs (Sha256 TEXT PRIMARY KEY, SizeBytes INTEGER NOT NULL, ChunkSize INTEGER NOT NULL, ChunkCount INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, ArchivedAtUtc TEXT, EvictedAtUtc TEXT)'),
            (Exec 'CREATE TABLE vtest_Chunks (Sha256 TEXT NOT NULL, ChunkIndex INTEGER NOT NULL, Data BLOB NOT NULL, PRIMARY KEY (Sha256, ChunkIndex))')
        ) 'Crear vtest_Blobs y vtest_Chunks' | Out-Null

        $now = "strftime('%Y-%m-%d %H:%M:%f','now')"
        $r = Run @(Exec "INSERT INTO vtest_Blobs (Sha256, SizeBytes, ChunkSize, ChunkCount, CreatedAtUtc) VALUES ('h1', 2097152, 524288, 4, $now) ON CONFLICT DO NOTHING") 'Registrar'
        Say ("    Registrar el archivo (INSERT … ON CONFLICT DO NOTHING): filas escritas = {0}" -f (Stat $r 0 'rows_written'))
        $r = Run @(Exec "INSERT INTO vtest_Chunks (Sha256, ChunkIndex, Data) VALUES ('h1', 0, randomblob(524288)) ON CONFLICT DO NOTHING") 'Trozo 0'
        Say ("    Un trozo de 512 KiB: filas escritas = {0} (se esperan 2: la fila y su llave)" -f (Stat $r 0 'rows_written'))
        $r = Run @(Exec "INSERT INTO vtest_Chunks (Sha256, ChunkIndex, Data) VALUES ('h1', 0, randomblob(524288)) ON CONFLICT DO NOTHING") 'Trozo 0 otra vez'
        Say ("    El mismo trozo otra vez (ya existía): filas escritas = {0}" -f (Stat $r 0 'rows_written'))
        Run @(1..3 | ForEach-Object { Exec "INSERT INTO vtest_Chunks (Sha256, ChunkIndex, Data) VALUES ('h1', $_, randomblob(524288))" }) 'Trozos 1 a 3' | Out-Null

        $r = Run @(Exec "SELECT ChunkIndex FROM vtest_Chunks WHERE Sha256 = 'h1'") 'Qué trozos existen'
        Say ("    Qué trozos existen (4): filas leídas = {0}" -f (Stat $r 0 'rows_read'))
        $r = Send @(Exec "SELECT ChunkIndex, Data FROM vtest_Chunks WHERE Sha256 = 'h1' AND ChunkIndex >= 0 ORDER BY ChunkIndex LIMIT 4") 300 -NoParse
        $err = ErrorInRaw $r
        if ($err) { Say "    Leer 4 trozos: ERROR · $err" }
        else { Say ("    Leer 4 trozos (2 MB): filas leídas = {0}; respuesta de {1:N2} MB en {2:N1} s" -f (Stat $r 0 'rows_read'), (Mb $r.Bytes), ($r.Ms / 1000)) }
        $r = Run @(Exec "UPDATE vtest_Blobs SET CompletedAtUtc = $now WHERE Sha256 = 'h1' AND (SELECT COUNT(*) FROM vtest_Chunks WHERE Sha256 = 'h1') = ChunkCount") 'Cerrar'
        Say ("    Cerrar el archivo (UPDATE con COUNT de sus trozos): filas leídas = {0}, escritas = {1}, cambió = {2}" -f (Stat $r 0 'rows_read'), (Stat $r 0 'rows_written'), (Prop (ResultAt $r 0).response.result 'affected_row_count'))

        Run @(Exec "WITH RECURSIVE n(i) AS (SELECT 2 UNION ALL SELECT i + 1 FROM n WHERE i < 50) INSERT INTO vtest_Blobs (Sha256, SizeBytes, ChunkSize, ChunkCount, CreatedAtUtc) SELECT 'h' || i, 524288 * i, 524288, i, $now FROM n") 'Más metadatos' | Out-Null
        $r = Run @(Exec 'SELECT COUNT(*), SUM(SizeBytes) FROM vtest_Blobs WHERE EvictedAtUtc IS NULL') 'Tope'
        Say ("    COUNT/SUM de 50 archivos (la revisión diaria del tope): filas leídas = {0}" -f (Stat $r 0 'rows_read'))
        Say ''
    }

    # ============================================================== V20 · ¿borrar libera espacio?
    if (Wants 'V20') {
        Say "== V20 (CRÍTICA): ¿borrar BLOB libera el espacio que cuenta Turso? ($StorageMB MB)"
        Run @((Exec 'DROP TABLE IF EXISTS vtest_Store'), (Exec 'CREATE TABLE vtest_Store (Id INTEGER PRIMARY KEY, Data BLOB NOT NULL)')) 'Crear vtest_Store' | Out-Null
        StorageNow 'Antes'
        $rowsToWrite = $StorageMB * 2
        for ($done = 0; $done -lt $rowsToWrite; $done += 10) {
            $count = [Math]::Min(10, $rowsToWrite - $done)
            Run @(1..$count | ForEach-Object { Exec 'INSERT INTO vtest_Store (Data) VALUES (randomblob(524288))' }) 'Escribir' | Out-Null
            Write-Host ("    … {0} de {1} MB" -f (($done + $count) / 2), $StorageMB)
        }
        StorageNow "Con $StorageMB MB escritos"
        $r = Run @(Exec 'DELETE FROM vtest_Store WHERE Id % 2 = 0') 'Borrar la mitad'
        Say ("  Se borraron {0} filas ({1} MB)" -f (Prop (ResultAt $r 0).response.result 'affected_row_count'), ($StorageMB / 2))
        StorageNow 'Justo después de borrar la mitad'
        if ($WaitMinutes -gt 0) {
            Write-Host "  Esperando $WaitMinutes min (el panel de Turso puede tardar en actualizarse)…"
            Start-Sleep -Seconds ($WaitMinutes * 60)
            StorageNow "$WaitMinutes min después de borrar"
        }
        Say '  Cómo leerlo: si page_count y el espacio de "turso db inspect" (o del panel) no bajan después de borrar, desalojar no libera cuota (el tope pasa a ser sobre lo acumulado).'
        Say ''
    }

    # ============================================================== V21 · ¿gzip en la respuesta?
    if (Wants 'V21') {
        Say '== V21: ¿Turso comprime la respuesta de un BLOB de 2 MB si se pide gzip?'
        Run @(
            (Exec 'DROP TABLE IF EXISTS vtest_Gz'),
            (Exec 'CREATE TABLE vtest_Gz (Id INTEGER PRIMARY KEY, Data BLOB NOT NULL)'),
            (Exec 'INSERT INTO vtest_Gz (Id, Data) VALUES (1, randomblob(2097152))')
        ) 'Crear vtest_Gz' | Out-Null
        $body = [Text.Encoding]::UTF8.GetBytes((RequestBody @(Exec 'SELECT Data FROM vtest_Gz WHERE Id = 1')))
        $request = [System.Net.HttpWebRequest]::Create($pipeline)
        $request.Method = 'POST'
        $request.ContentType = 'application/json'
        $request.Headers.Add('Authorization', "Bearer $token")
        $request.Headers.Add('Accept-Encoding', 'gzip')
        $request.AutomaticDecompression = [System.Net.DecompressionMethods]::None
        $request.Timeout = 120000
        $out = $request.GetRequestStream()
        $out.Write($body, 0, $body.Length)
        $out.Close()
        $response = $request.GetResponse()
        $encoding = $response.Headers['Content-Encoding']
        $raw = New-Object IO.MemoryStream
        $response.GetResponseStream().CopyTo($raw)
        $response.Close()
        $wire = $raw.Length
        $json = $wire
        if ($encoding -eq 'gzip') {
            $raw.Position = 0
            $unzipped = New-Object IO.MemoryStream
            (New-Object IO.Compression.GZipStream($raw, [IO.Compression.CompressionMode]::Decompress)).CopyTo($unzipped)
            $json = $unzipped.Length
        }
        Say ("    Content-Encoding: {0}" -f $(if ($encoding) { $encoding } else { '(ninguno: sin comprimir)' }))
        Say ("    Bytes que viajaron: {0:N2} MB; JSON: {1:N2} MB; el BLOB: 2.00 MB → la bajada cuesta {2:+0;-0}% sobre el archivo" -f (Mb $wire), (Mb $json), [Math]::Round(($wire / 2MB - 1) * 100))
        Say ''
    }

    # ============================================================== V22 · tiempos por la red
    if (Wants 'V22') {
        Say ("== V22: tiempos de subir y bajar por la red de aquí{0} (mediana de 3)" -f $(if ($Network) { " ($Network)" } else { '' }))
        Run @((Exec 'DROP TABLE IF EXISTS vtest_Speed'), (Exec 'CREATE TABLE vtest_Speed (Id INTEGER PRIMARY KEY, Data BLOB NOT NULL)')) 'Crear vtest_Speed' | Out-Null
        $id = 0
        foreach ($size in 524288, 2097152) {
            $ups = @()
            $downs = @()
            foreach ($try in 1..3) {
                $id++
                $bytes = RandomBytes $size (100 + $id)
                $r = Send @(Exec 'INSERT INTO vtest_Speed (Id, Data) VALUES (?, ?)' @($id, (BlobArg))) 300 -Blob $bytes
                $err = ErrorInRaw $r
                if ($err) { Say "    Subir: ERROR · $err"; break }
                $ups += $r.Ms
                $r = Send @(Exec 'SELECT Data FROM vtest_Speed WHERE Id = ?' @($id)) 300 -NoParse
                $err = ErrorInRaw $r
                if ($err) { Say "    Bajar: ERROR · $err"; break }
                $downs += $r.Ms
            }
            if ($ups.Count -eq 3 -and $downs.Count -eq 3) {
                $up = ($ups | Sort-Object)[1]
                $down = ($downs | Sort-Object)[1]
                Say ("    {0,4} KiB: subir {1:N1} s (~{2:N1} Mbps), bajar {3:N1} s (~{4:N1} Mbps)" -f ($size / 1KB), ($up / 1000), ($size * 8 / 1e6 / [Math]::Max(0.001, $up / 1000)), ($down / 1000), ($size * 8 / 1e6 / [Math]::Max(0.001, $down / 1000)))
            }
        }
        Say '  Cómo leerlo: un trozo de 512 KiB debe tardar bastante menos que los 30 s de espera de TPVC; si 2 MB tardan poco, el trozo podría ser más grande.'
        Say ''
    }

    # ============================================================== V23 · batch atómico con BLOB
    if (Wants 'V23') {
        Say '== V23: ¿un batch con BEGIN/COMMIT que lleva un BLOB de 1 MB es atómico?'
        function BlobAtomicTry([string]$secondInsert) {
            Run @((Exec 'DROP TABLE IF EXISTS vtest_Tx'), (Exec 'CREATE TABLE vtest_Tx (Id INTEGER PRIMARY KEY, Data BLOB NOT NULL)')) 'Crear vtest_Tx' | Out-Null
            $steps = @(
                @{ stmt = (Stmt 'BEGIN') },
                @{ condition = @{ type = 'ok'; step = 0 }; stmt = (Stmt 'INSERT INTO vtest_Tx (Id, Data) VALUES (1, ?)' @((BlobArg))) },
                @{ condition = @{ type = 'ok'; step = 1 }; stmt = (Stmt $secondInsert) },
                @{ condition = @{ type = 'ok'; step = 2 }; stmt = (Stmt 'COMMIT') },
                @{ condition = @{ type = 'not'; cond = @{ type = 'ok'; step = 3 } }; stmt = (Stmt 'ROLLBACK') }
            )
            $r = Send @(@{ type = 'batch'; batch = @{ steps = $steps } }) 300 -Blob (RandomBytes 1048576 23)
            $res = ResultAt $r 0
            $batchError = if ($r.Status -ne 200) { "HTTP $($r.Status)" } else { ErrorText $res }
            $stepErrors = @()
            if (-not $batchError) {
                $errs = @(Prop $res.response.result 'step_errors')
                for ($i = 0; $i -lt $errs.Count; $i++) { if ($null -ne $errs[$i]) { $stepErrors += "paso $($i + 1): $($errs[$i].message)" } }
            }
            $count = Cell (Rows (ResultAt (Run @(Exec 'SELECT COUNT(*) FROM vtest_Tx') 'Contar') 0))[0] 0
            return [pscustomobject]@{ Http = $r.Status; Seconds = $r.Ms / 1000; BatchError = $batchError; StepErrors = $stepErrors; Rows = $count }
        }

        $ok = BlobAtomicTry "INSERT INTO vtest_Tx (Id, Data) VALUES (2, X'00')"
        Say ("    Todo bien: HTTP {0}, filas al final = {1} (se esperan 2), {2:N1} s{3}" -f $ok.Http, $ok.Rows, $ok.Seconds, $(if ($ok.BatchError) { " · error: $($ok.BatchError)" } else { '' }))
        $bad = BlobAtomicTry 'INSERT INTO vtest_Tx (Id, Data) VALUES (2, NULL)'
        Say ("    Con error en la segunda: HTTP {0}, filas al final = {1} (0 = atómica, 1 = no){2}" -f $bad.Http, $bad.Rows, $(if ($bad.BatchError) { " · error: $($bad.BatchError)" } else { '' }))
        foreach ($e in $bad.StepErrors) { Say "      $e" }
        Say ''
    }
}
finally {
    $cleanup = Send @($tables | ForEach-Object { Exec "DROP TABLE IF EXISTS $_" })
    if ($cleanup.Status -eq 200) { Say 'Tablas vtest_* borradas.' } else { Say "No se pudieron borrar las tablas vtest_* (HTTP $($cleanup.Status)); bórrelas a mano o borre la base de prueba." }
    if (Wants 'V20') {
        StorageNow 'Después de borrar las tablas'
        Say 'Nota: si el espacio no bajó, la base de prueba se queda con lo escrito en V20. Se puede borrar con "turso db destroy <nombre>" y crear otra.'
    }

    $file = Join-Path (Get-Location) ("turso-blobs-verificacion-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmm'))
    [IO.File]::WriteAllLines($file, $report, (New-Object Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host "Reporte guardado en $file (no lleva el token)." -ForegroundColor Green
}
