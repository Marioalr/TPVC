using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.13, kit de conformidad de lectura: contracts/hrana/*.jsonl son los intercambios con el servidor simulado de un
/// equipo que lee la nube (el motor de la PC hace lo que tendrá que hacer Kotlin): la primera descarga, la incremental,
/// los empates en la llave, el cambio de generación, la cuota agotada y el token rechazado. Lo que cambia en cada
/// corrida (horas, identificadores al azar, la versión) va como marcador; los SyncId de los datos de ejemplo son fijos.
/// Se regeneran con TPVC_WRITE_GOLDEN=1, como los demás vectores.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static readonly DateTime FixtureDate = new(2026, 1, 15, 10, 30, 0);
    private static readonly DateTime FixtureBirth = new(1985, 3, 2);
    private static readonly DateTime FixtureAppointment = new(2026, 1, 22);

    private static Guid FixtureId(int n) => new($"10000000-0000-4000-8000-{n:D12}");

    /// <summary>Lo que sube la PC antes de grabar: una categoría, un producto, un paciente con una consulta y una cita.</summary>
    private static async Task SeedReadFixtureAsync(Device pc)
    {
        var category = new Category { SyncId = FixtureId(1), Name = "Analgésicos", Icon = "Pill" };
        await SaveFixtureAsync(pc, category);
        await SaveFixtureAsync(pc, new Product
        {
            SyncId = FixtureId(2), Code = "PARA-500", Name = "Paracetamol 500 mg", CategoryId = category.Id, Price = 35.50m,
            UnitCost = 18m, CurrentStock = 12, CreatedAt = FixtureDate, UpdatedAt = FixtureDate
        });
        var patient = new PatientRecord
        {
            SyncId = FixtureId(3), RecordNumber = "EXP-2026-001", FullName = "Ana Pérez Núñez", Gender = "Femenino", BirthDate = FixtureBirth,
            Phone = "5512345678", Allergies = "Penicilina", LastVisit = FixtureDate, CreatedAt = FixtureDate
        };
        await SaveFixtureAsync(pc, patient);
        await SaveFixtureAsync(pc, new PatientConsultation
        {
            SyncId = FixtureId(4), PatientRecordId = patient.Id, Folio = "CNS-20260115-001", Date = FixtureDate, StartedAt = FixtureDate,
            EndedAt = FixtureDate.AddMinutes(20), ChiefComplaint = "Dolor de garganta", Diagnosis = "Faringitis aguda (J02.9)"
        });
        await SaveFixtureAsync(pc, new PatientAppointment
        {
            SyncId = FixtureId(5), PatientRecordId = patient.Id, PatientName = patient.FullName, PatientRecordNumber = patient.RecordNumber,
            Date = FixtureAppointment, Time = "09:30", Reason = "Revisión", CreatedAt = FixtureDate
        });
    }

    /// <summary>Una fila por guardado y con un respiro: cada una con su propia marca (las grabaciones no dependen de empates).</summary>
    private static async Task SaveFixtureAsync<T>(Device device, T entity) where T : class
    {
        using var db = device.Factory.CreateDbContext();
        db.Add(entity);
        await db.SaveChangesAsync();
        await Task.Delay(3);
    }

    // ================================================================== marcadores

    private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d{1,7})?", RegexOptions.Compiled);
    private static readonly Regex GuidText = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex Hex32 = new(@"\b[0-9a-f]{32}\b", RegexOptions.Compiled);

    /// <summary>Reemplaza lo que cambia en cada corrida. Los identificadores al azar llevan número por orden de aparición.</summary>
    private sealed class Placeholders
    {
        private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fixedTimes = new(StringComparer.Ordinal)
        {
            TursoSyncService.FormatDate(FixtureDate), TursoSyncService.FormatDate(FixtureDate.AddMinutes(20)),
            TursoSyncService.FormatDate(FixtureBirth), TursoSyncService.FormatDate(FixtureAppointment)
        };

        public string Apply(string text)
        {
            if (text == TursoSyncService.AppVersionText) return "<appVersion>";
            // El modelo con que la PC se registra en DeviceRegistry es la versión del sistema: cambia de computadora en computadora
            if (text == System.Runtime.InteropServices.RuntimeInformation.OSDescription) return "<osDescription>";
            text = GuidText.Replace(text, m => m.Value.StartsWith("10000000-0000-4000-8000-", StringComparison.OrdinalIgnoreCase)
                ? m.Value.ToLowerInvariant()
                : Number(m.Value, "id"));
            text = Hex32.Replace(text, m => Number(m.Value, "nonce"));
            return Timestamp.Replace(text, m => _fixedTimes.Contains(m.Value) ? m.Value : "<time>");
        }

        private string Number(string value, string kind)
        {
            if (!_ids.TryGetValue(value, out var marker))
            {
                marker = $"<{kind}:{_ids.Count(p => p.Value.StartsWith($"<{kind}:", StringComparison.Ordinal)) + 1}>";
                _ids[value] = marker;
            }
            return marker;
        }
    }

    private static JsonNode? Normalize(JsonNode? node, Placeholders placeholders)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = key == "sql" ? value?.DeepClone() : Normalize(value, placeholders);
                }
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(item => Normalize(item, placeholders)).ToArray());
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(placeholders.Apply(value.GetValue<string>()));
            default:
                return node?.DeepClone();
        }
    }

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>La grabación como JSON Lines: un renglón de encabezado y uno por intercambio.</summary>
    private string Transcript(string scenario, params string[] notes)
    {
        var placeholders = new Placeholders();
        var lines = new List<string>
        {
            new JsonObject
            {
                ["scenario"] = scenario,
                ["contract"] = SyncContract.Revision,
                ["protocol"] = TursoSyncService.ClientProtocol,
                ["generatedBy"] = "tests/TPVC.Tests/TursoSyncEndToEndTests.Hrana.cs (TPVC_WRITE_GOLDEN=1 lo regenera)",
                ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray())
            }.ToJsonString(CompactJson)
        };
        int n = 0;
        foreach (var exchange in _server.Transcript)
        {
            lines.Add(new JsonObject
            {
                ["n"] = ++n,
                ["request"] = Normalize(JsonNode.Parse(exchange.Request), placeholders),
                ["status"] = exchange.Status,
                ["response"] = Normalize(ParseOrText(exchange.Response), placeholders)
            }.ToJsonString(CompactJson));
        }
        return string.Join("\n", lines) + "\n";
    }

    private static JsonNode? ParseOrText(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    private void StartRecording()
    {
        _server.ClearRecording();
        _server.RecordTranscript = true;
    }

    private async Task<(Device Pc, Device Tablet)> CloudWithReadFixtureAsync()
    {
        var pc = CreateDevice("pc", seed: false);
        await SeedReadFixtureAsync(pc);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success, "La PC sube los datos de ejemplo y verifica la nube");
        var tablet = CreateDevice("tablet", seed: false);
        return (pc, tablet);
    }

    // ================================================================== grabaciones

    [TestMethod]
    public async Task Hrana_FirstDownload_AndIncremental_MatchTheTranscripts()
    {
        var (pc, tablet) = await CloudWithReadFixtureAsync();

        StartRecording();
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        GoldenVectorTests.CheckOrWrite("contracts/hrana/first-download.jsonl", Transcript("first-download",
            "Un equipo nuevo se une a una nube que ya tiene identidad (clinicId) y baja todo: «Unirse» lee la identidad, luego la primera sincronización revisa el esquema (_tpvc_meta.schema), escribe su latido y baja tabla por tabla, sin omitir sus propias filas, en el orden de schema.json.",
            "La sonda del esquema (CREATE TABLE IF NOT EXISTS _tpvc_meta y la huella) y el registro en DeviceRegistry son de la PC: Kotlin no corre DDL ni se registra solo (lo aprueba la PC, paso 8.14). Kotlin repite la lectura de la identidad, el latido y las páginas.",
            "Cada página: \"SyncedAtUtc\" >= ? AND (\"SyncedAtUtc\" > ? OR (\"SyncedAtUtc\" = ? AND \"SyncId\" > ?)) ORDER BY \"SyncedAtUtc\", \"SyncId\" LIMIT n, con el cursor de la última fila aplicada; una página que no se llena termina la tabla.",
            "Marcadores: <time> = una hora que cambia en cada corrida, <id:N> = un GUID al azar (clinicId, generación), <nonce:N> = el InstanceNonce, <appVersion> = la versión de TPVC, <osDescription> = el sistema de la computadora que grabó. Los SyncId 10000000-0000-4000-8000-… son de los datos de ejemplo."));

        // La PC cambia un teléfono y borra la cita: la tablet lo baja en una sola petición
        using (var db = pc.Factory.CreateDbContext())
        {
            var patient = db.Patients.Single(p => p.SyncId == FixtureId(3));
            patient.Phone = "5587654321";
            db.Appointments.Remove(db.Appointments.Single(a => a.SyncId == FixtureId(5)));
            await db.SaveChangesAsync();
        }
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        StartRecording();
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.HasCount(1, _server.Transcript, "La bajada incremental es una sola petición");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/incremental.jsonl", Transcript("incremental",
            "Con los cursores guardados y nada que subir, la bajada es una sola petición: un batch con BEGIN/COMMIT (una sola foto, V14) con la hora del servidor y las claves de _tpvc_meta, la primera página de cada tabla y de las lápidas omitiendo las filas propias (OriginDeviceId), y el perfil del consultorio.",
            "Llegan el paciente con su teléfono nuevo y la lápida de la cita; se aplica la lápida si su DeletedAtUtc es igual o más nuevo que la fila.",
            "Solo se piden más páginas de las tablas que llenaron la suya."));
    }

    [TestMethod]
    public async Task Hrana_TiesInTheKey_MatchTheTranscript()
    {
        // Cinco categorías con la misma hora de la nube: la página de 2 sigue por SyncId
        var pc = CreateDevice("pc", seed: false);
        var at = DateTime.UtcNow;
        _server.Clock = () => at;
        for (int i = 1; i <= 5; i++) await SaveFixtureAsync(pc, new Category { SyncId = FixtureId(100 + i), Name = $"Categoría {i}", Icon = "Folder" });
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        _server.Clock = null;

        var tablet = CreateDevice("tablet", seed: false);
        tablet.Sync.PullPageSizeOverride = 2;
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        StartRecording();
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        using (var db = tablet.Factory.CreateDbContext()) Assert.AreEqual(5, db.Categories.Count(), "Ninguna se pierde en el empate");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/key-ties.jsonl", Transcript("key-ties",
            "Cinco categorías subidas con la misma hora de la nube (SyncedAtUtc igual) y páginas de 2: el cursor es (SyncedAtUtc, SyncId) de la última fila aplicada, así que la segunda página empieza en la tercera categoría y la tercera página trae la quinta.",
            "Comparar solo por SyncedAtUtc > ? perdería las filas empatadas; comparar con >= las repetiría para siempre."));
    }

    [TestMethod]
    public async Task Hrana_GenerationChange_MatchesTheTranscript()
    {
        var (_, tablet) = await CloudWithReadFixtureAsync();
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Otra PC empieza a reemplazar la nube con un respaldo: generación nueva, todavía no lista
        var previous = CloudMeta("generation");
        const string replacement = "10000000-0000-4000-8000-000000000900";
        ExecuteCloud($"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generationPrevious', '{previous}'); " +
                     $"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generation', '{replacement}');");
        StartRecording();
        var waiting = await tablet.Sync.SyncAsync();
        Assert.IsFalse(waiting.Success);
        StringAssert.Contains(waiting.ErrorMessage, "reemplazando la nube");

        // Ya terminó: la tablet se vacía y vuelve a bajar todo, una vez
        ExecuteCloud($"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generationReady', '{replacement}');");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        GoldenVectorTests.CheckOrWrite("contracts/hrana/generation-change.jsonl", Transcript("generation-change",
            "La nube pasa a otra generación (10000000-…-000000000900) porque otra PC la reemplaza con un respaldo. Primero generationReady todavía es la anterior: el equipo lo ve en la primera instrucción (la hora y las claves de _tpvc_meta), no escribe ni baja nada y espera (GENERATION_PENDING).",
            "Cuando generationReady es la nueva: guarda lo que no había subido, se vacía, adopta la generación nueva y baja todo una sola vez (sin omitir sus filas).",
            "La cerca de generación de cada escritura y el SELECT de la generación al final de cada página están en lww.json y en la regla 'Cerca de generación' de schema.json."));
    }

    [TestMethod]
    public async Task Hrana_BlockedAndUnauthorized_MatchTheTranscripts()
    {
        var (_, tablet) = await CloudWithReadFixtureAsync();
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        StartRecording();
        _server.Blocked = FakeBlockedMode.StatementError;
        var blocked = await tablet.Sync.SyncAsync();
        _server.Blocked = FakeBlockedMode.HttpError;
        var blockedHttp = await tablet.Sync.SyncAsync(lockWait: TimeSpan.FromSeconds(5));
        _server.Blocked = FakeBlockedMode.None;
        Assert.IsFalse(blocked.Success);
        Assert.IsFalse(blockedHttp.Success);
        Assert.AreEqual(TursoSyncPauseReason.QuotaExhausted, tablet.Sync.PauseReason, "Cuota agotada: pausa de una hora");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/blocked.jsonl", Transcript("blocked",
            "Cuota agotada, como la simula el servidor (V7: falta ver la forma exacta en Turso). Primero HTTP 200 con cada instrucción en error de código BLOCKED; después la petición entera rechazada con un estado de error y BLOCKED en el cuerpo.",
            "Las dos se tratan igual: la sincronización automática se detiene una hora y lo que pide el médico sigue intentando. No se borra ni se aparta nada."));

        // Un token anulado (otra rotación): 401, y el equipo no borra nada
        var fresh = CreateDevice("celular", seed: false);
        Assert.IsTrue((await fresh.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await fresh.Sync.SyncAsync()).Success);
        _server.AcceptedTokens = new HashSet<string> { "token-nuevo" };
        StartRecording();
        var rejected = await fresh.Sync.SyncAsync();
        _server.AcceptedTokens = null;
        Assert.IsFalse(rejected.Success);
        StringAssert.Contains(rejected.ErrorMessage, "401");
        using (var db = fresh.Factory.CreateDbContext()) Assert.AreEqual(1, db.Patients.Count(), "Un 401 no borra nada");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/unauthorized.jsonl", Transcript("unauthorized",
            "Un token desconocido o anulado (turso db tokens invalidate anula todos los de la base): HTTP 401 a la primera petición, sin ejecutar nada. El cuerpo es el del servidor simulado (V3: falta ver el de Turso real).",
            "El equipo no borra ni aparta nada; pide pegar el token nuevo (pasos 8.14 y 8.15)."));
    }
}
