using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [TestMethod]
    public void TheDuplicatesBanner_AddsPairsOnce_AndCloses()
    {
        var home = new MedicalRecordsViewModel().Home;
        PatientRecord P(string name, string folio) => new() { SyncId = Guid.NewGuid(), FullName = name, RecordNumber = folio };
        var a = P("Juan Pérez", "EXP-2026-004");
        var b = P("JUAN PEREZ", "EXP-2026-T1-001");
        var c = P("Ana López", "EXP-2026-010");
        var d = P("Ana Lopez", "EXP-2026-P2-002");

        home.AddDuplicatePairs(new[] { new DuplicatePatientPair(a, b, DuplicatePatientRules.Match.BirthDate) });
        Assert.AreEqual("Posible expediente duplicado con lo que llegó de otro equipo: Juan Pérez (EXP-2026-004) y JUAN PEREZ (EXP-2026-T1-001), " +
                        "misma fecha de nacimiento. Revise las dos fichas: si son la misma persona, siga con una. TPVC no los une solo.",
            home.DuplicatePatientsNoticeText);

        // El mismo par al revés no se repite; uno nuevo va primero
        home.AddDuplicatePairs(new[] { new DuplicatePatientPair(b, a, DuplicatePatientRules.Match.BirthDate), new DuplicatePatientPair(c, d, DuplicatePatientRules.Match.Phone) });
        Assert.HasCount(2, home.DuplicatePairs);
        StringAssert.StartsWith(home.DuplicatePatientsNoticeText, "Posibles expedientes duplicados con lo que llegó de otro equipo: Ana López (EXP-2026-010) y Ana Lopez (EXP-2026-P2-002), mismo teléfono; ");

        home.DismissDuplicatePatientsNotice();
        Assert.IsFalse(home.HasDuplicatePatientsNotice);
        Assert.IsEmpty(home.DuplicatePairs);
    }
}
