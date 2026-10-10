using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TPVC.Tests;

/// <summary>Cómo responde el servidor simulado cuando se "acaba la cuota" (V7: falta ver la forma real en Turso).</summary>
internal enum FakeBlockedMode
{
    None,

    /// <summary>HTTP 200 y cada instrucción con el error Hrana de código BLOCKED (lo que documenta Turso).</summary>
    StatementError,

    /// <summary>La petición entera se rechaza con un estado HTTP de error y BLOCKED en el cuerpo.</summary>
    HttpError
}

/// <summary>Qué hace el servidor simulado ante un SCAN (recorrido completo) de una tabla sincronizada.</summary>
internal enum FakeScanPolicy
{
    /// <summary>Lo anota en <see cref="FakeHranaServer.ScanWarnings"/> y en la salida de la prueba (hasta el paso 8.6).</summary>
    Warn,

    /// <summary>La instrucción falla con el código SCAN_FORBIDDEN (desde el paso 8.6), salvo las excepciones.</summary>
    Fail
}

/// <summary>Una instrucción tal como llegó, con lo que Turso le habría cobrado (estimado).</summary>
internal sealed record FakeRecordedStatement(string Sql, JsonArray Args, long RowsRead, long RowsWritten, string? ErrorCode);

/// <summary>Una petición /v2/pipeline: sus instrucciones en orden y el estado HTTP con que se respondió.</summary>
internal sealed record FakeRecordedRequest(int Number, IReadOnlyList<FakeRecordedStatement> Statements, HttpStatusCode Status);

/// <summary>
/// Servidor Hrana v2 (/v2/pipeline) simulado sobre SQLite. Copia de Turso lo que importa a la app:
/// <list type="bullet">
/// <item>Valida la codificación de valores: un entero enviado como número JSON provoca HTTP 400 Bad Request.</item>
/// <item>Un resultado por instrucción y sin transacción: cada instrucción se confirma sola. Una petición "batch"
/// con BEGIN/COMMIT y condiciones sí es atómica (V14, comprobado en Turso real el 2026-10-10).</item>
/// <item>Un escritor a la vez (<see cref="Lock"/>).</item>
/// <item>Reloj del servidor inyectable (<see cref="Clock"/>): reemplaza 'now' en las instrucciones.</item>
/// <item>Estimación de lo cobrado (<see cref="BilledRowsRead"/>, <see cref="BilledRowsWritten"/>): cada fila recorrida
/// cuenta como leída y una instrucción que no toca filas cuenta como 1.</item>
/// <item>Grabadora de peticiones (<see cref="Requests"/>), revisión de planes de consulta (<see cref="ScanPolicy"/>),
/// tope de tamaño de respuesta (<see cref="MaxResponseBytes"/>) y BLOCKED a pedido (<see cref="Blocked"/>).</item>
/// </list>
/// Lo que todavía no se comprobó en Turso real (V7, V10, V12, V15) está marcado donde se usa.
/// </summary>
internal sealed class FakeHranaServer : HttpMessageHandler
{
    private readonly SqliteConnection _connection;

    // Turso atiende las escrituras de una en una; dos equipos a la vez no comparten la conexión
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly List<FakeRecordedRequest> _requests = new();
    private readonly List<string> _scanWarnings = new();
    private readonly Dictionary<string, bool> _syncedTables = new(StringComparer.OrdinalIgnoreCase);

    public int RequestCount { get; private set; }
    public int StatementCount { get; private set; }

    /// <summary>Filas leídas que Turso habría cobrado (estimado; ver <see cref="EstimateRowsRead"/>).</summary>
    public long BilledRowsRead { get; private set; }

    /// <summary>Filas escritas que Turso habría cobrado (V10: falta saber si también cuenta las entradas de índice).</summary>
    public long BilledRowsWritten { get; private set; }

    /// <summary>Sin red: la petición falla antes de llegar.</summary>
    public bool IsOffline { get; set; }

    /// <summary>La petición se aplica pero la respuesta se pierde (la red cae justo después).</summary>
    public Func<string, bool>? DropResponseAfterExecute { get; set; }

    /// <summary>Se espera antes de aplicar la petición (para forzar el orden entre dos equipos).</summary>
    public Func<string, Task>? BeforeExecuteAsync { get; set; }

    /// <summary>La instrucción cuyo SQL cumple esto falla con SQLITE_CONSTRAINT, sin ejecutarse.</summary>
    public Func<string, bool>? FailWhen { get; set; }

