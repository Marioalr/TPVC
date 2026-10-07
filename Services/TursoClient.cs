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

/// <summary>Formatos de texto compartidos por el protocolo de sincronización (Windows y Android).</summary>
public static class TursoSyncFormats
{
    /// <summary>Fechas y marcas de tiempo: orden lexicográfico = orden cronológico.</summary>
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";
}

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
    /// <summary>
    /// Conexiones HTTP compartidas por toda la app: crear un HttpClient por sincronización
    /// (cada pocos segundos con el auto-sync) dejaba sockets abiertos en TIME_WAIT.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _pipelineUrl;
    private readonly string _token;

    public string DatabaseUrl { get; }

    public TursoClient(string databaseUrl, string token, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl)) throw new ArgumentException("La URL de la base de datos de Turso no puede estar vacía.", nameof(databaseUrl));
        _token = CleanToken(token);

        DatabaseUrl = databaseUrl.Trim().Trim('"', '\'', '`');
        _pipelineUrl = NormalizePipelineUrl(DatabaseUrl);

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = SharedHttpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>
    /// Limpia y normaliza un token de Turso, eliminando comillas accidentales, prefijos Bearer y saltos de línea.
    /// </summary>
    public static string CleanToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;

        var clean = token.Trim().Trim('"', '\'', '`');
        clean = clean.Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();

        if (clean.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("Bearer".Length).Trim().Trim('"', '\'', '`');
        }

        return clean;
    }

    /// <summary>
    /// Normaliza la URL de Turso a un endpoint válido del protocolo Hrana (/v2/pipeline).
    /// Soporta esquemas libsql://, https://, turso:// y hosts planos con validación de host.
    /// </summary>
    public static string NormalizePipelineUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("La URL de la base de datos de Turso no puede estar vacía.", nameof(url));
        }

        var clean = url.Trim().Trim('"', '\'', '`');

        // Quitar parámetros de consulta si se pegó una cadena de conexión con argumentos (?authToken=... etc.)
        var qIdx = clean.IndexOf('?');
        if (qIdx >= 0)
        {
            clean = clean.Substring(0, qIdx);
        }

        if (clean.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase))
        {
            clean = "https://" + clean.Substring("libsql://".Length);
        }
        else if (clean.StartsWith("turso://", StringComparison.OrdinalIgnoreCase))
        {
            clean = "https://" + clean.Substring("turso://".Length);
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

        if (Uri.TryCreate(clean, UriKind.Absolute, out var parsedUri))
        {
            var host = parsedUri.Host;
            if (!host.Contains('.') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1")
            {
                throw new ArgumentException(
                    $"La dirección '{host}' parece ser solo el nombre de la base de datos. " +
                    "Turso requiere la URL completa del host (por ejemplo: libsql://{host}-usuario.turso.io o https://{host}.turso.io).");
            }
        }

        return clean;
    }

    /// <summary>
    /// Comprueba la conexión con Turso ejecutando una consulta trivial (SELECT 1) y mide la latencia.
    /// </summary>
    public async Task<(bool Success, long LatencyMs, string? Error)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return (false, 0, "Token de autenticación no proporcionado. Ingrese el token de la base de datos generado en Turso.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var rows = await QueryAsync("SELECT 1 AS probe;", Array.Empty<object?>(), ct);
            sw.Stop();
            if (rows.Count > 0)
            {
                return (true, sw.ElapsedMilliseconds, null);
            }
            return (false, sw.ElapsedMilliseconds, "Turso respondió pero no devolvió filas de prueba.");
        }
        catch (TursoException tex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, tex.Message);
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
        var results = await QueryBatchAsync(new[] { (sql, args) }, ct);
        return results.Count > 0 ? results[0] : new List<Dictionary<string, object?>>();
    }

    /// <summary>
    /// Ejecuta varias consultas en un solo viaje HTTP y devuelve las filas de cada una, en orden.
    /// </summary>
    public async Task<List<List<Dictionary<string, object?>>>> QueryBatchAsync(IEnumerable<(string Sql, object?[]? Args)> statements, CancellationToken ct = default)
    {
        var requestObj = CreatePipelineRequest(statements);
        var root = await SendPipelineAsync(requestObj, ct);

        var all = new List<List<Dictionary<string, object?>>>();
        var results = root["results"]?.AsArray();
        if (results == null) return all;

        foreach (var result in results)
        {
            // La respuesta del "close" final no trae filas
            if (result?["response"]?["type"]?.GetValue<string>() == "close") continue;
            CheckResultError(result);
            all.Add(ParseRows(result?["response"]?["result"]));
        }
        return all;
    }

    private static List<Dictionary<string, object?>> ParseRows(JsonNode? resultObj)
    {
        var list = new List<Dictionary<string, object?>>();
        var cols = resultObj?["cols"]?.AsArray();
        var rows = resultObj?["rows"]?.AsArray();
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

        return ReadAffectedRows(firstResult);
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
            totalAffected += ReadAffectedRows(res);
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

    public static JsonObject FormatArg(object? value)
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
            return new JsonObject { ["type"] = "text", ["value"] = g.ToString("D") };
        }

        if (value is DateTime dt)
        {
            // Sin conversión de zona: el llamador decide si el valor ya es UTC (UpdatedAtUtc)
            // o una fecha local de negocio. Ver TursoSyncService.FormatDate.
            return new JsonObject { ["type"] = "text", ["value"] = dt.ToString(TursoSyncFormats.DateTimeFormat, CultureInfo.InvariantCulture) };
        }

        // Hrana: los enteros viajan como CADENA ("value": "42") para no perder precisión en JSON.
        // Enviarlos como número hace que Turso rechace todo el pipeline con HTTP 400 Bad Request.
        if (value is bool b)
        {
            return new JsonObject { ["type"] = "integer", ["value"] = b ? "1" : "0" };
        }

        if (value is int or long or short or byte or sbyte or uint or ushort)
        {
            return new JsonObject { ["type"] = "integer", ["value"] = Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
        }

        if (value is Enum e)
        {
            return new JsonObject { ["type"] = "integer", ["value"] = Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
        }

        if (value is float or double)
        {
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return new JsonObject { ["type"] = "null" };
            return new JsonObject { ["type"] = "float", ["value"] = d };
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
                "integer" => ParseLongSafe(valNode),
                "float" => ParseDoubleSafe(valNode),
                "text" => valNode.ToString(),
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

    private static long ParseLongSafe(JsonNode node)
    {
        if (node is JsonValue val && val.TryGetValue<long>(out var l)) return l;
        if (long.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0L;
    }

    private static double ParseDoubleSafe(JsonNode node)
    {
        if (node is JsonValue val && val.TryGetValue<double>(out var d)) return d;
        if (double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0.0;
    }

    private static int ReadAffectedRows(JsonNode? result)
    {
        var node = result?["response"]?["result"]?["affected_row_count"];
        return node == null ? 0 : (int)ParseLongSafe(node);
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

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!req.Headers.UserAgent.Any())
        {
            req.Headers.UserAgent.ParseAdd($"TPVC-Desktop/{typeof(TursoClient).Assembly.GetName().Version?.ToString(3) ?? "1"}");
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
            throw new TursoException($"No se pudo conectar con el servidor de Turso ({_pipelineUrl}): {ex.Message}. Verifique la conexión a internet o la URL.", "NETWORK_ERROR", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TursoException($"Tiempo de espera agotado al conectar con Turso ({_pipelineUrl}).", "TIMEOUT", ex);
        }

        var respContent = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            string diag = code switch
            {
                401 => $"Error de autenticación (HTTP 401 Unauthorized): El token no es válido o ha expirado. Verifique que sea un token de base de datos generado con 'turso db tokens create' o en Turso Dashboard -> Connect -> Generate token.",
                403 => $"Acceso denegado (HTTP 403 Forbidden): El token no tiene permisos suficientes para esta base de datos.",
                404 => $"Base de datos o endpoint no encontrado (HTTP 404 Not Found): Verifique que la URL ingresada corresponda a una base de datos existente en Turso.",
                400 => $"Turso rechazó la solicitud (HTTP 400 Bad Request): {Truncate(respContent, 400)}",
                _ => $"Turso respondió con error HTTP {code} ({resp.ReasonPhrase}): {Truncate(respContent, 400)}"
            };

            throw new TursoException(diag, $"HTTP_{code}");
        }

        var node = JsonNode.Parse(respContent);
        if (node == null)
        {
            throw new TursoException("La respuesta de Turso no contiene un JSON válido.");
        }

        return node;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
