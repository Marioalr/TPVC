using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TPVC.Services;

public class TursoException : Exception
{
    public string? ErrorCode { get; }

    public TursoException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Cliente HTTP ultraligero y robusto para interactuar con bases de datos Turso (libSQL)
/// a través del protocolo oficial Hrana /v2/pipeline.
/// Optimizado para el Free Tier (mínimo consumo de red y latencia ultrabaja).
/// </summary>
public class TursoClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _pipelineUrl;
    private readonly string _token;

    public string DatabaseUrl { get; }

    public TursoClient(string databaseUrl, string token, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl)) throw new ArgumentException("Database URL cannot be empty", nameof(databaseUrl));
        _token = token?.Trim() ?? string.Empty;

        DatabaseUrl = databaseUrl.Trim();
        _pipelineUrl = NormalizePipelineUrl(DatabaseUrl);

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _ownsHttpClient = true;
        }
    }

    public static string NormalizePipelineUrl(string url)
    {
        var clean = url.Trim();
        if (clean.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase))
        {
            clean = "https://" + clean.Substring("libsql://".Length);
        }
        else if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            clean = "https://" + clean;
        }

        clean = clean.TrimEnd('/');
        if (!clean.EndsWith("/v2/pipeline", StringComparison.OrdinalIgnoreCase))
        {
            clean += "/v2/pipeline";
        }
        return clean;
    }

    /// <summary>
    /// Comprueba la conexión con Turso ejecutando una consulta trivial (SELECT 1) y mide la latencia.
    /// </summary>
    public async Task<(bool Success, long LatencyMs, string? Error)> TestConnectionAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var rows = await QueryAsync("SELECT 1 AS probe;", Array.Empty<object?>(), ct);
            sw.Stop();
            if (rows.Count > 0)
            {
                return (true, sw.ElapsedMilliseconds, null);
            }
            return (false, sw.ElapsedMilliseconds, "Turso no devolvió resultados.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Ejecuta una consulta SELECT y devuelve los registros como una lista de diccionarios (nombreColumna -> valor).
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, object?[]? args = null, CancellationToken ct = default)
    {
        var requestObj = CreatePipelineRequest(new[] { (sql, args) });
        var root = await SendPipelineAsync(requestObj, ct);

        var list = new List<Dictionary<string, object?>>();
        var results = root["results"]?.AsArray();
        if (results == null || results.Count == 0) return list;

        var firstResult = results[0];
        CheckResultError(firstResult);

        var resultObj = firstResult?["response"]?["result"];
        if (resultObj == null) return list;

        var cols = resultObj["cols"]?.AsArray();
        var rows = resultObj["rows"]?.AsArray();
        if (cols == null || rows == null) return list;

        var colNames = new List<string>();
        foreach (var c in cols)
        {
            colNames.Add(c?["name"]?.GetValue<string>() ?? $"col_{colNames.Count}");
        }

        foreach (var r in rows)
        {
            var rowArray = r?.AsArray();
            if (rowArray == null) continue;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < colNames.Count && i < rowArray.Count; i++)
            {
                dict[colNames[i]] = ExtractValue(rowArray[i]);
            }
            list.Add(dict);
        }

        return list;
    }

    /// <summary>
    /// Ejecuta una sentencia DML (INSERT, UPDATE, DELETE) y devuelve el número de filas afectadas.
    /// </summary>
    public async Task<int> ExecuteAsync(string sql, object?[]? args = null, CancellationToken ct = default)
    {
        var requestObj = CreatePipelineRequest(new[] { (sql, args) });
        var root = await SendPipelineAsync(requestObj, ct);

        var results = root["results"]?.AsArray();
        if (results == null || results.Count == 0) return 0;

        var firstResult = results[0];
        CheckResultError(firstResult);

        var affected = firstResult?["response"]?["result"]?["affected_row_count"]?.GetValue<int>() ?? 0;
        return affected;
    }

    /// <summary>
    /// Ejecuta múltiples sentencias SQL en un solo pipeline HTTP transaccional.
    /// </summary>
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string Sql, object?[]? Args)> statements, CancellationToken ct = default)
    {
        var requestObj = CreatePipelineRequest(statements);
        var root = await SendPipelineAsync(requestObj, ct);

        var results = root["results"]?.AsArray();
        if (results == null) return 0;

        int totalAffected = 0;
        foreach (var res in results)
        {
            CheckResultError(res);
            totalAffected += res?["response"]?["result"]?["affected_row_count"]?.GetValue<int>() ?? 0;
        }

        return totalAffected;
    }

    public async Task ExecuteScriptAsync(string multiStatementSql, CancellationToken ct = default)
    {
        var rawStatements = multiStatementSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = new List<(string Sql, object?[]? Args)>();
        foreach (var stmt in rawStatements)
        {
            if (!string.IsNullOrWhiteSpace(stmt))
            {
                items.Add((stmt + ";", null));
            }
        }

        if (items.Count > 0)
        {
            await ExecuteBatchAsync(items, ct);
        }
    }

    private static JsonObject CreatePipelineRequest(IEnumerable<(string Sql, object?[]? Args)> statements)
    {
        var requestsArray = new JsonArray();
        foreach (var (sql, args) in statements)
        {
            var stmtObj = new JsonObject
            {
                ["sql"] = sql
            };

            if (args != null && args.Length > 0)
            {
                var argsArray = new JsonArray();
                foreach (var arg in args)
                {
                    argsArray.Add(FormatArg(arg));
                }
                stmtObj["args"] = argsArray;
            }

            var req = new JsonObject
            {
                ["type"] = "execute",
                ["stmt"] = stmtObj
            };
            requestsArray.Add(req);
        }

        requestsArray.Add(new JsonObject { ["type"] = "close" });

        return new JsonObject
        {
            ["requests"] = requestsArray
        };
    }

    private static JsonObject FormatArg(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return new JsonObject { ["type"] = "null" };
        }

        if (value is string s)
        {
            return new JsonObject { ["type"] = "text", ["value"] = s };
        }

        if (value is Guid g)
        {
            return new JsonObject { ["type"] = "text", ["value"] = g.ToString() };
        }

        if (value is DateTime dt)
        {
            return new JsonObject { ["type"] = "text", ["value"] = dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) };
        }

        if (value is bool b)
        {
            return new JsonObject { ["type"] = "integer", ["value"] = b ? 1 : 0 };
        }

        if (value is int or long or short or byte)
        {
            return new JsonObject { ["type"] = "integer", ["value"] = Convert.ToInt64(value) };
        }

        if (value is float or double)
        {
            return new JsonObject { ["type"] = "float", ["value"] = Convert.ToDouble(value) };
        }

        if (value is decimal dec)
        {
            return new JsonObject { ["type"] = "text", ["value"] = dec.ToString("G", CultureInfo.InvariantCulture) };
        }

        if (value is byte[] bytes)
        {
            return new JsonObject { ["type"] = "blob", ["base64"] = Convert.ToBase64String(bytes) };
        }

        return new JsonObject { ["type"] = "text", ["value"] = value.ToString() };
    }

    private static object? ExtractValue(JsonNode? node)
    {
        if (node == null) return null;

        // Si es un objeto de valor Hrana: {"type": "...", "value": ...}
        if (node is JsonObject obj && obj.ContainsKey("type"))
        {
            var type = obj["type"]?.GetValue<string>();
            if (string.Equals(type, "null", StringComparison.OrdinalIgnoreCase)) return null;

            var valNode = obj["value"];
            if (valNode == null) return null;

            return type switch
            {
                "integer" => valNode.GetValue<long>(),
                "float" => valNode.GetValue<double>(),
                "text" => valNode.GetValue<string>(),
                "blob" => obj["base64"]?.GetValue<string>(),
                _ => valNode.ToString()
            };
        }

        // Si es un valor plano directo
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            return val.GetValue<string>();
        }

        return node.ToString();
    }

    private static void CheckResultError(JsonNode? result)
    {
        if (result == null) return;
        var type = result["type"]?.GetValue<string>();
        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            var msg = result["error"]?["message"]?.GetValue<string>() ?? "Error desconocido en Turso";
            var code = result["error"]?["code"]?.GetValue<string>();
            throw new TursoException(msg, code);
        }
    }

    private async Task<JsonNode> SendPipelineAsync(JsonObject requestBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _pipelineUrl);
        if (!string.IsNullOrWhiteSpace(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        var json = requestBody.ToJsonString();
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TursoException($"No se pudo conectar con el servidor de Turso ({_pipelineUrl}): {ex.Message}", "NETWORK_ERROR", ex);
        }

        var respContent = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new TursoException($"Turso respondió con error HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}): {respContent}", $"HTTP_{(int)resp.StatusCode}");
        }

        var node = JsonNode.Parse(respContent);
        if (node == null)
        {
            throw new TursoException("La respuesta de Turso no contiene un JSON válido.");
        }

        return node;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
