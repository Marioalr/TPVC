using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_fake_{Guid.NewGuid():N}.db");
        _server = new FakeHranaServer(_dbPath);
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
    public async Task FullScanOfASyncedTable_WarnsUntilStep86_ThenFailsUnlessExcepted()
    {
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
}
