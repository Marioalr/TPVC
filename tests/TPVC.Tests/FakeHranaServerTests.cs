using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>Los instrumentos del servidor Turso simulado (paso 8.2): si mienten, las pruebas de los pasos siguientes también.</summary>
[TestClass]
public class FakeHranaServerTests
{
    private string _dbPath = string.Empty;
    private FakeHranaServer _server = null!;
    private TursoClient _client = null!;
    private FakeScanPolicy _defaultScanPolicy;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_fake_{Guid.NewGuid():N}.db");
        _server = new FakeHranaServer(_dbPath);

        // Estas pruebas recorren T completa a propósito: solo avisan (la de fábrica es fallar, ver abajo)
        _defaultScanPolicy = _server.ScanPolicy;
        _server.ScanPolicy = FakeScanPolicy.Warn;
        _client = new TursoClient("https://tpvc-test.turso.io", "token", new HttpClient(_server, disposeHandler: false));

        // Una tabla "sincronizada" (tiene SyncedAtUtc) con 300 filas, índice en A y sin índice en B ni C
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE T (SyncId TEXT PRIMARY KEY, SyncedAtUtc TEXT NOT NULL, A INTEGER, B INTEGER, C TEXT); " +
                          "CREATE INDEX IX_T_A ON T (A); " +
                          "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300) " +
                          "INSERT INTO T SELECT printf('id-%03d', i), '2026-01-01', i, i % 7, printf('c-%03d', 300 - i) FROM n;";
        cmd.ExecuteNonQuery();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private FakeRecordedStatement LastStatement() => _server.Requests[^1].Statements[^1];

    [TestMethod]
    public async Task ServerClock_ReplacesNow()
    {
        _server.Clock = () => new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var rows = await _client.QueryAsync($"SELECT {TursoSyncService.ServerNowSql} AS Ahora, strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds') AS Antes;");
        Assert.AreEqual("2030-01-02 03:04:05.000", rows[0]["Ahora"]);
        Assert.AreEqual("2030-01-02 03:04:00.000", rows[0]["Antes"]);
    }

    [TestMethod]
    public async Task BilledReads_CountEveryRowWalked_AndOneForAStatementThatTouchesNone()
    {
        await _client.QueryAsync("SELECT * FROM T WHERE SyncId = ?;", new object?[] { "id-150" });
        Assert.AreEqual(1, LastStatement().RowsRead, "Búsqueda por la llave: una fila");

        await _client.QueryAsync("SELECT * FROM T WHERE B = ?;", new object?[] { 3 });
        Assert.AreEqual(300, LastStatement().RowsRead, "Sin índice se recorre toda la tabla aunque regrese 43 filas");

        // Ordenar por una columna sin índice lee todas las candidatas antes de cortar en el LIMIT (como F1)
        await _client.QueryAsync("SELECT * FROM T WHERE A > ? ORDER BY C LIMIT 10;", new object?[] { 100 });
        Assert.AreEqual(200, LastStatement().RowsRead);

        await _client.ExecuteAsync("UPDATE T SET C = 'x' WHERE SyncId = ?;", new object?[] { "id-001" });
        Assert.AreEqual(1, LastStatement().RowsWritten);
        await _client.ExecuteAsync("ALTER TABLE T ADD COLUMN D TEXT;");
        Assert.AreEqual(300, LastStatement().RowsRead, "ALTER TABLE recorre toda la tabla en Turso");

        await _client.QueryAsync("SELECT 1;");
        Assert.AreEqual(1, LastStatement().RowsRead, "Una instrucción que no toca filas cuenta como 1");
        Assert.AreEqual(_server.Requests.SelectMany(r => r.Statements).Sum(s => s.RowsRead), _server.BilledRowsRead);
    }