    /// <summary>Hora del servidor (UTC). null = la hora real de la máquina.</summary>
    public Func<DateTime>? Clock { get; set; }

    /// <summary>Cuota agotada a pedido: ninguna instrucción se aplica.</summary>
    public FakeBlockedMode Blocked { get; set; }

    /// <summary>Estado HTTP de <see cref="FakeBlockedMode.HttpError"/>.</summary>
    public HttpStatusCode BlockedHttpStatus { get; set; } = HttpStatusCode.Forbidden;

    /// <summary>
    /// Tope del cuerpo de la respuesta (V12: falta el tope real de Turso). La instrucción que lo pasa
    /// responde con el error RESPONSE_TOO_LARGE. null = sin tope.
    /// </summary>
    public int? MaxResponseBytes { get; set; }

    /// <summary>Desde el paso 8.6 un SCAN de una tabla sincronizada hace fallar la instrucción (F1, V15).</summary>
    public FakeScanPolicy ScanPolicy { get; set; } = FakeScanPolicy.Fail;

    /// <summary>Instrucciones a las que se les permite un SCAN aunque <see cref="ScanPolicy"/> sea Fail.</summary>
    public List<Func<string, bool>> ScanExceptions { get; } = new();

    /// <summary>Recorridos completos de tablas sincronizadas que se vieron ("SCAN Products ← SELECT …").</summary>
    public IReadOnlyList<string> ScanWarnings
    {
        get { lock (_scanWarnings) return _scanWarnings.ToList(); }
    }

    /// <summary>Peticiones atendidas, en orden.</summary>
    public IReadOnlyList<FakeRecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public void ClearRecording()
    {
        lock (_requests) _requests.Clear();
        lock (_scanWarnings) _scanWarnings.Clear();
    }

