using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.13: <see cref="FakeHranaServer"/> servido por HTTP, solo en 127.0.0.1, para que la CI de Kotlin y la PC
/// sincronicen contra el mismo servidor en un solo job (docs/FAKE_HRANA_SPEC.md). Vive en el proyecto de pruebas y nunca
/// entra a TPVC.exe. Atiende POST /v2/pipeline (con el token en Authorization), GET /health y GET /__stop.
/// </summary>
internal sealed class FakeTursoHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpMessageInvoker _invoker;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeTursoHost(FakeHranaServer server, int port = 0)
    {
        Server = server;
        BaseUri = new Uri($"http://127.0.0.1:{(port == 0 ? FreePort() : port)}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _invoker = new HttpMessageInvoker(server, disposeHandler: false);
        _loop = Task.Run(LoopAsync);
    }

    public FakeHranaServer Server { get; }

    /// <summary>http://127.0.0.1:puerto/ (TursoClient acepta http:// solo en 127.0.0.1 y localhost).</summary>
    public Uri BaseUri { get; }

    /// <summary>Se cumple cuando alguien pide GET /__stop.</summary>
    public Task StopRequested => _stopRequested.Task;

    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            if (request.HttpMethod == "POST" && path == "/v2/pipeline")
            {
                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                using var forwarded = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "v2/pipeline"))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (request.Headers["Authorization"] is { } authorization && AuthenticationHeaderValue.TryParse(authorization, out var parsed))
                    forwarded.Headers.Authorization = parsed;

                HttpResponseMessage answer;
                try
                {
                    answer = await _invoker.SendAsync(forwarded, _stop.Token);
                }
                catch (HttpRequestException ex)
                {
                    // Sin red simulada o respuesta perdida: el cliente ve un error de servicio
                    await WriteAsync(response, 503, "text/plain", ex.Message);
                    return;
                }
                using (answer)
                {
                    await WriteAsync(response, (int)answer.StatusCode, "application/json", await answer.Content.ReadAsStringAsync());
                }
            }
            else if (path.StartsWith("/v1/organizations/", StringComparison.Ordinal) && PlatformApiToken != null)
            {
                await HandlePlatformApiAsync(request, response, path);
            }
            else if (request.HttpMethod == "GET" && path == "/health")
            {
                await WriteAsync(response, 200, "text/plain", "ok");
            }
            else if (request.HttpMethod == "GET" && path == "/__stop")
            {
                await WriteAsync(response, 200, "text/plain", "adiós");
                _stopRequested.TrySetResult();
            }
            else
            {
                await WriteAsync(response, 404, "text/plain", "Solo POST /v2/pipeline, GET /health y GET /__stop");
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // El anfitrión se cerró a media respuesta
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------ API de la plataforma (solo lo de la rotación)

    /// <summary>
    /// Paso 8.15: con un valor, el anfitrión también hace de API de la plataforma de Turso para la rotación de
    /// scripts/verify-turso-hrana.ps1 -Rotate (-ApiBase http://127.0.0.1:puerto): listar las bases, anular los tokens de la
    /// base y crear uno. Pide este token en Authorization. null = sin API.
    /// </summary>
    public string? PlatformApiToken { get; set; }

    /// <summary>Otras bases en el mismo grupo que la de prueba (para ver que el script se niega a rotar).</summary>
    public string[] GroupPeers { get; set; } = Array.Empty<string>();

    public const string FakeDatabaseName = "prueba-simulada";
    public const string FakeGroupName = "pruebas";

    private async Task HandlePlatformApiAsync(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        var auth = request.Headers["Authorization"];
        if (auth != $"Bearer {PlatformApiToken}")
        {
            await WriteAsync(response, 401, "application/json", "{\"error\":\"token is invalid\"}");
            return;
        }

        // /v1/organizations/{org}/databases[/{name}/auth/{rotate|tokens}]
        var parts = path.Trim('/').Split('/');
        if (request.HttpMethod == "GET" && parts.Length == 4 && parts[3] == "databases")
        {
            // Como Turso: 'Hostname' y 'hostname' (solo cambian en mayúsculas; PowerShell 5.1 no los lee con ConvertFrom-Json).
            // Aquí el primero es la dirección vieja y el segundo la que usa el equipo
            var databases = new[] { FakeDatabaseName }.Concat(GroupPeers)
                .Select(name => new System.Text.Json.Nodes.JsonObject
                {
                    ["Name"] = name,
                    ["Hostname"] = $"{name}-simulada.turso.io",
                    ["hostname"] = name == FakeDatabaseName ? BaseUri.Host : $"{name}-simulada.aws-us-east-2.turso.io",
                    ["group"] = FakeGroupName
                });
            var json = new System.Text.Json.Nodes.JsonObject { ["databases"] = new System.Text.Json.Nodes.JsonArray(databases.ToArray<System.Text.Json.Nodes.JsonNode?>()) };
            await WriteAsync(response, 200, "application/json", json.ToJsonString());
        }
        else if (request.HttpMethod == "POST" && parts.Length == 7 && parts[4] == FakeDatabaseName && parts[5] == "auth" && parts[6] == "rotate")
        {
            // Como turso db tokens invalidate: ningún token anterior sirve
            Server.AcceptedTokens = new();
            await WriteAsync(response, 200, "application/json", string.Empty);
        }
        else if (request.HttpMethod == "POST" && parts.Length == 7 && parts[4] == FakeDatabaseName && parts[5] == "auth" && parts[6] == "tokens")
        {
            var jwt = $"simulado.{Guid.NewGuid():N}.token";
            // Un conjunto nuevo: el servidor lo lee desde otros hilos
            Server.AcceptedTokens = new(Server.AcceptedTokens ?? new()) { jwt };
            await WriteAsync(response, 200, "application/json", $"{{\"jwt\":\"{jwt}\"}}");
        }
        else
        {
            await WriteAsync(response, 404, "application/json", "{\"error\":\"could not find database\"}");
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType + "; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>
    /// Una nube lista para leer: una PC con los datos de demostración (pacientes, consultas, productos, lotes y ventas) que
    /// subió todo y verificó la nube («Sincronización completa»), como la del consultorio.
    /// </summary>
    public static async Task SeedDemoCloudAsync(FakeHranaServer server, string folder)
    {
        Directory.CreateDirectory(folder);
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={Path.Combine(folder, "consultorio.db")}");
        using (var db = new AppDbContext(builder.Options)) DatabaseInitializer.Initialize(db, seedDemoData: true);
        var factory = new AppDbContextFactory(builder.Options);

        var settings = new TursoSettingsService(Path.Combine(folder, "turso.json"));
        var s = settings.CurrentSettings;
        s.IsSyncEnabled = true;
        s.AutoSyncOnChanges = false;
        s.AutoSyncOnStartup = false;
        s.DatabaseUrl = "https://tpvc-fake.turso.io";
        s.AuthToken = "semilla";
        s.DeviceId = "device-pc";
        s.DeviceName = "PC del consultorio";
        settings.SaveSettings(s);

        var sync = new TursoSyncService(factory, settings, new ClinicProfileService(Path.Combine(folder, "clinic_profile.json")), new InventoryService(factory))
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(server, disposeHandler: false)),
            AutoLabTransfers = false
        };
        var result = await sync.InitialUploadToTursoAsync();
        if (!result.Success) throw new InvalidOperationException($"No se pudo sembrar la nube simulada: {result.ErrorMessage}");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _invoker.Dispose();
        _stop.Dispose();
    }
}
