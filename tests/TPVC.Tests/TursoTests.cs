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
        Assert.IsTrue(quota.PercentageWritesUsed < 1.0); // Extremadamente bajo respecto a 25M
        Assert.IsTrue(quota.PercentageReadsUsed < 1.0);  // Extremadamente bajo respecto a 1B
    }

    [TestMethod]
    public void TursoSettingsService_GenerateAndroidPairingPayload_GeneratesValidJson()
    {
        var service = new TursoSettingsService(_tempConfigPath);
        service.CurrentSettings.DatabaseUrl = "https://clinica-test.turso.io";
        service.CurrentSettings.AuthToken = "secret-token-abc";

        // 1. Tablet Android
        var tabletJson = service.GenerateAndroidPairingPayload(DeviceRole.AndroidTablet, "Clínica San José", "Dr. Martínez");
        Assert.IsNotNull(tabletJson);
        using (var doc = JsonDocument.Parse(tabletJson))
        {
            var root = doc.RootElement;
            Assert.AreEqual("https://clinica-test.turso.io", root.GetProperty("DatabaseUrl").GetString());
            Assert.AreEqual("secret-token-abc", root.GetProperty("AuthToken").GetString());
            Assert.AreEqual("AndroidTablet", root.GetProperty("AssignedRole").GetString());
        }

        // 2. Celular Android Admin
        var mobileJson = service.GenerateAndroidPairingPayload(DeviceRole.AndroidMobileAdmin, "Clínica San José", "Dr. Martínez");
        Assert.IsNotNull(mobileJson);
        using (var doc = JsonDocument.Parse(mobileJson))
        {
            var root = doc.RootElement;
            Assert.AreEqual("AndroidMobileAdmin", root.GetProperty("AssignedRole").GetString());
        }
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
