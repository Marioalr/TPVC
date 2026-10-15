using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TPVC.Models;
using TPVC.Services;
using TPVC.Tests.Fase6;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.24 (A5, F2): la cadencia (<see cref="SyncScheduler"/>) con tiempo simulado: el reloj de los equipos y la hora
/// del servidor simulado son el mismo <see cref="ManualTimeProvider"/>, y cada pasada se corre a mano
/// (<see cref="SyncScheduler.TickAsync"/>), sin esperas reales.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Un reloj simulado que es también la hora del servidor (un poco adelante de la real, para no ir hacia atrás).</summary>
    private ManualTimeProvider SimulatedClock()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(1);
        start = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, TimeSpan.Zero);
        // Lejos de la medianoche UTC: los contadores del día (descargas completas, freno) no se reinician a media prueba
        if (start.Hour == 23) start = start.Date.AddDays(1).AddMinutes(5);
        var clock = new ManualTimeProvider(start);
        _server.Clock = () => clock.Now.UtcDateTime;
        return clock;
    }

    /// <summary>La cadencia de un equipo, sin la vuelta en segundo plano (la prueba la mueve) ni la espera del arranque.</summary>
    private static SyncScheduler CadenceOf(Device device, bool inPatients)
    {
        var s = device.Settings.CurrentSettings;
        s.AutoSyncOnStartup = false;
        device.Settings.SaveSettings(s);
        var scheduler = new ConsultationHandoffService(device.Sync, device.Settings) { AutoPoll = false }.Scheduler;
        scheduler.InPatientsSection = inPatients;
        scheduler.Begin();
        return scheduler;
    }

    /// <summary>
    /// Mueve el reloj de pasada en pasada hasta <paramref name="until"/> (sin incluirlo), con varias cadencias y sucesos a
    /// cierta hora (guardar, abrir o cerrar un equipo). Las horas que recibe son de pared y se pasan al reloj monótono al
    /// recibirlas (como la vuelta en segundo plano, que espera con el reloj monótono). <paramref name="stop"/> termina antes.
    /// Por omisión espera cada sincronización que empieza la cadencia antes de seguir (<see cref="AwaitSyncs"/>).
    /// </summary>
    private sealed class CadenceDriver(ManualTimeProvider clock)
    {
        private readonly Dictionary<SyncScheduler, TimeSpan> _active = new();
        private readonly List<(TimeSpan At, int Order, Func<Task> Run)> _events = new();
        private int _order;

        public DateTime Now => clock.Now.UtcDateTime;

        /// <summary>false: la sincronización que empieza una pasada sigue sola mientras las demás pasadas corren.</summary>
        public bool AwaitSyncs { get; set; } = true;

        private TimeSpan ToMono(DateTime wall) => clock.Elapsed + (wall - Now);

        public void Open(SyncScheduler scheduler)
        {
            scheduler.Begin();
            _active[scheduler] = clock.Elapsed;
        }

        public void Close(SyncScheduler scheduler) => _active.Remove(scheduler);

        /// <summary>Lo que hace <see cref="SyncScheduler.Wake"/> con la vuelta en segundo plano: una pasada ya.</summary>
        public void Wake(SyncScheduler scheduler) => _active[scheduler] = clock.Elapsed;

        public void At(DateTime at, Func<Task> run) => _events.Add((ToMono(at), _order++, run));

        /// <summary>Una pasada o un suceso, lo que toque primero antes de <paramref name="until"/>; false si no hay nada.</summary>
        public Task<bool> StepAsync(DateTime until) => StepMonoAsync(ToMono(until));

        private async Task<bool> StepMonoAsync(TimeSpan until)
        {
            var nextEvent = _events.Count == 0 ? TimeSpan.MaxValue : _events.Min(e => e.At);
            var nextTick = _active.Count == 0 ? TimeSpan.MaxValue : _active.Values.Min();
            var next = nextEvent <= nextTick ? nextEvent : nextTick;
            if (next >= until) return false;
            if (next > clock.Elapsed) clock.Advance(next - clock.Elapsed);

            if (nextEvent <= nextTick)
            {
                var due = _events.Where(e => e.At == nextEvent).OrderBy(e => e.Order).First();
                _events.Remove(due);
                await due.Run();
                return true;
            }

            var scheduler = _active.First(kv => kv.Value == nextTick).Key;
            var wait = await scheduler.TickAsync();
            if (AwaitSyncs) await scheduler.PendingSync;
            // Como la vuelta en segundo plano: nunca vuelve antes de 1 s
            if (_active.ContainsKey(scheduler))
                _active[scheduler] = clock.Elapsed + (wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
            return true;
        }

        public async Task RunUntilAsync(DateTime until, Func<bool>? stop = null)
        {
            var end = ToMono(until);
            while (stop == null || !stop())
            {
                if (!await StepMonoAsync(end))
                {
                    if (end > clock.Elapsed) clock.Advance(end - clock.Elapsed);
                    return;
                }
            }
        }
    }

    private static List<(DateTime At, string Reason)> RecordReasons(SyncScheduler scheduler, ManualTimeProvider clock)
    {
        var reasons = new List<(DateTime, string)>();
        scheduler.SyncRequested += reason => reasons.Add((clock.Now.UtcDateTime, reason));
        return reasons;
    }

    private async Task<(Device Pc, Device Tablet)> PcAndTabletOnClockAsync(ManualTimeProvider clock)
    {
        var pc = CreateDevice("pc", seed: true, clock);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false, clock);
        var first = await tablet.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        // La PC ya ve el latido de la tablet (un compañero que puede recibir consultas)
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        return (pc, tablet);
    }

    private static async Task<string?> OccupationAsync(Device device, Guid syncId) =>
        (await device.Inventory.GetPatientsAsync()).Single(p => p.SyncId == syncId).Occupation;

    // ================================================================== la marca de cambios

    [TestMethod]
    public async Task WhatTheTabletSaves_ReachesThePcWithinFourSecondsInPatients_AndThirtyElsewhere()
    {
        var clock = SimulatedClock();
        var (pc, tablet) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(1));
        Assert.IsTrue(cadence.HasPeer, "La tablet cuenta como compañero");
        Assert.AreEqual(SyncScheduler.FastCheckInterval, cadence.CheckInterval);

        async Task<TimeSpan> TabletEditSeenByPcAsync(string occupation)
        {
            var patient = (await tablet.Inventory.GetPatientsAsync()).First();
            patient.Occupation = occupation;
            await tablet.Inventory.SavePatientAsync(patient);
            Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
            var pushedAt = driver.Now;
            string? seen = null;
            while (seen != occupation && await driver.StepAsync(pushedAt.AddMinutes(2)))
                seen = await OccupationAsync(pc, patient.SyncId);
            Assert.AreEqual(occupation, seen);
            return driver.Now - pushedAt;
        }

        reasons.Clear();
        var inPatients = await TabletEditSeenByPcAsync("Docente");
        Assert.IsLessThanOrEqualTo(SyncScheduler.FastCheckInterval, inPatients, $"En Pacientes la PC lo ve en ≤ 4 s (tardó {inPatients})");
        Assert.AreEqual(SyncScheduler.ReasonPeerChanges, reasons.Single().Reason, "Lo trajo la marca de cambios, no la descarga de seguridad");

        // Fuera de Pacientes: la revisión va cada 30 s
        cadence.InPatientsSection = false;
        await driver.RunUntilAsync(driver.Now.AddSeconds(5));
        Assert.AreEqual(SyncScheduler.IdleCheckInterval, cadence.CheckInterval);
        reasons.Clear();
        var elsewhere = await TabletEditSeenByPcAsync("Ingeniera");
        Assert.IsLessThanOrEqualTo(SyncScheduler.IdleCheckInterval, elsewhere, $"Fuera de Pacientes la PC lo ve en ≤ 30 s (tardó {elsewhere})");
        Assert.IsGreaterThan(SyncScheduler.FastCheckInterval, elsewhere, "Fuera de Pacientes no revisa cada 4 s");
        Assert.AreEqual(SyncScheduler.ReasonPeerChanges, reasons.Single().Reason);

        // La misma marca no vuelve a sincronizar: sin cambios nuevos, solo revisiones
        reasons.Clear();
        await driver.RunUntilAsync(driver.Now.AddMinutes(5));
        Assert.IsEmpty(reasons, "Sin cambios de otro equipo no hay sincronizaciones (la de seguridad es a los 10 min)");
    }

    [TestMethod]
    public async Task AChangeMarkInTheFuture_TriggersOneSync_NotALoop_AndDoesNotHideTheOthers()
    {
        var clock = SimulatedClock();
        var (pc, tablet) = await PcAndTabletOnClockAsync(clock);
        // Un equipo con una marca rara (en el futuro): cada revisión la vería posterior a la última bajada
        using (_server.Lock())
        {
            using var cmd = _server.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, LastPushUtc) " +
                              "VALUES ('device-futuro', 'Equipo con el reloj roto', 'WindowsDesktop', '1.43.0', '2099-01-01 00:00:00.000', '2099-01-01 00:00:00.000')";
            cmd.ExecuteNonQuery();
        }
        var cadence = CadenceOf(pc, inPatients: true);
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddMinutes(5));
        Assert.AreEqual(1, reasons.Count(r => r.Reason == SyncScheduler.ReasonPeerChanges), "La misma marca dispara una sola sincronización");
        Assert.IsEmpty(reasons.Where(r => r.Reason != SyncScheduler.ReasonPeerChanges));

        // La marca rara no esconde lo que sube la tablet
        reasons.Clear();
        var patient = (await tablet.Inventory.GetPatientsAsync()).First();
        patient.Occupation = "Después de la marca rara";
        await tablet.Inventory.SavePatientAsync(patient);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var pushedAt = driver.Now;
        await driver.RunUntilAsync(pushedAt.AddSeconds(5));
        Assert.AreEqual(SyncScheduler.ReasonPeerChanges, reasons.Single().Reason);
        Assert.AreEqual("Después de la marca rara", await OccupationAsync(pc, patient.SyncId));
    }

    [TestMethod]
    public async Task WithoutPeers_ThePcChecksEveryThirtySeconds_AlsoInPatients()
    {
        var clock = SimulatedClock();
        var pc = CreateDevice("pc", seed: true, clock);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var cadence = CadenceOf(pc, inPatients: true);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(1));
        Assert.IsFalse(cadence.HasPeer);
        Assert.AreEqual(SyncScheduler.IdleCheckInterval, cadence.CheckInterval, "Nadie a quien enviarle ni de quien recibir: 30 s");

        // Con un envío pendiente sí va cada 4 s
        cadence.HasPendingSend = () => true;
        Assert.AreEqual(SyncScheduler.FastCheckInterval, cadence.CheckInterval);
    }

    [TestMethod]
    public async Task TheSafetyDownload_RunsTenMinutesAfterTheLastSync()
    {
        var clock = SimulatedClock();
        var pc = CreateDevice("pc", seed: true, clock);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var cadence = CadenceOf(pc, inPatients: false);
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        var start = driver.Now;
        driver.Open(cadence);

        await driver.RunUntilAsync(start.AddMinutes(25));
        CollectionAssert.AreEqual(new[] { start.AddMinutes(10), start.AddMinutes(20) }, reasons.Select(r => r.At).ToArray());
        Assert.IsTrue(reasons.All(r => r.Reason == SyncScheduler.ReasonSafety));

        // Una sincronización de otro origen (después de guardar) corre la de seguridad otros 10 min
        reasons.Clear();
        var saved = driver.Now;
        Assert.IsTrue((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        await driver.RunUntilAsync(saved.AddMinutes(10).AddSeconds(1));
        Assert.AreEqual(saved.AddMinutes(10), reasons.Single().At);
    }

    [TestMethod]
    public async Task WithoutNetwork_RetriesBackOff_AndTheNetworkComingBackSyncsAtOnce()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        // Fuera de Pacientes: la revisión sin red se espacia como los reintentos (en Pacientes con un compañero, no pasa de 30 s)
        var cadence = CadenceOf(pc, inPatients: false);
        var failures = new List<DateTime>();
        pc.Sync.SyncCompleted += r => { if (!r.Success) failures.Add(clock.Now.UtcDateTime); };
        var checks = new List<DateTime>();
        cadence.CheckRunner = async ct =>
        {
            checks.Add(clock.Now.UtcDateTime);
            await cadence.Handoff.PollAsync(null, ct);
        };
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(5));

        // Se cae la red y falla la sincronización de después de guardar
        _server.IsOffline = true;
        var start = driver.Now;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.AreEqual(TursoSyncStatus.Offline, pc.Sync.Status);
        checks.Clear();
        await driver.RunUntilAsync(start.AddMinutes(20));

        var offsets = failures.Select(f => (f - start).TotalSeconds).ToArray();
        CollectionAssert.AreEqual(new double[] { 0, 30, 90, 210, 510, 810, 1110 }, offsets,
            $"Reintentos a los 30 s, 1 min, 2 min y luego cada 5 min: {string.Join(", ", offsets)}");
        Assert.IsTrue(reasons.All(r => r.Reason == SyncScheduler.ReasonRetry));

        // La revisión también se espacia (sin red no pregunta cada 4 s)
        var gaps = checks.Zip(checks.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
        CollectionAssert.AreEqual(new double[] { 30, 60, 120, 300, 300 }, gaps.Take(5).ToArray(), $"Revisiones: {string.Join(", ", gaps)}");

        // Vuelve la red: sincroniza a más tardar con la primera revisión que responde (o antes, si toca el reintento)
        _server.IsOffline = false;
        reasons.Clear();
        var back = driver.Now;
        await driver.RunUntilAsync(back.AddMinutes(6), () => reasons.Count > 0);
        var (syncedAt, why) = reasons.Single();
        if (why == SyncScheduler.ReasonNetworkBack) CollectionAssert.Contains(checks, syncedAt, "En la pasada de la revisión que respondió");
        else Assert.AreEqual(SyncScheduler.ReasonRetry, why);
        Assert.AreEqual(TursoSyncStatus.Success, pc.Sync.Status);
        Assert.IsFalse(cadence.IsRetryPending);

        // Otra vez sin red, y una revisión que falla; al volver, la primera revisión que responde sincroniza sin esperar
        _server.IsOffline = true;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        await driver.RunUntilAsync(driver.Now.AddSeconds(25));
        Assert.IsTrue(cadence.IsRetryPending);
        _server.IsOffline = false;
        reasons.Clear();
        checks.Clear();
        // El médico vuelve a Pacientes: la revisión va enseguida, antes del reintento
        cadence.InPatientsSection = false;
        cadence.InPatientsSection = true;
        driver.Wake(cadence);
        var enteredAt = driver.Now;
        await driver.StepAsync(enteredAt.AddSeconds(1));
        CollectionAssert.AreEqual(new[] { enteredAt }, checks);
        Assert.AreEqual((enteredAt, SyncScheduler.ReasonNetworkBack), reasons.Single(), "En la misma pasada de la revisión que respondió");

        // Windows avisa que volvió la red (o que salió de la suspensión): revisa y sincroniza en ese momento
        _server.IsOffline = true;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        _server.IsOffline = false;
        reasons.Clear();
        cadence.OnConnectivityRestored();
        driver.Wake(cadence);
        var restoredAt = driver.Now;
        await driver.StepAsync(restoredAt.AddMinutes(1));
        Assert.AreEqual((restoredAt, SyncScheduler.ReasonNetworkBack), reasons.Single());
        Assert.AreEqual(TursoSyncStatus.Success, pc.Sync.Status);
    }

    [TestMethod]
    public async Task ActivatingTheWindow_SyncsOnlyIfTheLastGoodSyncIsOlderThanThirtySeconds()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: false);
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(20));

        cadence.OnWindowActivated();
        driver.Wake(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(1));
        Assert.IsEmpty(reasons, "La última buena fue hace 20 s");

        await driver.RunUntilAsync(driver.Now.AddSeconds(20));
        cadence.OnWindowActivated();
        driver.Wake(cadence);
        var activatedAt = driver.Now;
        await driver.RunUntilAsync(activatedAt.AddSeconds(1));
        Assert.AreEqual((activatedAt, SyncScheduler.ReasonActivated), reasons.Single());
    }

    [TestMethod]
    public async Task WithTheCloudOff_OrPaused_NothingReachesTurso()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);

        var s = pc.Settings.CurrentSettings;
        s.IsSyncEnabled = false;
        pc.Settings.SaveSettings(s);
        var requests = _server.RequestCount;
        await driver.RunUntilAsync(driver.Now.AddHours(1));
        Assert.AreEqual(requests, _server.RequestCount, "Con la nube apagada no se pregunta nada a Turso");
        Assert.IsEmpty(reasons);

        // Encendida pero con el freno de consumo: tampoco
        s.IsSyncEnabled = true;
        pc.Settings.SaveSettings(s);
        pc.Settings.TripConsumptionBrake();
        await driver.RunUntilAsync(driver.Now.AddHours(1));
        Assert.AreEqual(requests, _server.RequestCount, "Con el freno activo no se pregunta nada a Turso");
        Assert.IsEmpty(reasons);
    }

    // ================================================================== revisión 1: la sincronización no detiene la revisión

    private void OfferConsultationToPcInTheCloud(string id)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ConsultationHandoffs (Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, " +
                          "Folio, Payload, PayloadVersion, State, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, 'device-tablet', 'tablet', 'device-pc', 'pc', " +
                          "'p', 'Paciente', 'CNS-1', '{}', 1, 'enviada', strftime('%Y-%m-%d %H:%M:%f','now'), strftime('%Y-%m-%d %H:%M:%f','now'))";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private int HeartbeatsOf(string deviceId)
    {
        var heartbeat = TursoSyncService.HeartbeatSql(afterPush: false);
        return _server.Requests.SelectMany(r => r.Statements)
            .Count(st => st.Sql == heartbeat && st.Args[0]!["value"]!.GetValue<string>() == deviceId);
    }

    [TestMethod]
    public async Task ASlowScheduledSync_DoesNotStopTheChecksNorTheHeartbeat()
    {
        var clock = SimulatedClock();
        var (pc, tablet) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var offered = new List<DateTime>();
        cadence.CheckRunner = async ct =>
        {
            var result = await cadence.Handoff.PollAsync(null, ct);
            if (result.Incoming.Any(r => r.Id == "oferta-1")) offered.Add(clock.Now.UtcDateTime);
        };
        var reasons = RecordReasons(cadence, clock);
        var driver = new CadenceDriver(clock) { AwaitSyncs = false };
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(5));

        // La tablet sube; la bajada de la PC se queda colgada (una red que no contesta, una bajada de muchas páginas)
        var patient = (await tablet.Inventory.GetPatientsAsync()).First();
        patient.Occupation = "Mientras la PC baja";
        await tablet.Inventory.SavePatientAsync(patient);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.BeforeExecuteAsync = sql => sql.Contains("\"SyncedAtUtc\" >= ?", StringComparison.Ordinal) ? release.Task : Task.CompletedTask;
        await driver.RunUntilAsync(driver.Now.AddSeconds(10), () => reasons.Count > 0);
        Assert.AreEqual(SyncScheduler.ReasonPeerChanges, reasons.Single().Reason);
        Assert.IsFalse(cadence.PendingSync.IsCompleted, "La sincronización sigue colgada");

        // Dos minutos con la sincronización colgada: la tablet ofrece una consulta y la PC la ve en ≤ 4 s, y el latido sigue
        var beatsBefore = HeartbeatsOf("device-pc");
        var offeredAt = driver.Now;
        OfferConsultationToPcInTheCloud("oferta-1");
        await driver.RunUntilAsync(offeredAt.AddMinutes(2));
        Assert.IsNotEmpty(offered, "La revisión siguió con la sincronización colgada");
        Assert.IsLessThanOrEqualTo(SyncScheduler.FastCheckInterval, offered[0] - offeredAt);
        Assert.AreEqual(2, HeartbeatsOf("device-pc") - beatsBefore, "Un latido cada 60 s también con la sincronización colgada");
        Assert.AreEqual(1, reasons.Count, "Mientras corre no se pide otra");
        Assert.IsFalse(cadence.PendingSync.IsCompleted);

        // Se suelta: termina bien y trae lo de la tablet
        _server.BeforeExecuteAsync = null;
        release.SetResult();
        await cadence.PendingSync;
        Assert.AreEqual(TursoSyncStatus.Success, pc.Sync.Status, pc.Sync.StatusMessage);
        Assert.AreEqual("Mientras la PC baja", await OccupationAsync(pc, patient.SyncId));
        await driver.RunUntilAsync(driver.Now.AddMinutes(1));
        Assert.AreEqual(1, reasons.Count, "La marca ya atendida no vuelve a sincronizar");
    }

    // ================================================================== revisión 2: la revisión vuelve a su paso

    [TestMethod]
    public async Task WithoutNetworkInPatientsWithAPeer_TheCheckNeverWaitsMoreThanThirtySeconds()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var checks = new List<DateTime>();
        cadence.CheckRunner = async ct =>
        {
            checks.Add(clock.Now.UtcDateTime);
            await cadence.Handoff.PollAsync(null, ct);
        };
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(5));
        Assert.IsTrue(cadence.HasPeer);

        _server.IsOffline = true;
        checks.Clear();
        await driver.RunUntilAsync(driver.Now.AddMinutes(10));
        var gaps = checks.Zip(checks.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
        Assert.IsTrue(gaps.Length >= 15 && gaps.All(g => g == 30), $"Revisiones: {string.Join(", ", gaps)}");
    }

    [TestMethod]
    public async Task AfterAnOutageWithoutWindowsNotice_ASuccessfulSyncOrHandoffRequestOrActivation_BringsTheCheckBack()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: false);
        var driver = new CadenceDriver(clock);
        driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(5));

        async Task OutageAsync()
        {
            // ~4 min sin internet, sin aviso de Windows: la revisión llega a esperar 2–5 min
            _server.IsOffline = true;
            await driver.RunUntilAsync(driver.Now.AddMinutes(4));
            _server.IsOffline = false;
            Assert.IsGreaterThan(TimeSpan.FromSeconds(30), cadence.TimeToNextCheck, "La revisión quedó espaciada");
        }

        // Una sincronización buena (después de guardar, o la rápida)
        await OutageAsync();
        Assert.IsTrue((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.IsLessThanOrEqualTo(TimeSpan.Zero, cadence.TimeToNextCheck, "Revisa enseguida");
        driver.Wake(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(31));
        Assert.AreEqual(SyncScheduler.IdleCheckInterval, cadence.CheckInterval);
        Assert.IsLessThanOrEqualTo(SyncScheduler.IdleCheckInterval, cadence.TimeToNextCheck, "De vuelta a su paso");

        // Una petición de la consulta entre equipos que llega a la nube
        await OutageAsync();
        await cadence.Handoff.GetAcceptStateAsync("no-existe");
        Assert.IsLessThanOrEqualTo(TimeSpan.Zero, cadence.TimeToNextCheck);

        // Activar la ventana (también con un reintento esperando)
        driver.Wake(cadence);
        await driver.RunUntilAsync(driver.Now.AddSeconds(1));
        await OutageAsync();
        _server.IsOffline = true;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success, "Sin red, falla");
        Assert.IsTrue(cadence.IsRetryPending);
        _server.IsOffline = false;
        cadence.OnWindowActivated();
        Assert.IsLessThanOrEqualTo(TimeSpan.Zero, cadence.TimeToNextCheck);
        // La revisión responde y la sincronización no espera el reintento
        driver.Wake(cadence);
        await driver.StepAsync(driver.Now.AddSeconds(1));
        Assert.IsFalse(cadence.IsRetryPending);
        Assert.AreEqual(TursoSyncStatus.Success, pc.Sync.Status, pc.Sync.StatusMessage);
    }

    // ================================================================== revisión 3: la hora de Windows no mueve la cadencia

    [TestMethod]
    public async Task StepTheWallClockBackAndForwardAnHour_TheCadenceDoesNotChange()
    {
        var clock = SimulatedClock();
        // La hora de la nube no es la de la PC: sigue al reloj monótono
        var serverStart = clock.Now.UtcDateTime;
        _server.Clock = () => serverStart + clock.Elapsed;
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var checks = new List<TimeSpan>();
        cadence.CheckRunner = async ct =>
        {
            checks.Add(clock.Elapsed);
            await cadence.Handoff.PollAsync(null, ct);
        };
        var reasons = new List<(TimeSpan At, string Reason)>();
        cadence.SyncRequested += reason => reasons.Add((clock.Elapsed, reason));
        _server.ClearRecording();
        var driver = new CadenceDriver(clock);
        var start = clock.Elapsed;
        driver.Open(cadence);

        await driver.RunUntilAsync(driver.Now.AddMinutes(3));
        clock.StepWallClock(TimeSpan.FromHours(-1));
        await driver.RunUntilAsync(driver.Now.AddMinutes(4));
        clock.StepWallClock(TimeSpan.FromHours(1));
        await driver.RunUntilAsync(driver.Now.AddMinutes(4));

        var gaps = checks.Zip(checks.Skip(1), (a, b) => (b - a).TotalSeconds).Distinct().ToArray();
        CollectionAssert.AreEqual(new double[] { 4 }, gaps, "Una revisión cada 4 s antes, durante y después del cambio de hora");
        Assert.AreEqual(11 * 15, checks.Count);
        Assert.AreEqual(11, HeartbeatsOf("device-pc"), "Un latido por minuto");
        Assert.AreEqual((start + SyncScheduler.SafetyDownloadInterval, SyncScheduler.ReasonSafety), reasons.Single(),
            "La descarga de seguridad a los 10 min, sin adelantarse ni atrasarse una hora");
    }

    // ================================================================== latido e índice parcial

    [TestMethod]
    public async Task InAnHour_ThePcWritesSixtyHeartbeats_AndChecksEveryFourSeconds()
    {
        var clock = SimulatedClock();
        var (pc, _) = await PcAndTabletOnClockAsync(clock);
        var cadence = CadenceOf(pc, inPatients: true);
        var checks = 0;
        cadence.CheckRunner = async ct =>
        {
            checks++;
            await cadence.Handoff.PollAsync(null, ct);
        };
        _server.ClearRecording();
        var driver = new CadenceDriver(clock);
        var start = driver.Now;
        driver.Open(cadence);
        await driver.RunUntilAsync(start.AddHours(1));

        var heartbeat = TursoSyncService.HeartbeatSql(afterPush: false);
        var beats = _server.Requests.SelectMany(r => r.Statements)
            .Count(st => st.Sql == heartbeat && st.Args[0]!["value"]!.GetValue<string>() == "device-pc");
        Assert.AreEqual(60, beats, "Un latido cada 60 s");
        Assert.AreEqual(900, checks, "Una revisión cada 4 s en Pacientes con un compañero");

        // Cada revisión es una sola petición; sin cambios, solo las seis descargas de seguridad sincronizan
        var checkRequests = _server.Requests.Count(r => r.Statements.Any(st => st.Sql == ConsultationHandoffService.IncomingSql));
        Assert.AreEqual(900, checkRequests);
    }

    [TestMethod]
    public async Task TheCheck_UsesThePartialIndex_InLocalSqliteAndInTheCloud()
    {
        const string plan = "EXPLAIN QUERY PLAN " + ConsultationHandoffService.IncomingSql;

        static List<string> PlanOf(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = plan.Replace("ToDeviceId = ?", "ToDeviceId = $device", StringComparison.Ordinal);
            cmd.Parameters.AddWithValue("$device", "device-pc");
            using var reader = cmd.ExecuteReader();
            var steps = new List<string>();
            while (reader.Read()) steps.Add(reader.GetString(3));
            return steps;
        }

        // SQLite local, sin ANALYZE (como una base nueva de Turso) y con filas viejas para este equipo
        using (var local = new SqliteConnection("Data Source=:memory:"))
        {
            local.Open();
            foreach (var ddl in ConsultationHandoffService.HandoffTableDdl)
            {
                using var create = local.CreateCommand();
                create.CommandText = ddl;
                create.ExecuteNonQuery();
            }
            using (var fill = local.CreateCommand())
            {
                fill.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) " +
                                   "INSERT INTO ConsultationHandoffs SELECT 'h' || i, 'device-tablet', 'Tablet', 'device-pc', 'PC', 'p', 'Paciente', 'F', '', 1, " +
                                   "'aceptada', NULL, NULL, '2026-01-01 00:00:00', '2026-01-01 00:00:00' FROM n";
                fill.ExecuteNonQuery();
            }
            var steps = PlanOf(local);
            Assert.IsTrue(steps.Any(s => s.Contains($"USING INDEX {ConsultationHandoffService.PendingIndexName}", StringComparison.Ordinal)), string.Join(" | ", steps));
            Assert.IsFalse(steps.Any(s => s.StartsWith("SCAN ConsultationHandoffs", StringComparison.Ordinal)), string.Join(" | ", steps));
        }

        // La nube: la PC la crea con su esquema (solo la PC corre DDL)
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using (_server.Lock())
        {
            var steps = PlanOf(_server.Connection);
            Assert.IsTrue(steps.Any(s => s.Contains($"USING INDEX {ConsultationHandoffService.PendingIndexName}", StringComparison.Ordinal)), string.Join(" | ", steps));
        }
    }

    // ================================================================== un día normal y un cambio de generación

    [TestMethod]
    public async Task ANormalDayOfThreeDevices_CostsLessThan200kReadsAnd5kWrites()
    {
        var clock = SimulatedClock();
        var (pc, tablet) = await PcAndTabletOnClockAsync(clock);
        var phone = CreateDevice("phone", seed: false, clock);
        Assert.IsTrue((await phone.Sync.SyncAsync()).Success);
        foreach (var d in new[] { pc, tablet }) Assert.IsTrue((await d.Sync.SyncAsync()).Success);

        var pcCadence = CadenceOf(pc, inPatients: true);
        var tabletCadence = CadenceOf(tablet, inPatients: true);
        var phoneCadence = CadenceOf(phone, inPatients: false);
        var syncs = new Dictionary<string, int>();
        foreach (var (name, cadence) in new[] { ("pc", pcCadence), ("tablet", tabletCadence), ("phone", phoneCadence) })
            cadence.SyncRequested += reason => syncs[$"{name}: {reason}"] = syncs.GetValueOrDefault($"{name}: {reason}") + 1;

        long readsBefore = _server.BilledRowsRead, writtenBefore = _server.BilledRowsWritten;
        int requestsBefore = _server.RequestCount;
        var driver = new CadenceDriver(clock);
        var day = driver.Now;
        DateTime H(double hours) => day.AddHours(hours);

        // Supuestos de §3.2: la PC abierta 10 h (8 en Pacientes), la tablet 6 h al frente (5 en pacientes), el celular 0.5 h
        driver.At(H(0), () => { driver.Open(pcCadence); return Task.CompletedTask; });
        driver.At(H(4), () => { pcCadence.InPatientsSection = false; return Task.CompletedTask; });
        driver.At(H(6), () => { pcCadence.InPatientsSection = true; return Task.CompletedTask; });
        driver.At(H(10), () => { driver.Close(pcCadence); return Task.CompletedTask; });
        driver.At(H(1), () => { driver.Open(tabletCadence); return Task.CompletedTask; });
        driver.At(H(6), () => { tabletCadence.InPatientsSection = false; return Task.CompletedTask; });
        driver.At(H(7), () => { driver.Close(tabletCadence); return Task.CompletedTask; });
        driver.At(H(11), () => { driver.Open(phoneCadence); return Task.CompletedTask; });
        driver.At(H(11.5), () => { driver.Close(phoneCadence); return Task.CompletedTask; });

        // ~150 guardados al día (90 en la PC, 60 en la tablet) con ~400 filas; cada uno sincroniza 4 s después
        int rowsPushed = 0, saves = 0;
        async Task SaveAsync(Device device, int n)
        {
            var patients = await device.Inventory.GetPatientsAsync();
            var patient = patients[n % patients.Count];
            patient.Occupation = $"Ocupación {device.Settings.CurrentSettings.DeviceId} {n}";
            await device.Inventory.SavePatientAsync(patient);
            await device.Inventory.SaveAppointmentAsync(new PatientAppointment
            {
                PatientRecordId = patient.Id, Date = DateTime.Today.AddDays(1 + n % 20), Time = $"{8 + n % 10:00}:{n % 2 * 30:00}", Reason = "Control"
            });
            if (n % 3 == 0) await device.Inventory.AddConsultationNoteAsync(patient.Id, new PatientConsultation { ChiefComplaint = $"Nota {n}" });
            saves++;
            var at = driver.Now.AddSeconds(4);
            driver.At(at, async () =>
            {
                var result = await device.Sync.SyncAsync(isBackgroundAutoSync: true);
                Assert.IsTrue(result.Success, result.ErrorMessage);
                rowsPushed += result.RowsPushed;
            });
        }
        for (int i = 0; i < 90; i++)
        {
            int n = i;
            driver.At(H(0.05 + i * 9.9 / 90), () => SaveAsync(pc, n));
        }
        for (int i = 0; i < 60; i++)
        {
            int n = i;
            driver.At(H(1.05 + i * 5.9 / 60), () => SaveAsync(tablet, 1000 + n));
        }

        var watch = Stopwatch.StartNew();
        await driver.RunUntilAsync(H(24));
        watch.Stop();

        long reads = _server.BilledRowsRead - readsBefore, written = _server.BilledRowsWritten - writtenBefore;
        var summary = $"Un día: {reads:N0} lecturas, {written:N0} escrituras, {_server.RequestCount - requestsBefore:N0} peticiones; " +
                      $"{saves} guardados con {rowsPushed} filas subidas; sincronizaciones de la cadencia: " +
                      string.Join(", ", syncs.OrderBy(k => k.Key).Select(k => $"{k.Key} = {k.Value}")) + $" ({watch.Elapsed.TotalSeconds:0} s reales)";
        Console.WriteLine(summary);
        Assert.AreEqual(150, saves);
        Assert.IsLessThan(200_000L, reads, summary);
        Assert.IsLessThan(5_000L, written, summary);

        // Todo llegó: el celular, que abrió al final, quedó igual que la PC
        var pcPatients = (await pc.Inventory.GetPatientsAsync()).ToDictionary(p => p.SyncId, p => p.Occupation);
        var phonePatients = await phone.Inventory.GetPatientsAsync();
        Assert.HasCount(pcPatients.Count, phonePatients);
        foreach (var p in phonePatients) Assert.AreEqual(pcPatients[p.SyncId], p.Occupation, $"{p.FullName} igual en la PC y el celular");
    }

    [TestMethod]
    public async Task AGenerationChange_CausesExactlyOneFullDownloadPerDevice()
    {
        var clock = SimulatedClock();
        var pc = CreateDevice("pc", seed: true, clock);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false, clock);
        var phone = CreateDevice("phone", seed: false, clock);
        foreach (var d in new[] { tablet, phone })
        {
            var join = await d.Sync.JoinCloudAsync();
            Assert.IsTrue(join.Ok, join.Message);
            Assert.IsTrue((await d.Sync.SyncAsync()).Success);
        }
        var backup = new BackupService(pc.DbPath).CreateNamedBackup("prueba-paso-8-24");
        await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Registrado Después Del Respaldo", Age = 30 });
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        var cadences = new[] { (pc, CadenceOf(pc, inPatients: true)), (tablet, CadenceOf(tablet, inPatients: true)), (phone, CadenceOf(phone, inPatients: false)) };
        var driver = new CadenceDriver(clock);
        foreach (var (_, cadence) in cadences) driver.Open(cadence);
        await driver.RunUntilAsync(driver.Now.AddMinutes(1));

        await RestoreAsync(pc, backup);
        var replaced = await pc.Sync.ReplaceCloudWithLocalAsync();
        Assert.IsTrue(replaced.Ok, replaced.Message);
        var before = cadences.ToDictionary(c => c.Item1, c => c.Item1.Settings.GetQuotaSummary().FullPullsToday);

        // Media hora con la cadencia: la marca de cambios o la descarga de seguridad lo traen, y una sola vez
        await driver.RunUntilAsync(driver.Now.AddMinutes(30));
        foreach (var d in new[] { tablet, phone })
        {
            Assert.AreEqual(1, d.Settings.GetQuotaSummary().FullPullsToday - before[d], $"{d.Settings.CurrentSettings.DeviceId}: una descarga completa");
            Assert.AreEqual(TursoSyncStatus.Success, d.Sync.Status, d.Sync.StatusMessage);
            Assert.IsFalse((await d.Inventory.GetPatientsAsync()).Any(p => p.FullName == "Paciente Registrado Después Del Respaldo"), "Bajó la nube nueva");
        }
        Assert.AreEqual(0, pc.Settings.GetQuotaSummary().FullPullsToday - before[pc], "La PC que reemplazó la nube no la vuelve a bajar");
    }

    // ================================================================== refrescar con un formulario abierto

    [TestMethod]
    public async Task ASyncFromTheCadence_WithThePatientFormOpen_KeepsTheInstanceUntilItCloses()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");

        var records = await pc.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        records.PatientForm.EditPatient();
        Assert.IsTrue(records.PatientForm.IsPatientModalOpen);

        // La tablet cambia las alergias del mismo paciente y sube
        var onTablet = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == patient.SyncId);
        onTablet.Allergies = "Penicilina (de la tablet)";
        await tablet.Inventory.SavePatientAsync(onTablet);
        Assert.IsTrue((await tablet.Sync!.SyncAsync()).Success);

        // La revisión de la PC ve la marca de cambios y sincroniza; la lista se recarga
        var listBefore = records.Patients;
        var cadence = pc.Handoff!.Scheduler;
        var reasons = new List<string>();
        cadence.SyncRequested += reasons.Add;
        cadence.InPatientsSection = true;
        cadence.Begin();
        await cadence.TickAsync();
        CollectionAssert.AreEqual(new[] { SyncScheduler.ReasonPeerChanges }, reasons);
        await WaitUntilAsync(() => !ReferenceEquals(listBefore, records.Patients));
        Assert.AreEqual("Penicilina (de la tablet)", (await pc.Inventory.GetPatientByIdAsync(patient.Id))!.Allergies, "Bajó a la base");

        // Con el formulario abierto la instancia se queda (CanReplaceSelectedPatientInstance): guardar no pisa nada con una copia cambiada
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreSame(patient, records.Patients.Single(p => p.Id == patient.Id));
        Assert.AreNotEqual("Penicilina (de la tablet)", patient.Allergies);

        // Al cerrarlo se toma la de la base
        records.PatientForm.ClosePatientModal();
        await WaitUntilAsync(() => !ReferenceEquals(patient, records.SelectedPatient));
        Assert.AreEqual("Penicilina (de la tablet)", records.SelectedPatient!.Allergies);
        records.Dispose();
    }
}
