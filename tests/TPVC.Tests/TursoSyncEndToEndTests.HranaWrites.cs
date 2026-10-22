using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Folios;
using HandoffFixture = TPVC.Tests.GoldenVectorTests.HandoffFixture;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.25, kit de conformidad de escritura: contracts/hrana/*.jsonl de quien escribe. Con el mismo formato y los mismos
/// marcadores que los de lectura (TursoSyncEndToEndTests.Hrana.cs): la subida, el conflicto de empate, la lápida contra la
/// edición, la cerca de generación, el contador de folios y la consulta entre equipos (enviar, aceptar, respuesta perdida y
/// recuperar). Se regeneran con TPVC_WRITE_GOLDEN=1.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private const string MarkersNote =
        "Marcadores: <time> = una hora que cambia en cada corrida, <id:N> = un GUID al azar, <nonce:N> = el InstanceNonce, <appVersion> = la versión de TPVC, <osDescription> = el sistema de la computadora que grabó. Los SyncId 10000000-0000-4000-8000-… son fijos.";

    /// <summary>La nube con los datos de ejemplo de 8.13 y la tablet unida y al día.</summary>
    private async Task<(Device Pc, Device Tablet)> JoinedTabletAsync()
    {
        var (pc, tablet) = await CloudWithReadFixtureAsync();
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        return (pc, tablet);
    }

    private static object? LocalScalar(Device device, string sql)
    {
        using var db = device.Factory.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string Upper(int fixture) => FixtureId(fixture).ToString().ToUpperInvariant();

    // ================================================================== sincronización

    [TestMethod]
    public async Task Hrana_Push_MatchesTheTranscript()
    {
        var (_, tablet) = await JoinedTabletAsync();

        // La tablet cambia el teléfono del expediente (por campo, paso 8.22), agenda una cita y borra la nota
        var patient = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == FixtureId(3));
        patient.Phone = "5587654321";
        await tablet.Inventory.SavePatientAsync(patient);
        await SaveFixtureAsync(tablet, new PatientAppointment
        {
            SyncId = FixtureId(6), PatientRecordId = patient.Id, PatientName = patient.FullName, PatientRecordNumber = patient.RecordNumber,
            Date = FixtureAppointment, Time = "11:00", Reason = "Revisión de estudios", CreatedAt = FixtureDate
        });
        using (var db = tablet.Factory.CreateDbContext())
        {
            db.PatientConsultations.Remove(db.PatientConsultations.Single(c => c.SyncId == FixtureId(4)));
            await db.SaveChangesAsync();
        }

        StartRecording();
        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM PatientConsultations WHERE SyncId = '{SyncIds.Format(FixtureId(4))}'"));
        GoldenVectorTests.CheckOrWrite("contracts/hrana/push.jsonl", Transcript("push",
            "La tablet sube tres cambios: el expediente con el teléfono nuevo (y FieldStampsJson con la marca del campo, paso 8.22), una cita nueva y el borrado de la nota (su lápida). Con algo que subir, la primera petición es la de la hora del servidor y las claves de _tpvc_meta (8.7: mide el desfase y revisa clinicId, generation y el protocolo antes de escribir).",
            "La subida va en una petición batch con BEGIN/COMMIT (atómica, V14): por tabla en el orden de schema.json, cada fila con el upsert de la nube (INSERT … SELECT … WHERE NOT EXISTS lápida igual o más nueva AND la cerca de generación ON CONFLICT(SyncId) DO UPDATE … WHERE excluded.UpdatedAtUtc > UpdatedAtUtc, golden/lww.json), cada lápida con su upsert y el DELETE de la fila, y al final el SELECT de la generación (si cambió, nada de esto vale: generation-fence.jsonl). affected_row_count = 0 en un upsert es una fila que la nube no aceptó (tie-conflict.jsonl, tombstone-vs-edit.jsonl).",
            "Después de subir, el latido con LastPushUtc (regla «Latido»: la marca de cambios que leen los demás, paso 8.24) y la bajada incremental en una sola petición, como en incremental.jsonl.",
            "La sonda del esquema, la escritura del manifiesto y el registro en DeviceRegistry, si aparecen, son de la PC: Kotlin no corre DDL ni se registra solo. Las instrucciones de escritura no van en statements.json: Kotlin puede escribirlas con otro texto si hacen lo mismo (las reglas de lww.json), salvo folio.take y folio.raise y, desde la revisión 19, las del perfil y el membrete (profile.write y asset.*).",
            MarkersNote));
    }

    [TestMethod]
    public async Task Hrana_TieConflict_MatchesTheTranscript()
    {
        var (pc, tablet) = await JoinedTabletAsync();

        // Los dos guardan el producto con la misma marca y otro precio; la tablet sube primero
        var stamp = TursoSyncService.FormatDate(DateTime.UtcNow.AddMinutes(1));
        foreach (var (device, price) in new[] { (pc, "111.5"), (tablet, "222.5") })
        {
            using var db = device.Factory.CreateDbContext();
            db.Database.ExecuteSqlRaw("UPDATE \"Products\" SET \"Price\" = {0}, \"UpdatedAtUtc\" = {1} WHERE \"SyncId\" = {2}", price, stamp, Upper(2));
        }
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        StartRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("222.5", LocalScalar(pc, $"SELECT Price FROM Products WHERE SyncId = '{Upper(2)}'")?.ToString());
        GoldenVectorTests.CheckOrWrite("contracts/hrana/tie-conflict.jsonl", Transcript("tie-conflict",
            "Empate: la PC y la tablet guardaron el mismo producto con la misma marca (UpdatedAtUtc) y otro precio, y la tablet ya subió el suyo. Se graba la sincronización de la PC.",
            "El upsert de la PC responde affected_row_count 0 (la nube solo acepta una marca mayor). La PC vuelve a leer esas filas con sus lápidas en una sola petición y aplica la de la nube aunque las marcas empaten (SyncRules.ServerRowAppliesAfterReject, golden/lww.json): las dos quedan con el precio de la tablet y nada vuelve a subir.",
            MarkersNote));
    }

    [TestMethod]
    public async Task Hrana_TombstoneVsEdit_MatchesTheTranscript()
    {
        var (pc, tablet) = await JoinedTabletAsync();

        // La PC corrige el diagnóstico y, después, la tablet borra la nota y sube el borrado
        using (var db = pc.Factory.CreateDbContext())
        {
            db.PatientConsultations.Single(c => c.SyncId == FixtureId(4)).Diagnosis = "Faringitis estreptocócica (J02.0)";
            await db.SaveChangesAsync();
        }
        await Task.Delay(5);
        using (var db = tablet.Factory.CreateDbContext())
        {
            db.PatientConsultations.Remove(db.PatientConsultations.Single(c => c.SyncId == FixtureId(4)));
            await db.SaveChangesAsync();
        }
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        StartRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(0L, LocalScalar(pc, $"SELECT COUNT(*) FROM PatientConsultations WHERE SyncId = '{Upper(4)}'"), "La lápida más nueva gana");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/tombstone-vs-edit.jsonl", Transcript("tombstone-vs-edit",
            "Lápida contra edición: la PC corrige el diagnóstico de una nota y, después, la tablet la borra y sube la lápida. Se graba la sincronización de la PC.",
            "El upsert de la PC no entra (affected_row_count 0): hay una lápida igual o más nueva que su marca. La PC relee la fila y su lápida, aplica el borrado (DeletedAtUtc más nuevo que su fila) y no vuelve a subir la nota. Si la edición hubiera sido posterior al borrado, el upsert sí entraría y la nota volvería a existir (contracts/scenarios/tombstones.json).",
            MarkersNote));
    }

    [TestMethod]
    public async Task Hrana_GenerationFence_MatchesTheTranscript()
    {
        var (_, tablet) = await JoinedTabletAsync();
        using (var db = tablet.Factory.CreateDbContext())
        {
            db.Products.Single(p => p.SyncId == FixtureId(2)).Price = 99.5m;
            await db.SaveChangesAsync();
        }

        // Otra PC empieza a reemplazar la nube justo cuando llega la subida de la tablet (entre su saludo y su escritura)
        var previous = CloudMeta("generation");
        const string replacement = "10000000-0000-4000-8000-000000000900";
        bool replaced = false;
        _server.BeforeExecuteAsync = sql =>
        {
            if (!replaced && IsUploadRequest(sql))
            {
                replaced = true;
                ExecuteCloud($"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generationPrevious', '{previous}'); " +
                             $"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generation', '{replacement}');");
            }
            return Task.CompletedTask;
        };

        StartRecording();
        var result = await tablet.Sync.SyncAsync();
        _server.BeforeExecuteAsync = null;
        Assert.IsFalse(result.Success);
        Assert.AreEqual("35.5", CloudScalar($"SELECT Price FROM Products WHERE SyncId = '{SyncIds.Format(FixtureId(2))}'")?.ToString(), "Nada cayó en la nube nueva");
        GoldenVectorTests.CheckOrWrite("contracts/hrana/generation-fence.jsonl", Transcript("generation-fence",
            "Cerca de generación (paso 8.9): la tablet sube un precio y, entre su saludo (que vio la generación de siempre) y su subida, otra PC empieza a reemplazar la nube (generation 10000000-…-000000000900, generationPrevious la anterior).",
            "Cada upsert y lápida lleva la cerca ((SELECT Value FROM _tpvc_meta WHERE Key = 'generation') IS ?, con la generación de la tablet), así que no escribe nada (affected_row_count 0), y el SELECT de la generación al final de la petición regresa la nueva: la tablet se detiene (GENERATION_CHANGED) sin avanzar su cursor ni releer nada, y la siguiente sincronización espera a generationReady (generation-change.jsonl).",
            MarkersNote));
    }

    // ================================================================== folios

    [TestMethod]
    public async Task Hrana_FolioCounter_MatchesTheTranscript()
    {
        // Bases vacías: el número no depende del año ni de los datos de ejemplo
        var pc = CreateDevice("pc", seed: false);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        StartRecording();
        var first = await NewPatientAsync(pc, "Paciente Uno En La PC");
        var second = await NewPatientAsync(tablet, "Paciente Dos En La Tablet");
        _server.IsOffline = true;
        var offline = await NewPatientAsync(tablet, "Paciente Sin Red");
        _server.IsOffline = false;
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, 1), first.RecordNumber);
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, 2), second.RecordNumber);
        Assert.AreEqual($"{ExpSeries}-{tablet.Settings.CurrentSettings.DeviceCode}-001", offline.RecordNumber);

        var text = Transcript("folio-counter",
            "El contador de folios (paso 8.16): con conexión, cada expediente nuevo toma su número con folio.take de statements.json (literal): VALUES (serie, piso + 1) ON CONFLICT(Series) DO UPDATE SET LastNumber = max(LastNumber, piso) + 1 RETURNING LastNumber, donde el piso es el mayor número local de la serie. Una sola instrucción atómica: la PC toma el 1 y la tablet el 2 sin sincronizar entre ellos.",
            "Sin red no hay petición: el folio lleva el código del equipo (EXP-<year>-P2-001 en la PC que hace de tablet; T1 en la tablet) y su número sale de folios.json fuera de la base (golden/folios.json).",
            "El CREATE TABLE IF NOT EXISTS FolioCounters que va antes en la misma petición es de la PC (la tabla la crea la PC principal; Kotlin manda solo folio.take, y si falla o no responde en 2 s el folio lleva la letra).",
            "<year> = el año del consultorio (la serie EXP-<year>; TKT-<today> sería la del día).",
            MarkersNote);
        GoldenVectorTests.CheckOrWrite("contracts/hrana/folio-counter.jsonl", text.Replace($"\"{ExpSeries}\"", "\"EXP-<year>\"", StringComparison.Ordinal));
    }

    // ================================================================== consulta entre equipos

    private (ConsultationHandoffService Pc, ConsultationHandoffService Tablet, ConsultationHandoffMarker Marker) HandoffBetween(Device pc, Device tablet)
    {
        var marker = new ConsultationHandoffMarker
        {
            Id = SyncIds.Format(FixtureId(950)),
            ToDeviceId = tablet.Settings.CurrentSettings.DeviceId,
            ToDeviceName = "tablet",
            FromDeviceId = pc.Settings.CurrentSettings.DeviceId,
            FromDeviceName = "pc",
            DatabaseUrl = Url,
            PatientSyncId = SyncIds.Format(HandoffFixture.PatientSyncId),
            PatientName = "Ana Pérez Núñez",
            Folio = "CNS-20260115-103000",
            CreatedAt = HandoffFixture.SavedAt
        };
        return (new ConsultationHandoffService(pc.Sync, pc.Settings) { AutoPoll = false },
            new ConsultationHandoffService(tablet.Sync, tablet.Settings) { AutoPoll = false }, marker);
    }

    private static readonly string AcceptToken = SyncIds.Format(FixtureId(951));

    private const string HandoffNote =
        "La copia (Payload) es la de contracts/golden/handoff-payload.json (prepare-full), como texto JSON. ConsultationHandoffs no es una tabla sincronizada: cada instrucción va sola, con la hora de la nube ('now'). La limpieza que va en la primera revisión de cada equipo (DELETE FROM ConsultationHandoffs de los renglones terminados y DELETE FROM RegisteredDevices de los quitados y olvidados), el DDL si aparece y el registro en DeviceRegistry son de la PC: los dos equipos de la grabación son el motor de la PC; Kotlin no corre DDL ni limpia (regla «Cadencia»). Las instrucciones de la revisión son las check.* de statements.json (literales).";

    [TestMethod]
    public async Task Hrana_HandoffSendAndAccept_MatchesTheTranscript()
    {
        var (pc, tablet) = await JoinedTabletAsync();
        var (pcHandoff, tabletHandoff, marker) = HandoffBetween(pc, tablet);
        var payload = ConsultationDraftStore.Serialize(HandoffFixture.Payload());

        StartRecording();
        await pcHandoff.SendAsync(marker, payload);
        var check = await tabletHandoff.PollAsync(null);
        Assert.AreEqual(marker.Id, check.Incoming.Single().Id);
        Assert.IsTrue(await tabletHandoff.TryAcceptAsync(marker.Id, AcceptToken));
        await tabletHandoff.ClearPayloadAsync(marker.Id, AcceptToken);
        Assert.AreEqual(ConsultationHandoffService.StateAccepted, (await pcHandoff.PollAsync(marker.Id)).OutgoingState);
        await pcHandoff.AcknowledgeAsync(marker.Id);
        GoldenVectorTests.CheckOrWrite("contracts/hrana/handoff-send-accept.jsonl", Transcript("handoff-send-accept",
            "Enviar y aceptar una consulta. 1) La PC la envía: INSERT del renglón con State 'enviada', PayloadVersion 1 y la copia (si el INSERT falla con UNIQUE, un intento anterior sí llegó). 2) La revisión de la tablet (check.incoming con su DeviceId, check.peerPush y, la primera vez, el latido y la lista de compañeros) la encuentra. 3) La tablet la toma con una ficha propia (AcceptToken, aquí fija): UPDATE … SET State = 'aceptada', AcceptToken = ? WHERE Id = ? AND ToDeviceId = ? AND State = 'enviada'; affected_row_count 1 = es suya, 0 = otro equipo la tomó o la recuperaron. 4) Ya guardada en la tablet, borra la copia (Payload = '' con su ficha). 5) La revisión de la PC (check.outgoing) ve 'aceptada' y 6) marca que ya lo vio (SenderAckAtUtc).",
            "Antes de tomarla, la tablet revisa con la copia que ya tiene el expediente y la nota (golden/handoff-accept.json) y traduce cada cargo (golden/handoff-payload.json, receive-*).",
            HandoffNote,
            MarkersNote));
    }

    [TestMethod]
    public async Task Hrana_HandoffLostResponses_MatchTheTranscript()
    {
        var (pc, tablet) = await JoinedTabletAsync();
        var (pcHandoff, tabletHandoff, marker) = HandoffBetween(pc, tablet);
        var payload = ConsultationDraftStore.Serialize(HandoffFixture.Payload());

        StartRecording();
        // 1. Se pierde la respuesta del envío: la PC no sabe si llegó y lo resuelve contra la nube (aquí sí llegó: lo cancela)
        bool dropped = false;
        _server.DropResponseAfterExecute = sql => !dropped && sql.StartsWith("INSERT INTO ConsultationHandoffs", StringComparison.Ordinal) && (dropped = true);
        var lost = await Assert.ThrowsAsync<TursoException>(() => pcHandoff.SendAsync(marker, payload));
        Assert.IsFalse(ConsultationHandoffService.IsDefinitiveRejection(lost), "No se sabe si llegó");
        Assert.AreEqual(HandoffResolution.Cancelled, await pcHandoff.ResolveOutgoingAsync(marker));

        // 2. La envía otra vez (otro renglón) y se pierde la respuesta de la tablet al tomarla: pregunta con su ficha
        marker.Id = SyncIds.Format(FixtureId(952));
        await pcHandoff.SendAsync(marker, payload);
        dropped = false;
        _server.DropResponseAfterExecute = sql => !dropped && sql.Contains("SET State = 'aceptada'", StringComparison.Ordinal) && (dropped = true);
        await Assert.ThrowsAsync<TursoException>(() => tabletHandoff.TryAcceptAsync(marker.Id, AcceptToken));
        _server.DropResponseAfterExecute = null;
        var (state, token) = await tabletHandoff.GetAcceptStateAsync(marker.Id);
        Assert.AreEqual(ConsultationHandoffService.StateAccepted, state);
        Assert.AreEqual(AcceptToken, token, "La tomó este intento");
        await tabletHandoff.ClearPayloadAsync(marker.Id, AcceptToken);
        GoldenVectorTests.CheckOrWrite("contracts/hrana/handoff-lost-response.jsonl", Transcript("handoff-lost-response",
            "Respuestas perdidas. 1) El INSERT del envío se aplica pero su respuesta no llega (la red se cae justo después; el intercambio grabado sí tiene la respuesta). La PC deja la consulta en solo lectura (Unconfirmed) y la resuelve en cuanto hay red con una sola petición: INSERT del renglón ya 'cancelada' ON CONFLICT(Id) DO UPDATE SET State = 'cancelada', Payload = '' WHERE State = 'enviada', y SELECT State. Si regresa 'aceptada', otro equipo ya la tomó; si no, vuelve a ser editable aquí (la tablet ya no la ve).",
            "2) La tablet toma otra consulta y la respuesta del UPDATE se pierde: pregunta SELECT State, AcceptToken; con 'aceptada' y su ficha, la tomó este intento y sigue (borra la copia); con otra ficha, la tomó otro intento u otro equipo.",
            HandoffNote,
            MarkersNote));
    }

    [TestMethod]
    public async Task Hrana_HandoffRecover_MatchesTheTranscript()
    {
        var (pc, tablet) = await JoinedTabletAsync();
        var (pcHandoff, tabletHandoff, marker) = HandoffBetween(pc, tablet);
        var payload = ConsultationDraftStore.Serialize(HandoffFixture.Payload());

        StartRecording();
        // 1. «Recuperar» antes de que la tablet la tome: se cancela y la tablet ya no puede tomarla
        await pcHandoff.SendAsync(marker, payload);
        Assert.AreEqual(HandoffResolution.Cancelled, await pcHandoff.ResolveOutgoingAsync(marker));
        Assert.IsFalse(await tabletHandoff.TryAcceptAsync(marker.Id, AcceptToken), "Ya no está ofrecida");
        Assert.IsEmpty((await tabletHandoff.PollAsync(null)).Incoming);

        // 2. La tablet la tomó primero: «Recuperar» no la regresa
        marker.Id = SyncIds.Format(FixtureId(953));
        await pcHandoff.SendAsync(marker, payload);
        Assert.IsTrue(await tabletHandoff.TryAcceptAsync(marker.Id, AcceptToken));
        Assert.AreEqual(HandoffResolution.Accepted, await pcHandoff.ResolveOutgoingAsync(marker));
        GoldenVectorTests.CheckOrWrite("contracts/hrana/handoff-recover.jsonl", Transcript("handoff-recover",
            "«Recuperar» (quien envió la quiere de vuelta). 1) Antes de que la tomen: la misma petición que resuelve un envío dudoso (INSERT … 'cancelada' ON CONFLICT(Id) DO UPDATE … WHERE State = 'enviada', y SELECT State) la cancela y borra la copia; el UPDATE de la tablet para tomarla ya no afecta nada (affected_row_count 0) y su revisión no la ve.",
            "2) Si la tablet ya la tomó, el DO UPDATE no cambia nada (State ya no es 'enviada'), el SELECT regresa 'aceptada' y la consulta se queda en la tablet.",
            HandoffNote,
            MarkersNote));
    }
}
