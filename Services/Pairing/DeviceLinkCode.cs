using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;

namespace TPVC.Services.Pairing;

/// <summary>Lo que lleva el código de vinculación: la URL de la nube, el token y el nombre del consultorio.</summary>
public sealed record DeviceLinkData(string Url, string Token, string ClinicName);

/// <summary>
/// Paso 8.14 (decisión del médico del 2026-10-11): el texto del QR que muestra la PC para que la tablet o el celular se
/// vinculen sin teclear el token. Es <c>tpvc-link:1:</c> + base64url sin relleno de un JSON compacto
/// <c>{"u":url,"t":token,"n":consultorio}</c> (UTF-8, acentos sin escapar). Lleva el token completo: quien lo fotografía
/// tiene acceso a toda la nube, igual que quien copia el token del panel de Turso. Vectores en contracts/golden/link-qr.json.
/// </summary>
public static class DeviceLinkCode
{
    public const string Prefix = "tpvc-link:";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Encode(DeviceLinkData data)
    {
        if (string.IsNullOrWhiteSpace(data.Url) || string.IsNullOrWhiteSpace(data.Token))
            throw new ArgumentException("Faltan la URL o el token de la nube.", nameof(data));
        var json = new JsonObject
        {
            ["u"] = data.Url.Trim(),
            ["t"] = data.Token.Trim(),
            ["n"] = data.ClinicName.Trim()
        }.ToJsonString(Compact);
        return $"{Prefix}{Version}:{Base64Url(Encoding.UTF8.GetBytes(json))}";
    }

    /// <summary>Lee un código; null si no es uno de TPVC, si es de otra versión o si está dañado.</summary>
    public static DeviceLinkData? TryDecode(string? text)
    {
        var clean = text?.Trim();
        var head = $"{Prefix}{Version}:";
        if (clean == null || !clean.StartsWith(head, StringComparison.Ordinal)) return null;
        try
        {
            var payload = clean[head.Length..].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var url = node?["u"]?.GetValue<string>();
            var token = node?["t"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return null;
            return new DeviceLinkData(url, token, node?["n"]?.GetValue<string>() ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// El QR como PNG (blanco y negro, con su margen). Corrección de errores L: en una pantalla no se daña, y así un token
    /// de Turso (~350 caracteres) cabe en ~100 módulos, que en los 300 px de la pantalla son 3 px cada uno.
    /// <paramref name="pixelsPerModule"/> decide el tamaño del PNG.
    /// </summary>
    public static byte[] QrPng(string code, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.L);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>Módulos por lado del QR, con su margen (para revisar que se lea desde la pantalla).</summary>
    public static int ModulesPerSide(string code)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.L);
        return data.ModuleMatrix.Count;
    }
}