    [TestMethod]
    public async Task FullScanOfASyncedTable_FailsByDefaultSinceStep86_UnlessExcepted_OrWarnsOnRequest()
    {
        Assert.AreEqual(FakeScanPolicy.Fail, _defaultScanPolicy, "Desde el paso 8.6 un SCAN de una tabla sincronizada falla");

        await _client.QueryAsync("SELECT * FROM T WHERE B = 1;");
        Assert.AreEqual(1, _server.ScanWarnings.Count);
        StringAssert.Contains(_server.ScanWarnings[0], "SCAN T");

        await _client.QueryAsync("SELECT * FROM T WHERE A = 5;");
        Assert.AreEqual(1, _server.ScanWarnings.Count, "Una búsqueda por índice no avisa");

        _server.ScanPolicy = FakeScanPolicy.Fail;
        var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => _client.QueryAsync("SELECT * FROM T WHERE B = 1;"));
        Assert.AreEqual("SCAN_FORBIDDEN", ex.ErrorCode);

        _server.ScanExceptions.Add(sql => sql.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        var count = await _client.QueryAsync("SELECT COUNT(*) AS N FROM T WHERE B = 1;");
        Assert.AreEqual(43L, count[0]["N"]);
    }

    [TestMethod]
    public async Task Batch_WithBeginAndCommit_IsAtomic_AndReturnsEachStatement()
    {
        // Paso 8.6 (V14): lo que hace Turso con un batch BEGIN … COMMIT, y ROLLBACK si un paso falla
        var ok = await _client.TransactionAsync(new (string, object?[]?)[]
        {
            ("UPDATE T SET C = 'x' WHERE SyncId = ?;", new object?[] { "id-001" }),
            ("SELECT C FROM T WHERE SyncId = ?;", new object?[] { "id-001" })
        });
        Assert.AreEqual(1, ok[0].AffectedRows);
        Assert.AreEqual("x", ok[1].Rows.Single()["C"], "Dentro de la transacción se ve lo escrito");

        var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => _client.TransactionAsync(new (string, object?[]?)[]
        {
            ("UPDATE T SET C = 'y' WHERE SyncId = ?;", new object?[] { "id-002" }),
            ("INSERT INTO T (SyncId, SyncedAtUtc) VALUES ('id-001', '2026-01-01');", null)
        }));
        StringAssert.Contains(ex.Message, "UNIQUE");
        Assert.AreEqual("c-298", (await _client.QueryAsync("SELECT C FROM T WHERE SyncId = 'id-002';"))[0]["C"], "El error deshizo lo anterior");

