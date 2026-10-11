using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Services.Pairing;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.14: contracts/golden/link-qr.json, el texto del QR con que la tablet y el celular reciben la URL y el token
/// (decisión del médico del 2026-10-11). Kotlin lo genera y lo lee igual.
/// </summary>
public partial class GoldenVectorTests
{
    [TestMethod]
    public void LinkQr_MatchesTheVectors()
    {
        const string url = "libsql://consultorio-ejemplo.aws-us-east-2.turso.io";
        const string token = "eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJhIjoicnciLCJpYXQiOjE3NjAwMDAwMDB9.c2lnbmF0dXJhLWRlLWVqZW1wbG8";
        var cases = new JsonArray();

        foreach (var (name, clinic) in new[] { ("con acentos y eñe", "Consultorio Médico Peña"), ("sin nombre de consultorio", "") })
        {
            var code = DeviceLinkCode.Encode(new DeviceLinkData(url, token, clinic));
            Assert.AreEqual(new DeviceLinkData(url, token, clinic), DeviceLinkCode.TryDecode(code), "Se lee lo mismo que se escribió");
            cases.Add(Case($"generar · {name}", new JsonObject { ["u"] = url, ["t"] = token, ["n"] = clinic }, code));
        }

        // Lo que no es un código de TPVC (o es de otra versión) no se lee: el equipo pide pegar la URL y el token a mano
        foreach (var (name, text) in new[]
                 {
                     ("otro prefijo", "https://consultorio-ejemplo.turso.io"),
                     ("otra versión", "tpvc-link:2:eyJ1IjoieCIsInQiOiJ5In0"),
                     ("no es base64", "tpvc-link:1:%%%"),
                     ("sin token", "tpvc-link:1:" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"u\":\"libsql://x.turso.io\",\"n\":\"\"}")).TrimEnd('=')),
                     ("vacío", "")
                 })
        {
            Assert.IsNull(DeviceLinkCode.TryDecode(text), name);
            cases.Add(Case($"leer · {name}", new JsonObject { ["text"] = text }, null));
        }

        var accepted = DeviceLinkCode.TryDecode("  " + DeviceLinkCode.Encode(new DeviceLinkData(url, token, "Consultorio")) + "\n");
        Assert.IsNotNull(accepted, "Con espacios o salto de línea alrededor (pegado de un correo) también se lee");

        // Un token de Turso real ronda los 350 caracteres: en los 300 px de la pantalla, cada módulo debe medir 3 px o más
        var realistic = DeviceLinkCode.Encode(new DeviceLinkData(url, new string('a', 120) + "." + new string('b', 160) + "." + new string('c', 86), "Consultorio Médico Peña"));
        Assert.IsLessThanOrEqualTo(100, DeviceLinkCode.ModulesPerSide(realistic), "El QR se lee desde la pantalla de la PC");

        CheckOrWrite("contracts/golden/link-qr.json", VectorFile("link-qr", cases,
            "Texto del QR: 'tpvc-link:1:' + base64url sin relleno (RFC 4648 §5: '-' y '_', sin '=') del JSON compacto {\"u\":url,\"t\":token,\"n\":consultorio} en UTF-8, con las claves en ese orden y los acentos sin escapar.",
            "Al leer: se quitan los espacios de los extremos; si el prefijo o la versión no coinciden, el base64 o el JSON no se pueden leer, o faltan 'u' o 't', 'expected' es null y el equipo pide pegar la URL y el token a mano. 'n' puede faltar o ir vacío.",
            "El QR lleva el token completo: la PC lo muestra solo a pedido y lo cierra a los 2 minutos; nunca va al portapapeles ni al registro.",
            "Después de leerlo, el equipo escribe su latido con Platform 'Android' y el rol que pide, muestra los últimos 6 caracteres de su DeviceId en mayúsculas y espera su fila de DeviceRegistry (regla 'Equipos' de sync/schema.json)."));
    }
}