    public FakeHranaServer(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _connection.Open();
    }

    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Para usar <see cref="Connection"/> desde la prueba sin cruzarse con una petición en curso: la
    /// conexión de SQLite no admite dos usos a la vez (en la CI fallaba de vez en cuando).
    /// </summary>
    public IDisposable Lock()
    {
        _gate.Wait();
        return new GateRelease(_gate);
    }

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsOffline) throw new HttpRequestException("Sin red (simulada)");
        var text = await request.Content!.ReadAsStringAsync(cancellationToken);

        // Los ganchos ven el SQL tal cual (en el JSON las comillas simples van escapadas), también el de los pasos de un batch
        var sql = string.Join("\n", JsonNode.Parse(text)!["requests"]!.AsArray()
            .SelectMany(Statements)
            .Select(s => s["sql"]?.GetValue<string>())
            .Where(q => q != null));
        if (BeforeExecuteAsync != null) await BeforeExecuteAsync(sql);

        HttpResponseMessage response;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new HttpRequestException("Servidor simulado cerrado");
            response = Process(text);
        }
        finally
        {
            _gate.Release();
        }

        if (DropResponseAfterExecute?.Invoke(sql) == true) throw new HttpRequestException("Se perdió la respuesta (simulada)");
        return response;
    }

    private HttpResponseMessage Process(string text)
    {
        RequestCount++;
        var number = RequestCount;
        var body = JsonNode.Parse(text)!;
        var results = new JsonArray();
        var recorded = new List<FakeRecordedStatement>();
        long responseBytes = 0;

        if (Blocked == FakeBlockedMode.HttpError)
        {
            Record(number, recorded, BlockedHttpStatus);
            return new HttpResponseMessage(BlockedHttpStatus)
            {
                Content = new StringContent("{\"error\":\"Operation was blocked: monthly quota exceeded\",\"code\":\"BLOCKED\"}", Encoding.UTF8, "application/json")
            };
        }

        // Validación estricta del protocolo (como el servidor real): un entero como número JSON rechaza todo
        foreach (var stmt in body["requests"]!.AsArray().SelectMany(Statements))
        {
            foreach (var arg in stmt["args"]?.AsArray() ?? new JsonArray())
            {
                if (arg!["type"]!.GetValue<string>() == "integer" && arg["value"] is JsonValue v && v.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    Record(number, recorded, HttpStatusCode.BadRequest);
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("Failed to deserialize the JSON body: invalid type: integer, expected a string")
                    };
                }
            }
        }

        foreach (var req in body["requests"]!.AsArray())
        {
            var type = req!["type"]!.GetValue<string>();
            if (type == "close")
            {
                results.Add(new JsonObject { ["type"] = "ok", ["response"] = new JsonObject { ["type"] = "close" } });
                continue;
            }

            if (type == "batch")
            {
                results.Add(RunBatch(req["batch"]!["steps"]!.AsArray(), recorded, ref responseBytes));
                continue;
            }

            var (result, _) = RunStatement(req["stmt"]!, recorded, ref responseBytes);
            results.Add(result);
        }

        // Al cerrar el stream, una transacción que quedó abierta se deshace
        if (raw.sqlite3_get_autocommit(_connection.Handle!) == 0) Execute("ROLLBACK", new JsonArray());

        Record(number, recorded, HttpStatusCode.OK);
        var payload = new JsonObject { ["baton"] = null, ["results"] = results };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>Las instrucciones de una petición: la suya ("execute") o las de sus pasos ("batch").</summary>
    private static IEnumerable<JsonNode> Statements(JsonNode? request)
    {
        if (request?["stmt"] is { } stmt) yield return stmt;
        foreach (var step in request?["batch"]?["steps"]?.AsArray() ?? new JsonArray())
        {
            if (step?["stmt"] is { } stepStmt) yield return stepStmt;
        }
    }

    /// <summary>
    /// Una instrucción: su resultado Hrana ({"type":"ok",…} o {"type":"error",…}) y, si salió bien, el "result" con
    /// columnas, filas y filas afectadas. Se graba con lo que habría cobrado Turso.
    /// </summary>
    private (JsonObject Response, JsonObject? Result) RunStatement(JsonNode stmt, List<FakeRecordedStatement> recorded, ref long responseBytes)
    {
        var sql = stmt["sql"]!.GetValue<string>();
        var args = stmt["args"]?.AsArray() ?? new JsonArray();

        if (Blocked == FakeBlockedMode.StatementError)
        {
            recorded.Add(new FakeRecordedStatement(sql, (JsonArray)args.DeepClone(), 0, 0, "BLOCKED"));
            return (Error("Operation was blocked: monthly quota exceeded", "BLOCKED"), null);
        }

        StatementCount++;
        JsonObject response;
        JsonObject? result = null;
        long rowsRead = 0, rowsWritten = 0;
        string? errorCode = null;
        try
        {
            var scanError = ReviewQueryPlan(sql, args);
            if (scanError != null)
            {
                errorCode = "SCAN_FORBIDDEN";
                response = Error(scanError, errorCode);
            }
            else if (FailWhen?.Invoke(sql) == true)
            {
                errorCode = "SQLITE_CONSTRAINT";
                response = Error("Falla simulada de la instrucción", errorCode);
            }
            else
            {
                var executed = Execute(sql, args);
                rowsRead = executed.RowsRead;
                rowsWritten = executed.RowsWritten;
                result = executed.Result;
                response = new JsonObject { ["type"] = "ok", ["response"] = new JsonObject { ["type"] = "execute", ["result"] = result } };
            }
        }
        catch (FakeSqliteException ex)
        {
            errorCode = "SQLITE_ERROR";
            response = Error(ex.Message, errorCode);
        }

        responseBytes += Encoding.UTF8.GetByteCount(response.ToJsonString());
        if (MaxResponseBytes is { } max && responseBytes > max)
        {
            errorCode = "RESPONSE_TOO_LARGE";
            response = Error($"Response is too large (más de {max} bytes, simulado)", errorCode);
            result = null;
        }

        BilledRowsRead += rowsRead;
        BilledRowsWritten += rowsWritten;
        recorded.Add(new FakeRecordedStatement(sql, (JsonArray)args.DeepClone(), rowsRead, rowsWritten, errorCode));
        return (response, result);
    }

    /// <summary>
    /// Una petición "batch" de Hrana: cada paso corre si su condición se cumple (ok, error, not, and, or de los pasos
    /// anteriores) y responde con step_results y step_errors (null en los pasos que no corrieron). BEGIN, COMMIT y
    /// ROLLBACK son transacciones reales de SQLite, así que la petición es atómica como en Turso (V14).
    /// </summary>
    private JsonObject RunBatch(JsonArray steps, List<FakeRecordedStatement> recorded, ref long responseBytes)
    {
        if (Blocked == FakeBlockedMode.StatementError)
        {
            recorded.Add(new FakeRecordedStatement("batch", new JsonArray(), 0, 0, "BLOCKED"));
            return Error("Operation was blocked: monthly quota exceeded", "BLOCKED");
        }

        var outcomes = new List<bool?>();
        var stepResults = new JsonArray();
        var stepErrors = new JsonArray();
        foreach (var step in steps)
        {
            if (step!["condition"] is { } condition && !Evaluate(condition, outcomes))
            {
                outcomes.Add(null);
                stepResults.Add(null);
                stepErrors.Add(null);
                continue;
            }

            var (response, result) = RunStatement(step["stmt"]!, recorded, ref responseBytes);
            bool ok = response["type"]!.GetValue<string>() == "ok";
            outcomes.Add(ok);
            stepResults.Add(ok ? result!.DeepClone() : null);
            stepErrors.Add(ok ? null : response["error"]!.DeepClone());
        }

        return new JsonObject
        {
            ["type"] = "ok",
            ["response"] = new JsonObject
            {
                ["type"] = "batch",
                ["result"] = new JsonObject { ["step_results"] = stepResults, ["step_errors"] = stepErrors }
            }
        };
    }

    private bool Evaluate(JsonNode condition, List<bool?> outcomes)
    {
        int Step() => condition["step"]!.GetValue<int>();
        return condition["type"]!.GetValue<string>() switch
        {
            "ok" => Step() < outcomes.Count && outcomes[Step()] == true,
            "error" => Step() < outcomes.Count && outcomes[Step()] == false,
            "not" => !Evaluate(condition["cond"]!, outcomes),
            "and" => condition["conds"]!.AsArray().All(c => Evaluate(c!, outcomes)),
            "or" => condition["conds"]!.AsArray().Any(c => Evaluate(c!, outcomes)),
            "is_autocommit" => raw.sqlite3_get_autocommit(_connection.Handle!) != 0,
            var other => throw new InvalidOperationException($"Condición de batch desconocida: {other}")
        };
    }

    private void Record(int number, List<FakeRecordedStatement> statements, HttpStatusCode status)
    {
        lock (_requests) _requests.Add(new FakeRecordedRequest(number, statements, status));
    }

    private static JsonObject Error(string message, string code) =>
        new() { ["type"] = "error", ["error"] = new JsonObject { ["message"] = message, ["code"] = code } };

    private sealed class FakeSqliteException(string message) : Exception(message);

    // ================================================================== ejecución

    private sealed record Executed(JsonObject Result, long RowsRead, long RowsWritten);

    private static readonly Regex LimitAtEnd = new(@"\bLIMIT\s+\d+\s*;?\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex TransactionControl = new(@"^\s*(BEGIN|COMMIT|ROLLBACK|END)\b", RegexOptions.IgnoreCase);
    private static readonly Regex AlterTable = new(@"^\s*ALTER\s+TABLE\s+""?(\w+)""?", RegexOptions.IgnoreCase);
    private static readonly Regex CreateIndex = new(
        @"^\s*CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?""?(\w+)""?\s+ON\s+""?(\w+)""?", RegexOptions.IgnoreCase);

    /// <summary>La hora del servidor reemplaza 'now' (el único uso de la hora en las instrucciones de la app).</summary>
    private string WithServerClock(string sql)
    {
        if (Clock == null) return sql;
        var now = Clock().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return sql.Replace("'now'", $"'{now}'", StringComparison.Ordinal);
    }

    private Executed Execute(string originalSql, JsonArray args)
    {
        var sql = WithServerClock(originalSql);
        var db = _connection.Handle!;

        // ALTER TABLE y CREATE INDEX recorren toda la tabla en Turso (una lectura por fila)
        long ddlScan = 0;
        var alter = AlterTable.Match(sql);
        var index = CreateIndex.Match(sql);
        if (alter.Success) ddlScan = CountRows(alter.Groups[1].Value);
        else if (index.Success && !IndexExists(index.Groups[1].Value)) ddlScan = CountRows(index.Groups[2].Value);

        var changesBefore = raw.sqlite3_total_changes(db);
        using var stmt = Prepare(sql, args);

        var cols = new JsonArray();
        int columnCount = raw.sqlite3_column_count(stmt);
        for (int i = 0; i < columnCount; i++) cols.Add(new JsonObject { ["name"] = raw.sqlite3_column_name(stmt, i).utf8_to_string() });

        var rows = new JsonArray();
        while (true)
        {
            int rc = raw.sqlite3_step(stmt);
            if (rc == raw.SQLITE_DONE) break;
            if (rc != raw.SQLITE_ROW) throw new FakeSqliteException(raw.sqlite3_errmsg(db).utf8_to_string());

            var row = new JsonArray();
            for (int i = 0; i < columnCount; i++) row.Add(ReadColumn(stmt, i));
            rows.Add(row);
        }

        // SQLite cuenta los avances entre filas de un recorrido completo: n filas son n − 1 avances
        long fullScanSteps = raw.sqlite3_stmt_status(stmt, raw.SQLITE_STMTSTATUS_FULLSCAN_STEP, 0);
        if (fullScanSteps > 0) fullScanSteps++;
        bool sorted = raw.sqlite3_stmt_status(stmt, raw.SQLITE_STMTSTATUS_SORT, 0) > 0;
        long changes = raw.sqlite3_total_changes(db) - changesBefore;

        // Un ORDER BY que se resuelve ordenando lee todas las filas candidatas antes de cortar en el LIMIT. Si solo ordena
        // el último término (recorre el índice en orden, como la consulta de páginas desde el paso 8.6), se detiene al
        // llenar el LIMIT: cuentan las filas devueltas (en Turso, V15: ~2,000 leídas por página de 500)
        long sortedCandidates = sorted && LimitAtEnd.IsMatch(originalSql) && !SortsOnlyLastTerms(sql, args) ? CountCandidates(sql, args) : 0;

        // BEGIN, COMMIT y ROLLBACK no tocan filas
        long rowsRead = TransactionControl.IsMatch(sql) ? 0 : EstimateRowsRead(fullScanSteps, sortedCandidates, rows.Count, changes, ddlScan);
        var result = new JsonObject { ["cols"] = cols, ["rows"] = rows, ["affected_row_count"] = changes };
        return new Executed(result, rowsRead, changes);
    }

    /// <summary>
    /// Filas leídas que cobraría Turso: las recorridas (pasos de un recorrido completo, filas que entraron a
    /// un ordenamiento antes del LIMIT, filas devueltas o cambiadas, o la tabla entera en ALTER y CREATE INDEX),
    /// y 1 si la instrucción no tocó filas. Es una estimación: SQLite no expone las filas leídas por un índice.
    /// </summary>
    public static long EstimateRowsRead(long fullScanSteps, long sortedCandidates, long rowsReturned, long rowsChanged, long ddlScan) =>
        Math.Max(1, new[] { fullScanSteps, sortedCandidates, rowsReturned, rowsChanged, ddlScan }.Max());

    private sqlite3_stmt Prepare(string sql, JsonArray args)
    {
        var db = _connection.Handle!;
        if (raw.sqlite3_prepare_v2(db, sql, out var stmt) != raw.SQLITE_OK)
        {
            var message = raw.sqlite3_errmsg(db).utf8_to_string();
            stmt?.Dispose();
            throw new FakeSqliteException(message);
        }

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i]!;
            int position = i + 1;
            switch (arg["type"]!.GetValue<string>())
            {
                case "integer":
                    raw.sqlite3_bind_int64(stmt, position, long.Parse(arg["value"]!.GetValue<string>(), CultureInfo.InvariantCulture));
                    break;
                case "float":
                    raw.sqlite3_bind_double(stmt, position, arg["value"]!.GetValue<double>());
                    break;
                case "text":
                    raw.sqlite3_bind_text(stmt, position, arg["value"]!.GetValue<string>());
                    break;
                case "blob":
                    raw.sqlite3_bind_blob(stmt, position, Convert.FromBase64String(arg["base64"]!.GetValue<string>()));
                    break;
                default:
                    raw.sqlite3_bind_null(stmt, position);
                    break;
            }
        }
        return stmt;
    }

    private static JsonObject ReadColumn(sqlite3_stmt stmt, int i) => raw.sqlite3_column_type(stmt, i) switch
    {
        raw.SQLITE_NULL => new JsonObject { ["type"] = "null" },
        raw.SQLITE_INTEGER => new JsonObject { ["type"] = "integer", ["value"] = raw.sqlite3_column_int64(stmt, i).ToString(CultureInfo.InvariantCulture) },
        raw.SQLITE_FLOAT => new JsonObject { ["type"] = "float", ["value"] = raw.sqlite3_column_double(stmt, i) },
        raw.SQLITE_BLOB => new JsonObject { ["type"] = "blob", ["base64"] = Convert.ToBase64String(raw.sqlite3_column_blob(stmt, i).ToArray()) },
        _ => new JsonObject { ["type"] = "text", ["value"] = raw.sqlite3_column_text(stmt, i).utf8_to_string() }
    };

    private long ScalarLong(string sql, JsonArray args)
    {
        using var stmt = Prepare(sql, args);
        return raw.sqlite3_step(stmt) == raw.SQLITE_ROW ? raw.sqlite3_column_int64(stmt, 0) : 0;
    }

    private long CountRows(string table)
    {
        try
        {
            return ScalarLong($"SELECT COUNT(*) FROM \"{table}\"", new JsonArray());
        }
        catch (FakeSqliteException)
        {
            return 0; // La tabla todavía no existe
        }
    }

    private bool IndexExists(string name) =>
        ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = ?",
            new JsonArray(new JsonObject { ["type"] = "text", ["value"] = name })) > 0;

    /// <summary>"USE TEMP B-TREE FOR LAST TERM OF ORDER BY" (o "LAST n TERMS"): un ordenamiento parcial.</summary>
    private bool SortsOnlyLastTerms(string sql, JsonArray args)
    {
        try
        {
            using var stmt = Prepare("EXPLAIN QUERY PLAN " + sql, args);
            int detailColumn = raw.sqlite3_column_count(stmt) - 1;
            while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
            {
                if (raw.sqlite3_column_text(stmt, detailColumn).utf8_to_string().Contains("FOR LAST", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (FakeSqliteException)
        {
            return false;
        }
    }

    private long CountCandidates(string sql, JsonArray args)
    {
        try
        {
            return ScalarLong($"SELECT COUNT(*) FROM ({LimitAtEnd.Replace(sql, string.Empty)})", args);
        }
        catch (FakeSqliteException)
        {
            return 0;
        }
    }

    // ================================================================== planes de consulta

    private static readonly Regex PlannedStatement = new(@"^\s*(SELECT|WITH|INSERT|UPDATE|DELETE|REPLACE)\b", RegexOptions.IgnoreCase);
    private static readonly Regex DdlStatement = new(@"^\s*(CREATE|ALTER|DROP)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ScanDetail = new(@"^SCAN\s+""?(\w+)""?", RegexOptions.IgnoreCase);

    /// <summary>
    /// EXPLAIN QUERY PLAN de cada consulta: un recorrido completo de una tabla sincronizada (las que tienen
    /// SyncedAtUtc) se anota o, con <see cref="FakeScanPolicy.Fail"/>, hace fallar la instrucción. V15: el
    /// planificador de libSQL puede elegir otro plan que el de este SQLite.
    /// </summary>
    private string? ReviewQueryPlan(string sql, JsonArray args)
    {
        if (DdlStatement.IsMatch(sql))
        {
            _syncedTables.Clear();
            return null;
        }
        if (!PlannedStatement.IsMatch(sql)) return null;

        var scans = new List<string>();
        try
        {
            using var stmt = Prepare("EXPLAIN QUERY PLAN " + WithServerClock(sql), args);
            int detailColumn = raw.sqlite3_column_count(stmt) - 1;
            while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
            {
                var detail = raw.sqlite3_column_text(stmt, detailColumn).utf8_to_string();
                var match = ScanDetail.Match(detail);
                if (match.Success && IsSyncedTable(match.Groups[1].Value)) scans.Add(detail);
            }
        }
        catch (FakeSqliteException)
        {
            return null; // La instrucción misma dará el error al ejecutarse
        }

        if (scans.Count == 0) return null;
        var warning = $"{string.Join("; ", scans)} ← {sql}";
        if (ScanPolicy == FakeScanPolicy.Fail && !ScanExceptions.Any(allowed => allowed(sql)))
            return $"Recorrido completo de una tabla sincronizada: {warning}";

        lock (_scanWarnings) _scanWarnings.Add(warning);
        Console.WriteLine($"[FakeHranaServer] Aviso de SCAN: {warning}");
        return null;
    }

    private bool IsSyncedTable(string table)
    {
        if (_syncedTables.TryGetValue(table, out var synced)) return synced;
        synced = ScalarLong("SELECT COUNT(*) FROM pragma_table_info(?) WHERE name = 'SyncedAtUtc'",
            new JsonArray(new JsonObject { ["type"] = "text", ["value"] = table })) > 0;
        _syncedTables[table] = synced;
        return synced;
    }

    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Una petición en segundo plano (p. ej. borrar la copia de una consulta) puede seguir en
            // curso al terminar la prueba: se espera a que termine y las siguientes fallan como sin red
            _gate.Wait();
            try
            {
                _disposed = true;
                _connection.Dispose();
            }
            finally
            {
                _gate.Release();
            }
        }
        base.Dispose(disposing);
    }
}