        var steps = _server.Requests[^2].Statements.Select(s => s.Sql.Split(' ')[0]).ToList();
        CollectionAssert.AreEqual(new[] { "BEGIN", "UPDATE", "INSERT", "ROLLBACK" }, steps, "El COMMIT se salta y corre el ROLLBACK");
    }

    [TestMethod]
    public async Task ResponseCap_FailsTheStatementThatPassesIt()
    {
        _server.MaxResponseBytes = 2_000;
        var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => _client.QueryAsync("SELECT * FROM T;"));
        Assert.AreEqual("RESPONSE_TOO_LARGE", ex.ErrorCode);
        Assert.AreEqual(1, (await _client.QueryAsync("SELECT * FROM T WHERE SyncId = 'id-001';")).Count);
    }

    [TestMethod]
    public async Task Blocked_OnDemand_AsStatementErrorOrHttpError_NothingApplies()
    {
        _server.Blocked = FakeBlockedMode.StatementError;
        var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => _client.ExecuteAsync("DELETE FROM T;"));
        Assert.AreEqual(TursoException.QuotaExhaustedCode, ex.ErrorCode);

        _server.Blocked = FakeBlockedMode.HttpError;
        ex = await Assert.ThrowsExactlyAsync<TursoException>(() => _client.ExecuteAsync("DELETE FROM T;"));
        Assert.AreEqual(TursoException.QuotaExhaustedCode, ex.ErrorCode);

        _server.Blocked = FakeBlockedMode.None;
        var rows = await _client.QueryAsync("SELECT COUNT(*) AS N FROM T;");
        Assert.AreEqual(300L, rows[0]["N"], "Con la cuota agotada no se aplicó nada");
    }

    [TestMethod]
    public async Task Blob_RoundTrip_ComesBackAsBytes_AndTravelsWithoutBase64PaddingLikeTurso()
    {
        // Para el membrete en la nube (8.12) y los resultados de laboratorio. V18: falta verlo en Turso real
        await _client.ExecuteAsync("CREATE TABLE Assets (Id INTEGER PRIMARY KEY, Content BLOB);");
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // firma PNG: su base64 lleva un '='
        var four = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // dos '='
        await _client.ExecuteBatchAsync(new (string, object?[]?)[]
        {
            ("INSERT INTO Assets (Id, Content) VALUES (?, ?);", new object?[] { 1, signature }),
            ("INSERT INTO Assets (Id, Content) VALUES (?, ?);", new object?[] { 2, four }),
            ("INSERT INTO Assets (Id, Content) VALUES (?, ?);", new object?[] { 3, Array.Empty<byte>() }),
            ("INSERT INTO Assets (Id, Content) VALUES (?, ?);", new object?[] { 4, null })
        });

        var read = await _client.QueryAsync("SELECT Id, Content, typeof(Content) AS Kind FROM Assets ORDER BY Id;");
        CollectionAssert.AreEqual(signature, (byte[])read[0]["Content"]!);
        CollectionAssert.AreEqual(four, (byte[])read[1]["Content"]!);
        Assert.IsEmpty((byte[])read[2]["Content"]!);
        Assert.AreEqual("blob", read[2]["Kind"], "Un BLOB vacío no se guarda como NULL");
        Assert.IsNull(read[3]["Content"]);

        // En la respuesta el base64 va sin relleno, como lo manda Turso (libSQL usa STANDARD_NO_PAD)
        var (_, body) = await PostRawAsync("SELECT Content FROM Assets WHERE Id IN (1, 2, 3) ORDER BY Id;");
        var sent = JsonNode.Parse(body)!["results"]![0]!["response"]!["result"]!["rows"]!.AsArray()
            .Select(r => r![0]!["base64"]!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "iVBORw0KGgo", "iVBORw", "" }, sent);

        // Al recibir acepta las dos formas (libSQL quita los '=' antes de decodificar); un base64 inválido es HTTP 400
        Assert.AreEqual(HttpStatusCode.OK, (await PostRawAsync("INSERT INTO Assets (Id, Content) VALUES (5, ?);", Blob("iVBORw"))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await PostRawAsync("INSERT INTO Assets (Id, Content) VALUES (6, ?);", Blob("iVBORw=="))).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostRawAsync("INSERT INTO Assets (Id, Content) VALUES (7, ?);", Blob("iVBOR"))).Status);
        var same = await _client.QueryAsync("SELECT group_concat(Id) AS Ids FROM Assets WHERE Content = ?;", new object?[] { four });
        Assert.AreEqual("2,5,6", same[0]["Ids"]);
    }

    private static JsonObject Blob(string base64) => new() { ["type"] = "blob", ["base64"] = base64 };

    /// <summary>Una instrucción tal cual, sin pasar por TursoClient: para ver y mandar el JSON exacto.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PostRawAsync(string sql, params JsonObject[] args)
    {
        var stmt = new JsonObject { ["sql"] = sql, ["args"] = new JsonArray(args) };
        var request = new JsonObject { ["requests"] = new JsonArray(new JsonObject { ["type"] = "execute", ["stmt"] = stmt }, new JsonObject { ["type"] = "close" }) };
        using var http = new HttpClient(_server, disposeHandler: false);
        using var response = await http.PostAsync("https://tpvc-test.turso.io/v2/pipeline", new StringContent(request.ToJsonString()));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
