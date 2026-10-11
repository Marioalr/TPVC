using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

[TestClass]
public class TursoTests
{
    private string _tempConfigPath = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"turso_test_{Guid.NewGuid():N}.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (File.Exists(_tempConfigPath)) File.Delete(_tempConfigPath);
        }
        catch { }
    }

    [TestMethod]
    public void TursoClient_NormalizePipelineUrl_FormatsCorrectly()
    {
        // 1. libsql scheme
        var url1 = TursoClient.NormalizePipelineUrl("libsql://my-db-mario.turso.io");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url1);

        // 2. https scheme without /v2/pipeline
        var url2 = TursoClient.NormalizePipelineUrl("https://my-db-mario.turso.io/");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url2);

        // 3. raw host
        var url3 = TursoClient.NormalizePipelineUrl("my-db-mario.turso.io");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url3);

        // 4. already full pipeline url
        var url4 = TursoClient.NormalizePipelineUrl("https://my-db-mario.turso.io/v2/pipeline");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url4);

        // 5. quoted url
        var url5 = TursoClient.NormalizePipelineUrl("\"libsql://my-db-mario.turso.io\"");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url5);

        // 6. turso:// scheme and query strings
        var url6 = TursoClient.NormalizePipelineUrl("turso://my-db-mario.turso.io?authToken=abc&tls=true");
        Assert.AreEqual("https://my-db-mario.turso.io/v2/pipeline", url6);

        // 7. bare db name without domain throws informative exception
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            TursoClient.NormalizePipelineUrl("clinica-db");
        });
    }

    [TestMethod]
    public void TursoClient_NormalizePipelineUrl_AcceptsHttpOnlyOnThisComputer()
    {
        // El token viaja en cada petición: sin cifrar solo hacia un servidor de pruebas en el mismo equipo
        var lan = Assert.ThrowsExactly<ArgumentException>(() => TursoClient.NormalizePipelineUrl("http://192.168.1.10:8080"));
        StringAssert.Contains(lan.Message, "http://");
        Assert.ThrowsExactly<ArgumentException>(() => TursoClient.NormalizePipelineUrl("http://consultorio.turso.io"));

        Assert.AreEqual("http://localhost:8080/v2/pipeline", TursoClient.NormalizePipelineUrl("http://localhost:8080"));
        Assert.AreEqual("http://127.0.0.1:8080/v2/pipeline", TursoClient.NormalizePipelineUrl("http://127.0.0.1:8080/"));
        Assert.AreEqual("https://192.168.1.10/v2/pipeline", TursoClient.NormalizePipelineUrl("https://192.168.1.10"));
    }

    [TestMethod]
    public async Task TursoClient_TranslatesBlocked_FromStatementErrorOrHttpBody()
    {
        // V7: Turso documenta el código BLOCKED, pero no si llega como error de la instrucción o de la petición
        var statementError = "{\"results\":[{\"type\":\"error\",\"error\":{\"message\":\"Operation was blocked\",\"code\":\"BLOCKED\"}}]}";
        using (var client = new TursoClient("https://dummy.turso.io", "token", new HttpClient(new MockHttpMessageHandler(HttpStatusCode.OK, statementError))))
        {
            var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => client.QueryAsync("SELECT 1;"));
            Assert.AreEqual(TursoException.QuotaExhaustedCode, ex.ErrorCode);
            StringAssert.StartsWith(ex.Message, "Se agotó la cuota mensual de Turso");
        }

        using (var client = new TursoClient("https://dummy.turso.io", "token",
                   new HttpClient(new MockHttpMessageHandler(HttpStatusCode.Forbidden, "{\"error\":\"BLOCKED: quota exceeded\"}"))))
        {
            var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => client.ExecuteAsync("DELETE FROM X;"));
            Assert.AreEqual(TursoException.QuotaExhaustedCode, ex.ErrorCode);
        }

        // Un 403 cualquiera sigue siendo un problema del token, no de la cuota
        using (var client = new TursoClient("https://dummy.turso.io", "token",
                   new HttpClient(new MockHttpMessageHandler(HttpStatusCode.Forbidden, "{\"error\":\"Unauthorized: invalid JWT\"}"))))
        {
            var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => client.QueryAsync("SELECT 1;"));
            Assert.AreEqual("HTTP_403", ex.ErrorCode);
        }
    }

    [TestMethod]
    public async Task TursoClient_TranslatesArchivedDatabase_WithInstructionsToUnarchive()
    {
        // V9: el texto exacto de Turso para una base archivada falta comprobarlo; se reconoce por "archived"
        using var client = new TursoClient("https://dummy.turso.io", "token",
            new HttpClient(new MockHttpMessageHandler(HttpStatusCode.BadRequest, "{\"error\":\"database is archived\"}")));
        var ex = await Assert.ThrowsExactlyAsync<TursoException>(() => client.QueryAsync("SELECT 1;"));
        Assert.AreEqual(TursoException.ArchivedCode, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "turso group unarchive");
    }

    [TestMethod]
    public void TursoClient_CleanToken_StripsQuotesWhitespaceAndBearerPrefix()
    {
        Assert.AreEqual("eyJhbGciOi...", TursoClient.CleanToken("Bearer eyJhbGciOi..."));
        Assert.AreEqual("eyJhbGciOi...", TursoClient.CleanToken("\"Bearer eyJhbGciOi...\""));
        Assert.AreEqual("eyJhbGciOi...", TursoClient.CleanToken("'bearer eyJhbGciOi...'"));
        Assert.AreEqual("eyJ123456", TursoClient.CleanToken("  eyJ123\r\n456  "));
        Assert.AreEqual(string.Empty, TursoClient.CleanToken(null));
        Assert.AreEqual(string.Empty, TursoClient.CleanToken("   "));
    }

    [TestMethod]
    public void TursoSettingsService_SaveAndLoad_PreservesTokensAndSettings()
    {
        var service = new TursoSettingsService(_tempConfigPath);
        var settings = new TursoSettings
        {
            IsSyncEnabled = true,
            DatabaseUrl = "https://clinica-test.turso.io",
            AuthToken = "turso-secret-test-token-123",
            DeviceName = "Consultorio Win11",
            DeviceRole = DeviceRole.WindowsDesktop,
            AutoSyncOnChanges = true,
            AutoSyncOnStartup = true
        };

        service.SaveSettings(settings);

        // Cargar en nueva instancia
        var reloaded = new TursoSettingsService(_tempConfigPath);
        Assert.IsTrue(reloaded.CurrentSettings.IsSyncEnabled);
        Assert.AreEqual("https://clinica-test.turso.io", reloaded.CurrentSettings.DatabaseUrl);
        Assert.AreEqual("turso-secret-test-token-123", reloaded.CurrentSettings.AuthToken);
        Assert.AreEqual(DeviceRole.WindowsDesktop, reloaded.CurrentSettings.DeviceRole);
    }

    [TestMethod]
    public void TursoSettingsService_RecordUsage_UpdatesQuotaSummary()
    {
        var service = new TursoSettingsService(_tempConfigPath);
        service.RecordUsage(rowsRead: 500, rowsWritten: 120);

        var quota = service.GetQuotaSummary();
        Assert.AreEqual(500, quota.RowsReadThisMonth);
        Assert.AreEqual(120, quota.RowsWrittenThisMonth);
        Assert.AreEqual(500, quota.RowsReadToday);
        Assert.AreEqual(120, quota.RowsWrittenToday);
        Assert.AreEqual(0.0001, quota.PercentageReadsUsed, 1e-12, "500 de 500 millones");
        Assert.AreEqual(0.0012, quota.PercentageWritesUsed, 1e-12, "120 de 10 millones");
    }

    [TestMethod]
    public void TursoQuotaSummary_UsesCurrentFreeTierLimits()
    {
        // https://turso.tech/pricing (consultado el 2026-10-09)
        Assert.AreEqual(500_000_000L, TursoQuotaSummary.FreeTierMonthlyRowsReadLimit);
        Assert.AreEqual(10_000_000L, TursoQuotaSummary.FreeTierMonthlyRowsWriteLimit);
        Assert.AreEqual(5_000.0, TursoQuotaSummary.FreeTierStorageMegabytes);
    }

    [TestMethod]
    public void TursoSettingsService_DailyCounters_ResetAtMidnight_AndBrakeSurvivesRestart()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 15, 0, 0, TimeSpan.Zero));
        var service = new TursoSettingsService(_tempConfigPath, clock);
        service.RecordUsage(1_000, 10);
        service.RecordFullPull();
        service.TripConsumptionBrake();

        var reloaded = new TursoSettingsService(_tempConfigPath, clock);
        Assert.AreEqual(1_000, reloaded.GetQuotaSummary().RowsReadToday);
        Assert.AreEqual(1, reloaded.GetQuotaSummary().FullPullsToday);
        Assert.IsNotNull(reloaded.CurrentSettings.ConsumptionBrakeTrippedUtc, "El freno sigue activo al reabrir el programa");

        clock.Advance(TimeSpan.FromHours(10)); // pasa la medianoche
        var nextDay = reloaded.GetQuotaSummary();
        Assert.AreEqual(0, nextDay.RowsReadToday);
        Assert.AreEqual(0, nextDay.FullPullsToday);
        Assert.AreEqual(1_000, nextDay.RowsReadThisMonth, "El mes sigue sumando");
        Assert.IsNotNull(reloaded.CurrentSettings.ConsumptionBrakeTrippedUtc, "Cambiar de día no quita el freno: solo «Reanudar»");

        reloaded.RecordUsage(50, 0);
        reloaded.ResumeAfterConsumptionBrake();
        Assert.IsNull(reloaded.CurrentSettings.ConsumptionBrakeTrippedUtc);
        Assert.AreEqual(0, reloaded.GetQuotaSummary().RowsReadToday, "Reanudar vuelve a contar el día desde cero");
    }

    [TestMethod]
    public void TursoSettingsService_PcRoleIsAlwaysWindowsDesktop()
    {
        // Antes había un selector de "Uso": una PC configurada como tablet se corrige al abrir
        var service = new TursoSettingsService(_tempConfigPath);
        service.SaveSettings(new TursoSettings { DatabaseUrl = "https://clinica.turso.io", DeviceRole = DeviceRole.AndroidTablet });

        var reloaded = new TursoSettingsService(_tempConfigPath);
        Assert.AreEqual(DeviceRole.WindowsDesktop, reloaded.CurrentSettings.DeviceRole);
    }

    [TestMethod]
    public void NubeYDispositivos_NoLongerHandsOutTheTokenInPlainText()
    {
        // Paso 8.2: se retiró el paquete de vinculación en claro (token completo, sin vencimiento, por el portapapeles)
        Assert.IsNull(typeof(TursoSettingsService).GetMethod("GenerateAndroidPairingPayload"));
        foreach (var member in new[] { "PairingJsonPayload", "IsPairingModalOpen", "OpenPairingModalCommand", "CopyPairingPayloadCommand", "SelectedRole" })
        {
            Assert.IsNull(typeof(TPVC.ViewModels.TursoSyncViewModel).GetProperty(member), $"{member} ya no debe existir");
        }

        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "TPVC.sln"))) root = Path.GetDirectoryName(root);
        Assert.IsNotNull(root, "No se encontró la carpeta del proyecto");
        var viewModel = File.ReadAllText(Path.Combine(root, "ViewModels", "TursoSyncViewModel.cs"));
        Assert.IsFalse(viewModel.Contains("Clipboard.SetText", StringComparison.Ordinal), "Nube y dispositivos no copia nada al portapapeles");
    }

    [TestMethod]
    public void DevicePermissions_EnforcesStrictRoleBoundaries()
    {
        // 1. WindowsDesktop: Control total
        Assert.IsTrue(DevicePermissions.CanManageClinicalRecords(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanPrescribeMedications(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanProcessSales(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanManageInventory(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanViewDashboard(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanViewCashCut(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanConfigureSystem(DeviceRole.WindowsDesktop));

        // 2. AndroidTablet: Uso clínico y cobro, no administración profunda
        Assert.IsTrue(DevicePermissions.CanManageClinicalRecords(DeviceRole.AndroidTablet));
        Assert.IsTrue(DevicePermissions.CanPrescribeMedications(DeviceRole.AndroidTablet));
        Assert.IsTrue(DevicePermissions.CanProcessSales(DeviceRole.AndroidTablet));
        Assert.IsFalse(DevicePermissions.CanManageInventory(DeviceRole.AndroidTablet));
        Assert.IsTrue(DevicePermissions.CanViewDashboard(DeviceRole.AndroidTablet));
        Assert.IsFalse(DevicePermissions.CanViewCashCut(DeviceRole.AndroidTablet));
        Assert.IsFalse(DevicePermissions.CanConfigureSystem(DeviceRole.AndroidTablet));

        // 3. AndroidMobileAdmin: Solo funciones limitadas para administrar (Dashboard, Cortes, Stock)
        Assert.IsFalse(DevicePermissions.CanManageClinicalRecords(DeviceRole.AndroidMobileAdmin));
        Assert.IsFalse(DevicePermissions.CanPrescribeMedications(DeviceRole.AndroidMobileAdmin));
        Assert.IsFalse(DevicePermissions.CanProcessSales(DeviceRole.AndroidMobileAdmin));
        Assert.IsFalse(DevicePermissions.CanManageInventory(DeviceRole.AndroidMobileAdmin));
        Assert.IsTrue(DevicePermissions.CanViewDashboard(DeviceRole.AndroidMobileAdmin));
        Assert.IsTrue(DevicePermissions.CanViewCashCut(DeviceRole.AndroidMobileAdmin));
        Assert.IsTrue(DevicePermissions.CanViewStockAlerts(DeviceRole.AndroidMobileAdmin));
        Assert.IsFalse(DevicePermissions.CanConfigureSystem(DeviceRole.AndroidMobileAdmin));
    }

    [TestMethod]
    public async Task TursoClient_QueryAsync_ParsesHranaResultsProperly()
    {
        var mockResponse = @"{
            ""results"": [
                {
                    ""type"": ""ok"",
                    ""response"": {
                        ""type"": ""execute"",
                        ""result"": {
                            ""cols"": [
                                { ""name"": ""Id"" },
                                { ""name"": ""Name"" },
                                { ""name"": ""Price"" }
                            ],
                            ""rows"": [
                                [
                                    { ""type"": ""integer"", ""value"": 10 },
                                    { ""type"": ""text"", ""value"": ""Paracetamol 500mg"" },
                                    { ""type"": ""float"", ""value"": 45.5 }
                                ]
                            ],
                            ""affected_row_count"": 0
                        }
                    }
                }
            ]
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, mockResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoClient("https://dummy.turso.io", "token", httpClient);

        var rows = await client.QueryAsync("SELECT * FROM Products;");
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(10L, rows[0]["Id"]);
        Assert.AreEqual("Paracetamol 500mg", rows[0]["Name"]);
        Assert.AreEqual(45.5, rows[0]["Price"]);
    }

    [TestMethod]
    public async Task TursoClient_QueryAsync_ThrowsOnHranaError()
    {
        var mockErrorResponse = @"{
            ""results"": [
                {
                    ""type"": ""error"",
                    ""error"": {
                        ""message"": ""no such table: UnknownTable"",
                        ""code"": ""SQLITE_ERROR""
                    }
                }
            ]
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, mockErrorResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoClient("https://dummy.turso.io", "token", httpClient);

        await Assert.ThrowsExactlyAsync<TursoException>(async () =>
        {
            await client.QueryAsync("SELECT * FROM UnknownTable;");
        });
    }

    [TestMethod]
    public void TursoSettingsService_NeverPersistsPlaintextTokenToDisk()
    {
        var service = new TursoSettingsService(_tempConfigPath);
        var secret = "super-secret-turso-token-98765";
        service.SaveSettings(new TursoSettings
        {
            DatabaseUrl = "https://clinica.turso.io",
            AuthToken = secret
        });

        var fileContent = File.ReadAllText(_tempConfigPath);
        Assert.IsFalse(fileContent.Contains(secret), "El token plano nunca debe aparecer en el archivo JSON de configuración.");
        Assert.IsTrue(fileContent.Contains("ProtectedAuthToken"), "El JSON debe contener el token cifrado en disco.");
    }

    [TestMethod]
    public async Task TursoClient_QueryAsync_ParsesStringNumbersSafely()
    {
        var mockResponse = @"{
            ""results"": [
                {
                    ""type"": ""ok"",
                    ""response"": {
                        ""type"": ""execute"",
                        ""result"": {
                            ""cols"": [
                                { ""name"": ""Count"" },
                                { ""name"": ""Rate"" }
                            ],
                            ""rows"": [
                                [
                                    { ""type"": ""integer"", ""value"": ""42"" },
                                    { ""type"": ""float"", ""value"": ""99.95"" }
                                ]
                            ],
                            ""affected_row_count"": 0
                        }
                    }
                }
            ]
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, mockResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoClient("https://dummy.turso.io", "token", httpClient);

        var rows = await client.QueryAsync("SELECT 42 AS Count, 99.95 AS Rate;");
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(42L, rows[0]["Count"]);
        Assert.AreEqual(99.95, rows[0]["Rate"]);
    }

    [TestMethod]
    public async Task TursoClient_TestConnectionAsync_ReturnsInformativeErrorOn401()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"JWT validation failed\"}");
        using var httpClient = new HttpClient(handler);
        using var client = new TursoClient("https://dummy.turso.io", "expired-token", httpClient);

        var (success, latency, error) = await client.TestConnectionAsync();
        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.IsTrue(error.Contains("HTTP 401") || error.Contains("autenticación"), "El error debe ser informativo sobre el token.");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
