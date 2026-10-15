using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.23 (H2, H3) en un solo equipo: las reglas de citas empalmadas y de expedientes duplicados (los mismos casos que sus
/// vectores, en los dos órdenes), la hora que la agenda lee con la misma regla, la marca "Empalme" y las preguntas antes de
/// agendar y al crear un expediente.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConflictWarningsTests
{
    private string _dbPath = string.Empty;
    private InventoryService _service = null!;
    private readonly List<(string Message, string Title)> _questions = new();
    private bool _answer;
    private Func<string, string, bool> _previousConfirm = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_conflicts_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        using (var db = new AppDbContext(builder.Options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(builder.Options));
        _previousConfirm = DialogService.ConfirmHandler;
        DialogService.ConfirmHandler = (message, title) =>
        {
            _questions.Add((message, title));
            return _answer;
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        DialogService.ConfirmHandler = _previousConfirm;
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private DbContextOptions<AppDbContext> Options()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        return builder.Options;
    }

    private async Task<MedicalRecordsViewModel> OpenRecordsAsync()
    {
        var records = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await records.LoadInitialDataAsync();
        return records;
    }

    // ================================================================== reglas

    [TestMethod]
    public void Overlaps_FollowTheVectorCases_InBothOrders()
    {
        foreach (var (name, a, b, expected) in GoldenVectorTests.OverlapPairs)
        {
            Assert.AreEqual(expected, AppointmentRules.Overlaps(a, b), name);
            Assert.AreEqual(expected, AppointmentRules.Overlaps(b, a), $"{name} (al revés)");
        }

        // Una cita nueva (sin SyncId todavía) contra otra nueva: sí se comparan
        var day = new DateTime(2026, 10, 15);
        Assert.IsTrue(AppointmentRules.Overlaps(new(Guid.Empty, day, "10:00", 30, null), new(Guid.Empty, day, "10:10", 30, null)));
    }

    [TestMethod]
    public void OverlapsWith_AndPairs_ComeInOrder()
    {
        var day = new DateTime(2026, 10, 15);
        AppointmentRules.Appointment A(int n, string time, int minutes = 30, string status = "Programada") =>
            new(new Guid($"c0000000-0000-4000-8000-{n:D12}"), day, time, minutes, status);
        var candidate = new AppointmentRules.Appointment(Guid.Empty, day, "10:00", 60, "Programada");
        var others = new[] { A(3, "10:45"), A(1, "10:30"), A(2, "10:30"), A(4, "11:00"), A(5, "10:15", status: "Cancelada") };

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, AppointmentRules.OverlapsWith(candidate, others).Select(o => o.SyncId.ToString()[^1] - '0').ToList(),
            "Por hora y luego por SyncId; las 11:00 tocan y la cancelada no cuenta");

        var pairs = AppointmentRules.Pairs(others.Append(A(6, "10:40")), new HashSet<Guid> { A(6, "x").SyncId });
        CollectionAssert.AreEqual(new[] { "1-6", "2-6", "6-3", "6-4" },
            pairs.Select(p => $"{p.First.SyncId.ToString()[^1]}-{p.Second.SyncId.ToString()[^1]}").ToList());
    }

    [TestMethod]
    public void TheAgendaReadsTheTime_WithTheSameRuleAsTheOverlaps()
    {
        foreach (var time in new[] { "09:00", "9:05", "10:30 AM", "9:15 p. m.", "12:30 am", "16:00:30", "3 PM", "7:5", "10.30", "mediodía", "", null })
        {
            Assert.AreEqual(PatientSummaryRules.TimeOfDay(time), AgendaPanelViewModel.ParseAppointmentTime(time), $"'{time}'");
        }
        // DateTime.TryParse leía "3 PM" como las 15:00; ahora es una hora que no se lee (las 10:00), como en el empalme
        Assert.AreEqual(new TimeSpan(10, 0, 0), AgendaPanelViewModel.ParseAppointmentTime("3 PM"));
    }

    [TestMethod]
    public void TheAgenda_MarksBothAppointmentsThatOverlap()
    {
        var day = new DateTime(2026, 10, 20);
        var appointments = new[]
        {
            new PatientAppointment { SyncId = Guid.NewGuid(), Date = day, Time = "10:00", DurationMinutes = 60, PatientName = "Ana López" },
            new PatientAppointment { SyncId = Guid.NewGuid(), Date = day, Time = "10:30", DurationMinutes = 0, PatientName = "Luis Rojas" },
            new PatientAppointment { SyncId = Guid.NewGuid(), Date = day, Time = "10:45", PatientName = "Eva Ruiz", Status = "Cancelada" },
            new PatientAppointment { SyncId = Guid.NewGuid(), Date = day, Time = "11:00", PatientName = "Sin empalme" },
        };

        var slots = AgendaPanelViewModel.BuildSlots(day, appointments, now: day.AddDays(-1)).Where(s => s.HasAppointment).ToList();

        Assert.AreEqual("Empalme con Luis Rojas (10:30)", slots.Single(s => s.Title == "Ana López").OverlapText);
        Assert.AreEqual("Empalme con Ana López (10:00)", slots.Single(s => s.Title == "Luis Rojas").OverlapText);
        Assert.IsFalse(slots.Single(s => s.Title == "Sin empalme").IsOverlapping, "Tocarse a las 11:00 no es empalme");
        Assert.HasCount(3, slots, "La cancelada no aparece");
    }

    [TestMethod]
    public void Duplicates_FollowTheVectorCases_InBothOrders()
    {
        foreach (var (name, a, b, expected) in GoldenVectorTests.DuplicatePairsCases)
        {
            Assert.AreEqual(expected, DuplicatePatientRules.Compare(a, b), name);
            Assert.AreEqual(expected, DuplicatePatientRules.Compare(b, a), $"{name} (al revés)");
        }

        Assert.AreEqual("maria del carmen lopez", DuplicatePatientRules.NormalizeName("  María  del\tCARMEN   López\n"));
        Assert.AreEqual("5512345678", DuplicatePatientRules.PhoneKey("+52 1 (55) 1234 5678") is { } key ? key : null);
        Assert.IsNull(DuplicatePatientRules.PhoneKey("55-1234-567"), "Nueve dígitos no se comparan");
    }

    // ================================================================== preguntas antes de guardar

    [TestMethod]
    public async Task SchedulingAnOverlappingAppointment_AsksFirst_AndOnlySavesIfConfirmed()
    {
        var records = await OpenRecordsAsync();
        var ana = records.Patients[0];
        var luis = records.Patients[1];
        var day = DateTime.Today.AddDays(3);
        await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = ana.Id, Date = day, Time = "10:30", DurationMinutes = 30, Reason = "Control" });

        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        async Task ScheduleAsync(string time, int minutes)
        {
            await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == time));
            records.Agenda.AgendaFormPatient = luis;
            records.Agenda.AgendaFormReason = "Primera vez";
            records.Agenda.AgendaFormDuration = minutes;
            await records.Agenda.SaveAgendaFormAsync();
        }

        // 10:00 por 60 minutos se cruza con la de las 10:30: se pregunta y, sin confirmar, no se guarda
        _answer = false;
        await ScheduleAsync("10:00", 60);
        Assert.AreEqual(($"Ya hay una cita a las 10:30 con {ana.FullName}.\n\n¿Agendar de todos modos?", "Cita empalmada"), _questions.Single());
        Assert.HasCount(1, await _service.GetAppointmentsByDayAsync(day));
        Assert.IsTrue(records.Agenda.AgendaSlots.Any(s => s.IsFormOpen), "El formulario sigue abierto para elegir otro horario");

        // Confirmada, se guarda y las dos llevan la marca
        _answer = true;
        await ScheduleAsync("10:00", 60);
        Assert.HasCount(2, _questions);
        Assert.HasCount(2, await _service.GetAppointmentsByDayAsync(day));
        await records.Agenda.LoadAgendaPanelAsync();
        Assert.IsTrue(records.Agenda.AgendaSlots.Where(s => s.HasAppointment).All(s => s.IsOverlapping));

        // Lo que no se cruza (11:00, justo cuando termina) no pregunta
        await ScheduleAsync("11:00", 30);
        Assert.HasCount(2, _questions);
        Assert.HasCount(3, await _service.GetAppointmentsByDayAsync(day));
    }

    [TestMethod]
    public async Task MovingAnAppointmentOntoAnother_AsksFirst_AndKeepsMovingIfNotConfirmed()
    {
        var records = await OpenRecordsAsync();
        var day = DateTime.Today.AddDays(3);
        var long1 = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = records.Patients[0].Id, Date = day, Time = "08:00", DurationMinutes = 60, Reason = "Control" });
        await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = records.Patients[1].Id, Date = day, Time = "10:45", DurationMinutes = 15, Reason = "Control" });

        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        records.Agenda.StartMovingAppointment(records.Agenda.AgendaSlots.Single(s => s.Appointment?.Id == long1.Id).Appointment);

        _answer = false;
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "10:00"));
        StringAssert.StartsWith(_questions.Single().Message, "Ya hay una cita a las 10:45 con ");
        Assert.IsTrue(records.Agenda.IsMovingAppointment, "Sigue eligiendo otro horario");
        Assert.AreEqual("08:00", (await _service.GetAppointmentsByDayAsync(day)).Single(a => a.Id == long1.Id).Time);

        // Moverla a un hueco que no se cruza (y sobre su propio horario) no pregunta
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "09:00"));
        Assert.HasCount(1, _questions);
        Assert.AreEqual("09:00", (await _service.GetAppointmentsByDayAsync(day)).Single(a => a.Id == long1.Id).Time);
    }

    private static void FillNewPatient(PatientFormViewModel form, string first, string last, string birth, string phone = "")
    {
        form.FormFirstName = first;
        form.FormLastName = last;
        form.FormBirthDateText = birth;
        form.FormGender = "Masculino";
        form.FormPhone = phone;
        form.SetAllergiesDenied();
    }

    [TestMethod]
    public async Task CreatingAPatientThatLooksLikeAnExistingOne_AsksFirst()
    {
        var existing = await _service.SavePatientAsync(new PatientRecord { FullName = "Ramón Íñiguez Tello", BirthDate = new DateTime(1990, 2, 1), Phone = "5512345678" });
        var records = await OpenRecordsAsync();
        var count = (await _service.GetPatientsAsync()).Count;

        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "RAMON", "INIGUEZ  TELLO", "01/02/1990");
        _answer = false;
        await records.PatientForm.SavePatientModalAsync();

        var (message, title) = _questions.Single();
        Assert.AreEqual("Posible paciente duplicado", title);
        StringAssert.StartsWith(message, $"Ya hay un expediente parecido:\n\n• Ramón Íñiguez Tello · {existing.RecordNumber} · misma fecha de nacimiento\n\n");
        StringAssert.EndsWith(message, "¿Crear el expediente nuevo de todos modos?");
        Assert.IsTrue(records.PatientForm.IsPatientModalOpen, "Sin confirmar, el formulario sigue abierto con lo capturado");
        Assert.AreEqual("RAMON", records.PatientForm.FormFirstName);
        Assert.HasCount(count, await _service.GetPatientsAsync());

        _answer = true;
        await records.PatientForm.SavePatientModalAsync();
        Assert.IsFalse(records.PatientForm.IsPatientModalOpen);
        Assert.HasCount(count + 1, await _service.GetPatientsAsync(), "Confirmado, se crea");

        // Otro nombre no pregunta; editar uno que ya existe tampoco
        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "Ramona", "Íñiguez Tello", "01/02/1990", "55 1234 5678");
        await records.PatientForm.SavePatientModalAsync();
        Assert.HasCount(2, _questions);
        Assert.HasCount(count + 2, await _service.GetPatientsAsync());
    }

    // ================================================================== avisos que se quedan (decisión del médico, 2026-10-14)

    /// <summary>Dos citas que se empalman mañana y un posible duplicado, guardados de verdad (los avisos se depuran contra la base).</summary>
    private async Task<(SyncArrivals Arrivals, PatientAppointment A, PatientAppointment B, PatientRecord P, PatientRecord Q)> ConflictsInTheDatabaseAsync()
    {
        var patients = await _service.GetPatientsAsync();
        var day = DateTime.Today.AddDays(2);
        var a = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patients[0].Id, Date = day, Time = "10:30", DurationMinutes = 30, Reason = "Control" });
        var b = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patients[1].Id, Date = day, Time = "10:45", DurationMinutes = 30, Reason = "Control" });
        var p = await _service.SavePatientAsync(new PatientRecord { FullName = "Ixchel Hernández Tun", BirthDate = new DateTime(1980, 5, 4), Phone = "5511112222" });
        var q = await _service.SavePatientAsync(new PatientRecord { FullName = "IXCHEL HERNANDEZ TUN", BirthDate = new DateTime(1980, 5, 4) });
        var arrivals = new SyncArrivals(new[] { new AppointmentOverlap(a, b) }, new[] { new DuplicatePatientPair(p, q, DuplicatePatientRules.Match.BirthDate) });
        return (arrivals, a, b, p, q);
    }

    [TestMethod]
    public async Task TheNotices_StayAfterARestart_UntilDismissed_AndADismissedOneDoesNotComeBack()
    {
        var (arrivals, a, b, p, q) = await ConflictsInTheDatabaseAsync();
        Assert.IsTrue(await _service.AddSyncNoticesAsync(arrivals));
        Assert.IsFalse(await _service.AddSyncNoticesAsync(arrivals), "El mismo par no se agrega dos veces");

        var first = await OpenRecordsAsync();
        Assert.HasCount(1, first.Home.OverlapPairs);
        Assert.HasCount(1, first.Home.DuplicatePairs);
        StringAssert.StartsWith(first.Home.OverlapNoticeText, "Empalme en la agenda con lo que llegó de otro equipo: ");
        StringAssert.Contains(first.Home.OverlapNoticeText, $"{a.PatientName} (10:30) y {b.PatientName} (10:45)");
        StringAssert.Contains(first.Home.DuplicatePatientsNoticeText, $"Ixchel Hernández Tun ({p.RecordNumber})");
        StringAssert.Contains(first.Home.DuplicatePatientsNoticeText, $"IXCHEL HERNANDEZ TUN ({q.RecordNumber}), misma fecha de nacimiento".Split(", ")[0]);
        StringAssert.Contains(first.Home.DuplicatePatientsNoticeText, "misma fecha de nacimiento");

        // Cerrar y abrir TPVC: se quedan
        var second = await OpenRecordsAsync();
        Assert.IsTrue(second.Home.HasOverlapNotice && second.Home.HasDuplicatePatientsNotice, "Siguen después de reiniciar");

        // «Entendido» en el de empalmes: no vuelve, ni al reiniciar ni si el mismo empalme vuelve a llegar
        await second.Home.DismissOverlapNoticeAsync();
        Assert.IsFalse(second.Home.HasOverlapNotice);
        Assert.IsFalse(await _service.AddSyncNoticesAsync(new SyncArrivals(arrivals.Overlaps, Array.Empty<DuplicatePatientPair>())));
        var third = await OpenRecordsAsync();
        Assert.IsFalse(third.Home.HasOverlapNotice);
        Assert.IsTrue(third.Home.HasDuplicatePatientsNotice, "El otro aviso sigue");

        // Un conflicto nuevo sí avisa: la misma pareja de citas en otro horario
        b.Time = "10:40";
        await _service.SaveAppointmentAsync(b);
        var moved = (await _service.GetAppointmentsByDayAsync(a.Date)).ToDictionary(x => x.SyncId);
        Assert.IsTrue(await _service.AddSyncNoticesAsync(new SyncArrivals(new[] { new AppointmentOverlap(moved[a.SyncId], moved[b.SyncId]) }, Array.Empty<DuplicatePatientPair>())));
        await third.Home.LoadSyncNoticesAsync();
        StringAssert.Contains(third.Home.OverlapNoticeText, $"{b.PatientName} (10:40)");

        // El de duplicados descartado no vuelve aunque el par vuelva a llegar
        await third.Home.DismissDuplicatePatientsNoticeAsync();
        Assert.IsFalse(await _service.AddSyncNoticesAsync(new SyncArrivals(Array.Empty<AppointmentOverlap>(), arrivals.DuplicatePatients)));
        Assert.IsFalse((await OpenRecordsAsync()).Home.HasDuplicatePatientsNotice);
    }

    [TestMethod]
    public async Task TheNotices_ArePruned_WhenTheyNoLongerApply()
    {
        var (arrivals, a, _, p, q) = await ConflictsInTheDatabaseAsync();
        await _service.AddSyncNoticesAsync(arrivals);

        // Una cita cancelada ya no se empalma
        await _service.UpdateAppointmentStatusAsync(a.Id, "Cancelada");
        Assert.IsEmpty((await _service.GetSyncNoticesAsync()).Overlaps);

        // Un expediente unido a otro (8.A) o dado de baja ya no es posible duplicado
        var merged = (await _service.GetPatientsAsync()).Single(x => x.Id == q.Id);
        merged.MergedIntoSyncId = p.SyncId;
        await _service.SavePatientAsync(merged);
        Assert.IsEmpty((await _service.GetSyncNoticesAsync()).DuplicatePatients);

        // Un empalme de un día que ya pasó y uno de citas que ya no existen
        var yesterday = DateTime.Today.AddDays(-1);
        var patients = await _service.GetPatientsAsync();
        var old1 = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patients[0].Id, Date = yesterday, Time = "09:00", Reason = "Control" });
        var old2 = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patients[1].Id, Date = yesterday, Time = "09:00", Reason = "Control" });
        await _service.AddSyncNoticesAsync(new SyncArrivals(new[] { new AppointmentOverlap(old1, old2) }, Array.Empty<DuplicatePatientPair>()));
        var ghost = new PatientAppointment { SyncId = Guid.NewGuid(), Date = DateTime.Today.AddDays(3), Time = "09:00" };
        var ghost2 = new PatientAppointment { SyncId = Guid.NewGuid(), Date = DateTime.Today.AddDays(3), Time = "09:00" };
        await _service.AddSyncNoticesAsync(new SyncArrivals(new[] { new AppointmentOverlap(ghost, ghost2) }, Array.Empty<DuplicatePatientPair>()));
        Assert.IsTrue((await _service.GetSyncNoticesAsync()).IsEmpty);

        // Y la base ya no los guarda
        using var db = new AppDbContext(Options());
        Assert.IsNull(DbMeta.Get(db, SyncNotices.MetaKey));
    }

    [TestMethod]
    public void TheNoticeTexts_ListTheMostRecentPairs()
    {
        var home = new MedicalRecordsViewModel().Home;
        PatientRecord P(string name, string folio) => new() { SyncId = Guid.NewGuid(), FullName = name, RecordNumber = folio };
        home.ShowSyncNotices(new SyncArrivals(Array.Empty<AppointmentOverlap>(), new[]
        {
            new DuplicatePatientPair(P("Ana López", "EXP-2026-010"), P("Ana Lopez", "EXP-2026-P2-002"), DuplicatePatientRules.Match.Phone),
            new DuplicatePatientPair(P("Juan Pérez", "EXP-2026-004"), P("JUAN PEREZ", "EXP-2026-T1-001"), DuplicatePatientRules.Match.BirthDate)
        }));
        Assert.AreEqual("Posibles expedientes duplicados con lo que llegó de otro equipo: Ana López (EXP-2026-010) y Ana Lopez (EXP-2026-P2-002), mismo teléfono; " +
                        "Juan Pérez (EXP-2026-004) y JUAN PEREZ (EXP-2026-T1-001), misma fecha de nacimiento. Revise las dos fichas: si son la misma persona, " +
                        "siga con una. TPVC no los une solo.", home.DuplicatePatientsNoticeText);
        Assert.IsFalse(home.HasOverlapNotice);
    }

    // ================================================================== lo que se cancela o se recarga mientras se espera a la nube

    /// <summary>Una sincronización rápida que espera hasta que la prueba la suelta.</summary>
    private sealed class HeldQuickSync : IQuickSync
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuickSyncOutcome> QuickSyncAsync(CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task;
            return QuickSyncOutcome.Synced;
        }
    }

    private async Task<(MedicalRecordsViewModel Records, DateTime Day)> AgendaWithFormAsync(string time, int minutes)
    {
        var records = await OpenRecordsAsync();
        var day = DateTime.Today.AddDays(3);
        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == time));
        records.Agenda.AgendaFormPatient = records.Patients[1];
        records.Agenda.AgendaFormReason = "Primera vez";
        records.Agenda.AgendaFormDuration = minutes;
        return (records, day);
    }

    [TestMethod]
    public async Task ClosingTheAgendaFormWhileWaitingForTheCloud_DoesNotSchedule()
    {
        var (records, day) = await AgendaWithFormAsync("10:00", 30);
        var held = new HeldQuickSync();
        _service.QuickSync = held;

        var saving = records.Agenda.SaveAgendaFormAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        records.Agenda.CloseAgendaForm();
        held.Release.SetResult();
        await saving;

        Assert.IsEmpty(await _service.GetAppointmentsByDayAsync(day), "El médico cerró el formulario mientras se esperaba: no se agenda");
        Assert.IsEmpty(_questions);
    }

    [TestMethod]
    public async Task CancellingAMoveWhileWaitingForTheCloud_DoesNotMove()
    {
        var records = await OpenRecordsAsync();
        var day = DateTime.Today.AddDays(3);
        var original = await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = records.Patients[0].Id, Date = day, Time = "08:00", Reason = "Control" });
        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        records.Agenda.StartMovingAppointment(records.Agenda.AgendaSlots.Single(s => s.Appointment?.Id == original.Id).Appointment);
        var held = new HeldQuickSync();
        _service.QuickSync = held;

        var moving = records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "12:00"));
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        records.Agenda.CancelMovingAppointment();
        held.Release.SetResult();
        await moving;

        Assert.AreEqual("08:00", (await _service.GetAppointmentsByDayAsync(day)).Single().Time, "Se canceló el cambio mientras se esperaba: no se mueve");
    }

    [TestMethod]
    public async Task ClosingThePatientFormWhileWaitingForTheCloud_DoesNotCreateThePatient()
    {
        var records = await OpenRecordsAsync();
        var count = (await _service.GetPatientsAsync()).Count;
        var held = new HeldQuickSync();
        _service.QuickSync = held;

        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "Teodoro", "Salas Ruiz", "03/03/1977");
        var saving = records.PatientForm.SavePatientModalAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _answer = true; // «¿Salir sin guardar el paciente nuevo?»
        records.PatientForm.CancelPatientForm();
        held.Release.SetResult();
        await saving;

        Assert.HasCount(count, await _service.GetPatientsAsync(), "Se cerró el formulario mientras se esperaba: no se crea");
        Assert.IsFalse(records.PatientForm.IsPatientModalOpen);
    }

    [TestMethod]
    public async Task EditingAPatient_DoesNotWaitForTheCloud()
    {
        var records = await OpenRecordsAsync();
        var held = new HeldQuickSync();
        _service.QuickSync = held;
        records.OpenPatientChart(records.Patients[0]);
        records.PatientForm.EditPatient();
        records.PatientForm.FormOccupation = "Docente";

        await records.PatientForm.SavePatientModalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(held.Started.Task.IsCompleted, "Solo crear un expediente sincroniza antes");
    }

    [TestMethod]
    public async Task AReloadWhileWaiting_KeepsTheFormOpen_WhenItsSlotIsStillFree()
    {
        var (records, day) = await AgendaWithFormAsync("10:00", 60);
        var held = new HeldQuickSync();
        _service.QuickSync = held;
        _answer = false;

        var saving = records.Agenda.SaveAgendaFormAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Lo que bajó la sincronización rápida: otra cita a las 10:30, y la agenda se recarga
        await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = records.Patients[0].Id, Date = day, Time = "10:30", Reason = "Control" });
        await records.Agenda.LoadAgendaPanelAsync();
        held.Release.SetResult();
        await saving;

        StringAssert.StartsWith(_questions.Single().Message, "Ya hay una cita a las 10:30 con ");
        var open = records.Agenda.AgendaSlots.Single(s => s.IsFormOpen);
        Assert.AreEqual("10:00", open.TimeText, "El hueco sigue libre: el formulario sigue abierto ahí");
        Assert.AreEqual(records.Patients[1].Id, records.Agenda.AgendaFormPatient?.Id);
        Assert.AreEqual("Primera vez", records.Agenda.AgendaFormReason);
        Assert.AreEqual(60, records.Agenda.AgendaFormDuration);
    }

    [TestMethod]
    public async Task AReloadThatTakesTheSlot_KeepsWhatWasTyped_ForTheNextSlot()
    {
        var (records, day) = await AgendaWithFormAsync("10:00", 45);
        var held = new HeldQuickSync();
        _service.QuickSync = held;
        _answer = false;

        var saving = records.Agenda.SaveAgendaFormAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = records.Patients[0].Id, Date = day, Time = "10:00", Reason = "Control" });
        await records.Agenda.LoadAgendaPanelAsync();
        held.Release.SetResult();
        await saving;

        Assert.IsFalse(records.Agenda.AgendaSlots.Any(s => s.IsFormOpen), "El hueco ya no está libre");
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "11:00"));
        Assert.AreEqual(records.Patients[1].Id, records.Agenda.AgendaFormPatient?.Id, "Lo escrito pasa al siguiente hueco");
        Assert.AreEqual("Primera vez", records.Agenda.AgendaFormReason);
        Assert.AreEqual(45, records.Agenda.AgendaFormDuration);

        // Cerrar la agenda lo olvida
        records.Agenda.CloseAgendaPanel();
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "12:00"));
        Assert.AreEqual(string.Empty, records.Agenda.AgendaFormReason);
        Assert.AreEqual(30, records.Agenda.AgendaFormDuration);
    }

    // ================================================================== horas escritas a mano en v1.7–v1.13

    [TestMethod]
    public void TheOldReader_ReadsTheHandWrittenTimes_ThatTheRuleDoesNot()
    {
        foreach (var (text, expected) in new[]
                 {
                     ("5 pm", "17:00"), ("12 PM", "12:00"), ("4.30 pm", "16:30"), ("16:30 PM", "16:30"), ("7:5", "07:05"),
                     // Punto, coma o espacio entre hora y minutos: H:mm (antes el lector los tomaba como fecha y daba 00:00)
                     ("10.30", "10:30"), ("4.30", "04:30"), ("9.15", "09:15"), ("12.30", "12:30"), ("13.12", "13:12"), ("9,30", "09:30"),
                     ("10 30", "10:30"), ("10.30 am", "10:30"), ("12.30 p. m.", "12:30")
                 })
        {
            Assert.IsFalse(PatientSummaryRules.TryTimeOfDay(text, out _), $"'{text}': la regla no la lee");
            Assert.AreEqual(expected, AppointmentTimeMigration.LegacyTimeOfDay(text) is { } t ? AppointmentTimeMigration.Format(t) : null, text);
        }
        // Lo que el lector de antes toma como fecha no es una hora: se deja como está (nunca "00:00")
        foreach (var text in new[] { "mediodía", "10/30", "10-30", "9.5", "10.75", "25.10" })
        {
            Assert.IsNull(AppointmentTimeMigration.LegacyTimeOfDay(text), text);
        }
        Assert.IsTrue(PatientSummaryRules.TryTimeOfDay("09:15 AM", out var nine) && nine == new TimeSpan(9, 15, 0));
    }

    [TestMethod]
    public async Task WithoutTheCloud_TheTimesAreNormalizedWhenTheDatabaseOpens_AndWithTheCloudOnlyAfterAPull()
    {
        var patient = (await _service.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(3);
        foreach (var time in new[] { "5 pm", "10.30", "10/30" })
        {
            await _service.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patient.Id, Date = day, Time = time, Reason = "Control" });
        }
        using var db = new AppDbContext(Options());
        Assert.IsNull(DbMeta.Get(db, AppointmentTimeMigration.DoneKey), "Abrir la base ya no la normaliza por su cuenta");

        // Con la nube activa, al abrir no se toca nada ni se marca como hecho
        Assert.AreEqual(0, AppointmentTimeMigration.RunWithoutCloud(db, cloudActive: true));
        Assert.IsNull(DbMeta.Get(db, AppointmentTimeMigration.DoneKey));

        // Sin nube, sí, una sola vez
        Assert.AreEqual(2, AppointmentTimeMigration.RunWithoutCloud(db, cloudActive: false));
        CollectionAssert.AreEquivalent(new[] { "17:00", "10:30", "10/30" }, (await _service.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList());
        Assert.AreEqual("1", DbMeta.Get(db, AppointmentTimeMigration.DoneKey));
        Assert.AreEqual(0, AppointmentTimeMigration.RunWithoutCloud(db, cloudActive: false));
    }

    [TestMethod]
    public async Task ADismissedOverlapOfADayThatPassed_IsForgotten_AndADismissedDuplicateIsNot()
    {
        var (arrivals, a, _, _, _) = await ConflictsInTheDatabaseAsync();
        await _service.AddSyncNoticesAsync(arrivals);
        await _service.DismissSyncNoticesAsync(SyncNoticeKind.Overlaps);
        await _service.DismissSyncNoticesAsync(SyncNoticeKind.DuplicatePatients);
        var factory = new AppDbContextFactory(Options());
        string? Stored()
        {
            using var db = factory.CreateDbContext();
            return DbMeta.Get(db, SyncNotices.MetaKey);
        }
        StringAssert.Contains(Stored(), "empalme:");

        // El día del empalme ya pasó: su llave descartada se olvida; la del duplicado se queda
        await SyncNotices.LoadAsync(factory, a.Date.AddDays(1));
        Assert.IsFalse(Stored()!.Contains("empalme:", StringComparison.Ordinal), "La llave descartada de un día que pasó se olvida");
        StringAssert.Contains(Stored(), "duplicado:");
    }

    [TestMethod]
    public async Task ClosingTheFormWhileWaiting_OnSaveAndStartConsultation_DoesNotStartAConsultationForThePatientOpenBefore()
    {
        var records = await OpenRecordsAsync();
        records.OpenPatientChart(records.Patients[0]);
        var held = new HeldQuickSync();
        _service.QuickSync = held;

        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "Teodoro", "Salas Ruiz", "03/03/1977");
        var saving = records.PatientForm.SavePatientAndStartConsultationAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _answer = true; // «¿Salir sin guardar el paciente nuevo?»
        records.PatientForm.CancelPatientForm();
        held.Release.SetResult();
        await saving;

        Assert.IsFalse(records.Active.HasActiveConsultation, "No se guardó nada: no se abre una consulta del paciente que estaba abierto");
    }
}
