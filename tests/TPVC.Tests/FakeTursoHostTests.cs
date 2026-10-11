using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.13: el servidor simulado por HTTP en 127.0.0.1. Una prueba normal comprueba que una PC sincroniza por HTTP de
/// verdad; la de la categoría FakeTursoHost lo deja escuchando para la CI de Kotlin (solo con TPVC_FAKE_TURSO_HOST=1).
/// </summary>
[TestClass]
public class FakeTursoHostTests
{
    private string _folder = null!;

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"tpvc_fakehost_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>Un equipo que lee la nube por HTTP, con el cliente de siempre (sin el servidor en el mismo proceso).</summary>
    private (IDbContextFactory<AppDbContext> Factory, TursoSyncService Sync) Reader(Uri url, string token)
    {
        var dir = Path.Combine(_folder, "lector");
        Directory.CreateDirectory(dir);
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={Path.Combine(dir, "consultorio.db")}");
        using (var db = new AppDbContext(builder.Options))
        {
            db.Database.EnsureCreated();
            DatabaseInitializer.EnsureSchema(db);
        }
        var factory = new AppDbContextFactory(builder.Options);
        var settings = new TursoSettingsService(Path.Combine(dir, "turso.json"));
        var s = settings.CurrentSettings;
        s.IsSyncEnabled = true;
        s.AutoSyncOnChanges = false;
        s.DatabaseUrl = url.ToString().TrimEnd('/');
        s.AuthToken = token;
        s.DeviceId = "device-lector";
        s.DeviceName = "Lector";
        settings.SaveSettings(s);
        var sync = new TursoSyncService(factory, settings, new ClinicProfileService(Path.Combine(dir, "clinic_profile.json")), new InventoryService(factory))
        {
            AutoLabTransfers = false
        };
        return (factory, sync);
    }

    [TestMethod]
    public async Task TheHost_ServesARealSyncOverHttp_OnlyOnLoopback()
    {
        using var server = new FakeHranaServer(Path.Combine(_folder, "nube.db"));
        using var host = new FakeTursoHost(server);
        Assert.AreEqual("127.0.0.1", host.BaseUri.Host, "Nunca escucha fuera del equipo");
        await FakeTursoHost.SeedDemoCloudAsync(server, Path.Combine(_folder, "pc"));

        using (var http = new HttpClient())
        {
            Assert.AreEqual("ok", await http.GetStringAsync(new Uri(host.BaseUri, "health")));
            Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(host.BaseUri, "otra-cosa"))).StatusCode);
        }

        var (factory, sync) = Reader(host.BaseUri, "kotlin-ci");
        Assert.IsTrue((await sync.JoinCloudAsync()).Ok);
        var result = await sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        using (var db = factory.CreateDbContext())
        {
            Assert.IsGreaterThan(0, await db.Patients.CountAsync(), "Bajó los pacientes de la PC por HTTP");
            Assert.IsGreaterThan(0, await db.Products.CountAsync());
        }

        // Con los tokens rotados, el 401 llega por HTTP igual que en el mismo proceso
        server.AcceptedTokens = new() { "otro" };
        var rejected = await sync.SyncAsync();
        Assert.IsFalse(rejected.Success);
        StringAssert.Contains(rejected.ErrorMessage, "401");
    }

    /// <summary>
    /// Paso 8.15: lo que usa verify-turso-hrana.ps1 -Rotate de la API de la plataforma (listar, anular los tokens y crear
    /// uno), para probar el script sin una cuenta de Turso.
    /// </summary>
    [TestMethod]
    public async Task ThePlatformApiStandIn_InvalidatesTheTokens_AndMintsANewOne()
    {
        using var server = new FakeHranaServer(Path.Combine(_folder, "nube.db")) { AcceptedTokens = new() { "token-viejo" } };
        using var host = new FakeTursoHost(server) { PlatformApiToken = "api-secreto", GroupPeers = new[] { "consultorio" } };
        using var http = new HttpClient { BaseAddress = host.BaseUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "api-secreto");
        const string db = $"v1/organizations/simulada/databases/{FakeTursoHost.FakeDatabaseName}/auth/";

        var list = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync("v1/organizations/simulada/databases"))!["databases"]!.AsArray();
        Assert.HasCount(2, list);
        Assert.AreEqual("127.0.0.1", list.Single(d => (string?)d!["Name"] == FakeTursoHost.FakeDatabaseName)!["Hostname"]!.GetValue<string>());
        Assert.IsTrue(list.All(d => (string?)d!["group"] == FakeTursoHost.FakeGroupName), "El script se niega a rotar con otra base en el grupo");

        using var old = new TursoClient(host.BaseUri.ToString(), "token-viejo");
        Assert.IsTrue((await old.TestConnectionAsync()).Success);
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsync(db + "rotate", null)).StatusCode);
        Assert.IsFalse((await old.TestConnectionAsync()).Success, "turso db tokens invalidate: el token viejo ya no sirve");

        var jwt = System.Text.Json.Nodes.JsonNode.Parse(await (await http.PostAsync(db + "tokens", null)).Content.ReadAsStringAsync())!["jwt"]!.GetValue<string>();
        using var renewed = new TursoClient(host.BaseUri.ToString(), jwt);
        Assert.IsTrue((await renewed.TestConnectionAsync()).Success);

        using var stranger = new HttpClient { BaseAddress = host.BaseUri };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await stranger.GetAsync("v1/organizations/simulada/databases")).StatusCode);
    }

    /// <summary>
    /// Para la CI de Kotlin: con TPVC_FAKE_TURSO_HOST=1 siembra la nube con los datos de demostración y escucha en
    /// 127.0.0.1:TPVC_FAKE_TURSO_PORT (8787) hasta GET /__stop o TPVC_FAKE_TURSO_MINUTES (30). Escribe la URL en
    /// TPVC_FAKE_TURSO_READY_FILE cuando está lista. Sin la variable no hace nada.
    /// </summary>
    [TestMethod]
    [TestCategory("FakeTursoHost")]
    public async Task ServeForTheKotlinCi()
    {
        if (Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_HOST") != "1")
        {
            Console.WriteLine("TPVC_FAKE_TURSO_HOST no es 1: no se levanta el servidor (ver docs/FAKE_HRANA_SPEC.md).");
            return;
        }

        int port = int.TryParse(Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 8787;
        double minutes = double.TryParse(Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_MINUTES"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 30;
        var token = Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_TOKEN");

        using var server = new FakeHranaServer(Path.Combine(_folder, "nube.db"));
        // TPVC_FAKE_TURSO_SEED=none: una nube vacía (para probar los scripts de comprobación, que se niegan a correr en una
        // base con datos)
        if (Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_SEED") != "none") await FakeTursoHost.SeedDemoCloudAsync(server, Path.Combine(_folder, "pc"));
        if (!string.IsNullOrEmpty(token)) server.AcceptedTokens = new() { token };

        using var host = new FakeTursoHost(server, port);
        // Paso 8.15: la API de la plataforma para probar la rotación de verify-turso-hrana.ps1 (-ApiBase)
        if (Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_API_TOKEN") is { Length: > 0 } apiToken) host.PlatformApiToken = apiToken;
        if (Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_GROUP_PEERS") is { Length: > 0 } peers) host.GroupPeers = peers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var url = host.BaseUri.ToString().TrimEnd('/');
        Console.WriteLine($"FakeTurso listo en {url}");
        if (Environment.GetEnvironmentVariable("TPVC_FAKE_TURSO_READY_FILE") is { Length: > 0 } ready) await File.WriteAllTextAsync(ready, url);

        await Task.WhenAny(host.StopRequested, Task.Delay(TimeSpan.FromMinutes(minutes)));
        Console.WriteLine($"FakeTurso termina: {server.RequestCount} peticiones atendidas.");
    }
}
