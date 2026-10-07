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
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Sección Pacientes v1.14: inicio con búsqueda, ficha del paciente (lo esencial, antecedentes
/// según el paciente e historial único), alta por secciones y agenda como panel lateral.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PacientesTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_pacientes_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        _options = builder.Options;

        using (var db = new AppDbContext(_options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(_options));
        DialogService.ConfirmHandler = (_, _) => true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private async Task<(MedicalRecordsViewModel Records, PosViewModel Pos, ConsultationService Consultations)> CreateRecordsAsync()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var pos = new PosViewModel(_service, consultations);
        var records = new MedicalRecordsViewModel(_service, consultations, pos);
        await records.LoadInitialDataAsync();
        return (records, pos, consultations);
    }

    private static PatientRecord Patient(string name, string record = "", string phone = "", string diagnosis = "") => new()
    {
        FullName = name,
        RecordNumber = record,
        Phone = phone,
        LastDiagnosis = diagnosis
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    // ------------------------------------------------------------------ búsqueda

    [TestMethod]
    public void RankPatients_PrefixThenWordThenContains_IgnoringAccents()
    {
        var patients = new[]
        {
            Patient("Ramírez Ana", "EXP-001"),
            Patient("Ana María López", "EXP-002"),
            Patient("Juliana Pérez", "EXP-003"),
            Patient("Carlos Anaya", "EXP-004"),
            Patient("Pedro Gómez", "EXP-005", diagnosis: "Fisura anal"),
            Patient("Luis Torres", "EXP-006")
        };

        var result = MedicalRecordsViewModel.RankPatients(patients, "ana").Select(p => p.FullName).ToList();

        Assert.AreEqual("Ana María López", result[0], "Primero quien empieza así");
        CollectionAssert.AreEquivalent(new[] { "Carlos Anaya", "Ramírez Ana" }, result.Skip(1).Take(2).ToList(), "Luego una palabra que empieza así");
        CollectionAssert.AreEquivalent(new[] { "Juliana Pérez", "Pedro Gómez" }, result.Skip(3).ToList(), "Al final quien lo contiene (nombre o diagnóstico)");
        Assert.HasCount(5, result);

        Assert.AreEqual("Pedro Gómez", MedicalRecordsViewModel.RankPatients(patients, "GOMEZ").Single().FullName, "Sin acentos ni mayúsculas");
        Assert.AreEqual("Luis Torres", MedicalRecordsViewModel.RankPatients(patients, "exp-006").Single().FullName, "Por folio");
        Assert.IsEmpty(MedicalRecordsViewModel.RankPatients(patients, "   "));
    }

    [TestMethod]
    public void RankPatients_FindsByPhoneDigits_OnlyWithFourOrMore()
    {
        var patients = new[] { Patient("Elena Ruiz", phone: "55 1234 5678"), Patient("Mario Díaz", phone: "3312345678") };

        Assert.AreEqual("Elena Ruiz", MedicalRecordsViewModel.RankPatients(patients, "5512345678").Single().FullName);
        Assert.AreEqual("Elena Ruiz", MedicalRecordsViewModel.RankPatients(patients, "5678").First().FullName);
        Assert.IsEmpty(MedicalRecordsViewModel.RankPatients(patients, "567"), "Con menos de 4 dígitos no busca en teléfonos");
    }

    [TestMethod]
    public async Task HomeSearch_EnterOpensFirstResult_AndEscClears()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        Assert.IsTrue(records.IsHomePage);
        Assert.IsFalse(records.HasHomeQuery);
        Assert.IsNotEmpty(records.RecentPatients, "El inicio muestra pacientes recientes");

        var target = records.Patients.First();
        var firstWord = target.FullName.Split(' ')[0];
        records.HomeSearchText = firstWord;

        Assert.IsTrue(records.HasHomeQuery);
        Assert.IsNotEmpty(records.HomeResults);
        Assert.IsFalse(records.HasNoHomeResults);

        var expected = records.HomeResults[0];
        records.OpenFirstHomeResult();
        Assert.IsTrue(records.IsChartPage);
        Assert.AreEqual(expected.Id, records.SelectedPatient?.Id);

        records.HomeSearchText = "zzzz-nadie";
        Assert.IsTrue(records.HasNoHomeResults);
        Assert.AreEqual("Crear paciente \"zzzz-nadie\"", records.CreateFromSearchText);
    }

    [TestMethod]
    public async Task CreatePatientFromSearch_PrefillsNameFromWhatWasTyped()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        records.HomeSearchText = "maría lópez garcía";

        await records.CreatePatientFromSearchAsync();

        Assert.IsTrue(records.IsFormPage, "Abre el alta como página");
        Assert.IsTrue(records.IsFormNew);
        Assert.AreEqual("María", records.FormFirstName);
        Assert.AreEqual("López", records.FormLastName);
        Assert.AreEqual("García", records.FormMaternalLastName);
        Assert.AreEqual(string.Empty, records.HomeSearchText);

        // Un folio o teléfono no se usa como nombre
        records.CancelPatientForm();
        records.HomeSearchText = "EXP-123";
        await records.CreatePatientFromSearchAsync();
        Assert.AreEqual(string.Empty, records.FormFirstName);
    }

    // ------------------------------------------------------------------ navegación

    [TestMethod]
    public async Task ChartPrimaryAction_StartsConsultation_AndBannerLeadsBack()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        var patient = records.Patients.First();

        records.OpenPatientChart(patient);
        Assert.IsTrue(records.IsChartPage);
        Assert.IsTrue(records.ShowsPatientsTopBar);
        Assert.AreEqual("Iniciar consulta", records.ChartPrimaryActionText);

        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.IsConsultationPage);
        Assert.IsFalse(records.ShowsPatientsTopBar, "La consulta ocupa toda la pantalla");
        Assert.IsFalse(records.ShowsConsultationBanner);

        // Consultar el expediente durante la consulta no la cierra
        records.ShowChartFromConsultation();
        Assert.IsTrue(records.IsChartPage);
        Assert.IsTrue(records.ShowsConsultationBanner);
        StringAssert.Contains(records.ConsultationBannerText, patient.FullName);
        Assert.AreEqual("Volver a la consulta", records.ChartPrimaryActionText);

        // Ir al inicio y volver a entrar regresa a la consulta en curso
        records.GoToPatientsHome();
        Assert.IsTrue(records.ShowsConsultationBanner);
        records.EnterPatientsSection();
        Assert.IsTrue(records.IsConsultationPage);

        // Descartar la consulta deja la ficha del paciente
        await records.DiscardActiveConsultationAsync();
        Assert.IsFalse(consultations.HasActiveConsultation);
        Assert.IsTrue(records.IsChartPage);
        Assert.IsFalse(records.ShowsConsultationBanner);
    }

    [TestMethod]
    public async Task ChartOfAnotherPatient_DuringConsultation_DoesNotOfferToStartAnother()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        var first = records.Patients[0];
        var second = records.Patients[1];

        records.OpenPatientChart(first);
        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);

        records.GoToPatientsHome();
        records.OpenPatientChart(second);
        Assert.IsTrue(records.IsChartPage);
        Assert.IsFalse(records.IsConsultationForSelectedPatient);
        Assert.IsTrue(records.ShowsConsultationBanner, "El aviso recuerda la consulta en curso");

        records.ChartPrimaryAction();
        Assert.AreEqual(first.Id, consultations.CurrentConsultation?.PatientId, "No se reemplaza la consulta en curso");
    }

    [TestMethod]
    public async Task NewPatient_OpensFormPage_AndCancelReturns()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();

        await records.NewPatientAsync();
        Assert.IsTrue(records.IsFormPage);
        Assert.AreEqual("Nuevo paciente", records.FormPageTitle);
        Assert.AreEqual("Identificacion", records.FormOpenSection);

        records.CancelPatientForm();
        Assert.IsFalse(records.IsFormPage);
        Assert.IsTrue(records.IsHomePage);
    }

    [TestMethod]
    public async Task SavePatientAndStartConsultation_CreatesPatientAndOpensConsultation()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        await records.NewPatientAsync();
        records.FormFirstName = "Sofía";
        records.FormLastName = "Hernández";
        records.FormBirthDateText = DateTime.Today.AddYears(-34).ToString("dd/MM/yyyy");
        records.FormGender = "Femenino";
        records.SetAllergiesDenied();

        await records.SavePatientAndStartConsultationAsync();

        Assert.IsNull(records.FormErrorMessage);
        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.IsConsultationPage);
        Assert.AreEqual("Sofía Hernández", records.SelectedPatient?.FullName);
    }

    [TestMethod]
    public async Task FormValidationError_OpensTheSectionWithTheMissingData()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.NewPatientAsync();
        records.OpenFormSection("Notas");

        await records.SavePatientModalAsync();

        Assert.IsNotNull(records.FormErrorMessage);
        Assert.AreEqual("Identificacion", records.FormOpenSection, "Lleva al dato que falta");
        Assert.IsTrue(records.IsFormPage, "No sale del alta");
    }

    [TestMethod]
    public async Task FormSections_OnlyOfferWhatAppliesToThePatient()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.NewPatientAsync();

        records.FormGender = "Masculino";
        records.FormBirthDateText = DateTime.Today.AddYears(-40).ToString("dd/MM/yyyy");
        var keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Gineco", keys);
        Assert.DoesNotContain("Desarrollo", keys);

        records.FormGender = "Femenino";
        keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.Contains("Gineco", keys);
        Assert.DoesNotContain("Desarrollo", keys);

        records.FormBirthDateText = DateTime.Today.AddYears(-4).ToString("dd/MM/yyyy");
        keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Gineco", keys);
        Assert.Contains("Desarrollo", keys);

        // Numeración continua aunque falte una sección
        var numbers = records.FormSections.Select(s => s.Number).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, numbers.Count).Select(n => n.ToString()).ToList(), numbers);
        StringAssert.Contains(records.FormProgressText, "obligatoria");
    }

    // ------------------------------------------------------------------ ficha

    [TestMethod]
    public void BuildAntecedentSections_DependOnSexAndAge()
    {
        var man = new PatientRecord { FullName = "Juan", Gender = "Masculino", BirthDate = DateTime.Today.AddYears(-45), FamilyHistory = "Padre con diabetes" };
        var woman = new PatientRecord { FullName = "Ana", Gender = "Femenino", BirthDate = DateTime.Today.AddYears(-30), PregnanciesCount = 2, CesareansCount = 1 };
        var child = new PatientRecord { FullName = "Leo", Gender = "Masculino", BirthDate = DateTime.Today.AddYears(-5), ImmunizationSchedule = "Completo" };

        var manKeys = MedicalRecordsViewModel.BuildAntecedentSections(man).Select(s => s.Key).ToList();
        CollectionAssert.AreEqual(new[] { "Heredofamiliares", "Patologicos", "NoPatologicos", "Contacto" }, manKeys);
        var family = MedicalRecordsViewModel.BuildAntecedentSections(man)[0];
        Assert.AreEqual("Padre con diabetes", family.Summary);
        Assert.IsFalse(family.IsEmpty);

        var womanSections = MedicalRecordsViewModel.BuildAntecedentSections(woman);
        var gyneco = womanSections.Single(s => s.Key == "Gineco");
        StringAssert.Contains(gyneco.Summary, "G2 C1 A0");
        Assert.IsTrue(womanSections.Single(s => s.Key == "Heredofamiliares").IsEmpty);
        Assert.AreEqual("Sin registrar", womanSections.Single(s => s.Key == "Heredofamiliares").Summary);

        var childSections = MedicalRecordsViewModel.BuildAntecedentSections(child);
        Assert.IsFalse(childSections.Any(s => s.Key == "Gineco"));
        StringAssert.Contains(childSections.Single(s => s.Key == "Desarrollo").Detail, "Vacunación: Completo");
        Assert.IsFalse(childSections.Single(s => s.Key == "Patologicos").Detail.Contains("Vacunación"), "La vacunación del niño va en Desarrollo");
    }

    [TestMethod]
    public void BuildTimeline_MergesConsultationsAndAppointments_NewestFirst()
    {
        var older = new PatientConsultation
        {
            Folio = "CNS-1",
            Date = new DateTime(2026, 9, 1, 10, 0, 0),
            ChiefComplaint = "Tos",
            Diagnosis = "J00 Rinofaringitis",
            VitalSigns = "T/A: 120/80 | Peso: 70 kg"
        };
        var newer = new PatientConsultation
        {
            Folio = "CNS-2",
            Date = new DateTime(2026, 10, 1, 11, 0, 0),
            ChiefComplaint = "Control",
            Prescriptions = new List<PatientPrescriptionItem> { new() { MedicineName = "Paracetamol", Dosage = "1 cada 8 h" } }
        };
        var appointment = new PatientAppointment { Date = new DateTime(2026, 10, 15), Time = "09:30", Reason = "Seguimiento", Status = "Programada" };

        var timeline = MedicalRecordsViewModel.BuildTimeline(new[] { older, newer }, new[] { appointment });

        Assert.AreEqual(TimelineKind.Appointment, timeline[0].Kind, "La cita futura arriba");
        Assert.AreEqual("Seguimiento", timeline[0].Title);
        Assert.AreEqual(TimelineKind.Consultation, timeline[1].Kind);
        Assert.AreEqual("Control", timeline[1].Title, "Sin diagnóstico, el título es el motivo");
        Assert.AreEqual(TimelineKind.Prescription, timeline[2].Kind);
        StringAssert.Contains(timeline[2].Detail, "Paracetamol: 1 cada 8 h");
        Assert.AreEqual("J00 Rinofaringitis", timeline[3].Title, "Con diagnóstico, el título es el diagnóstico");
        Assert.AreEqual(TimelineKind.Vitals, timeline[4].Kind);
        Assert.AreEqual("T/A: 120/80 · Peso: 70 kg", timeline[4].Detail);
        Assert.HasCount(5, timeline);
    }

    [TestMethod]
    public void BuildLatestVitals_ShowsLastValueAndTrend()
    {
        var points = new[]
        {
            new VitalSignsEvolutionPoint { Date = new DateTime(2026, 9, 1), Systolic = 130, Diastolic = 85, Weight = 82.5m, Glucose = 110 },
            new VitalSignsEvolutionPoint { Date = new DateTime(2026, 10, 1), Systolic = 120, Diastolic = 80, Weight = 80.0m }
        };

        var vitals = MedicalRecordsViewModel.BuildLatestVitals(points);

        var pressure = vitals.Single(v => v.Label.StartsWith("T/A"));
        Assert.AreEqual("120/80", pressure.Value);
        Assert.AreEqual("antes 130/85", pressure.Trend);
        Assert.IsTrue(pressure.IsDown);

        var weight = vitals.Single(v => v.Label.StartsWith("Peso"));
        Assert.AreEqual("80.0", weight.Value);
        Assert.AreEqual("↓ 2.5", weight.Trend);

        var glucose = vitals.Single(v => v.Label.StartsWith("Glucosa"));
        Assert.AreEqual("110", glucose.Value, "El último valor registrado aunque la última consulta no lo tenga");
        Assert.AreEqual(string.Empty, glucose.Trend);

        Assert.IsEmpty(MedicalRecordsViewModel.BuildLatestVitals(null));
    }

    // ------------------------------------------------------------------ agenda

    [TestMethod]
    public void BuildSlots_OffersFreeHalfHours_SkippingBusyAndPastOnes()
    {
        var day = new DateTime(2026, 10, 20);
        var appointments = new[]
        {
            new PatientAppointment { Date = day, Time = "10:15", DurationMinutes = 45, PatientName = "Ana" },
            new PatientAppointment { Date = day, Time = "12:00", Status = "Cancelada", PatientName = "Luis" },
            new PatientAppointment { Date = day.AddDays(1), Time = "08:00", PatientName = "Otro día" }
        };

        var slots = MedicalRecordsViewModel.BuildSlots(day, appointments, now: day.AddDays(-1));

        Assert.AreEqual(1, slots.Count(s => s.HasAppointment), "Las canceladas y las de otro día no aparecen");
        var free = slots.Where(s => s.IsFree).Select(s => s.TimeText).ToList();
        Assert.DoesNotContain("10:00", free, "10:15–11:00 ocupa el hueco de las 10:00");
        Assert.DoesNotContain("10:30", free);
        Assert.Contains("11:00", free);
        Assert.Contains("12:00", free, "La cita cancelada libera su hueco");
        Assert.AreEqual("08:00", free.First());
        Assert.AreEqual("19:30", free.Last());
        Assert.HasCount(22, free);
        Assert.AreEqual("10:15", slots.Single(s => s.HasAppointment).TimeText);
        Assert.IsTrue(slots.Select(s => s.Time).SequenceEqual(slots.Select(s => s.Time).OrderBy(t => t)), "En orden de hora");

        // Hoy no se ofrecen horas que ya pasaron
        var today = MedicalRecordsViewModel.BuildSlots(day, Array.Empty<PatientAppointment>(), now: day.AddHours(13).AddMinutes(10));
        Assert.AreEqual("13:30", today.First().TimeText);
    }

    [TestMethod]
    public async Task AgendaPanel_SchedulesMovesAndCancelsAppointments()
    {
        var (records, _, _) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        records.OpenPatientChart(patient);

        await records.ScheduleForSelectedPatientAsync();
        Assert.IsTrue(records.IsAgendaPanelOpen);
        Assert.AreEqual(DateTime.Today.AddDays(1), records.AgendaPanelDate);
        Assert.HasCount(7, records.AgendaWeek);

        var slot = records.AgendaSlots.First(s => s.IsFree && s.TimeText == "09:00");
        await records.OpenAgendaSlotAsync(slot);
        Assert.IsTrue(slot.IsFormOpen);
        Assert.AreEqual(patient.Id, records.AgendaFormPatient?.Id, "El paciente de la ficha ya viene elegido");

        // Sin motivo no se guarda
        await records.SaveAgendaFormAsync();
        Assert.IsNotNull(records.AgendaFormError);

        records.SetAgendaReason("Control");
        records.SetAgendaDuration(60);
        await records.SaveAgendaFormAsync();

        var saved = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.PatientRecordId == patient.Id);
        Assert.AreEqual("09:00", saved.Time);
        Assert.AreEqual(60, saved.DurationMinutes);
        Assert.AreEqual("Control", saved.Reason);
        Assert.IsFalse(records.AgendaSlots.Any(s => s.IsFree && (s.TimeText == "09:00" || s.TimeText == "09:30")), "La hora queda ocupada");
        Assert.AreEqual(1, records.AgendaWeek.Single(d => d.Date == DateTime.Today.AddDays(1)).Count);

        // Cambiar de horario: se elige la cita y luego el nuevo hueco
        var booked = records.AgendaSlots.Single(s => s.HasAppointment).Appointment!;
        records.StartMovingAppointment(booked);
        Assert.IsTrue(records.IsMovingAppointment);
        await records.OpenAgendaSlotAsync(records.AgendaSlots.First(s => s.IsFree && s.TimeText == "16:00"));
        Assert.IsFalse(records.IsMovingAppointment);
        var moved = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.PatientRecordId == patient.Id);
        Assert.AreEqual(saved.Id, moved.Id, "Es la misma cita, no una nueva");
        Assert.AreEqual("16:00", moved.Time);

        await records.CancelAppointmentAsync(records.AgendaSlots.Single(s => s.HasAppointment).Appointment);
        var cancelled = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.Id == saved.Id);
        Assert.AreEqual("Cancelada", cancelled.Status);
        Assert.IsFalse(records.AgendaSlots.Any(s => s.HasAppointment));

        // Al volver a abrir el panel empieza en hoy
        records.CloseAgendaPanel();
        await records.ToggleAgendaPanelAsync();
        Assert.AreEqual(DateTime.Today, records.AgendaPanelDate);
    }

    [TestMethod]
    public async Task AgendaPanel_RequiresPatient()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.OpenAgendaPanelAsync();
        records.AgendaPanelDate = DateTime.Today.AddDays(2);
        await records.LoadAgendaPanelAsync();

        await records.OpenAgendaSlotAsync(records.AgendaSlots.First(s => s.IsFree));
        Assert.IsNull(records.AgendaFormPatient, "Sin ficha abierta hay que elegir al paciente");
        records.SetAgendaReason("Primera vez");
        await records.SaveAgendaFormAsync();
        StringAssert.Contains(records.AgendaFormError, "paciente");
    }

    [TestMethod]
    public async Task RemindByWhatsApp_OpensChatWithMexicanPrefix_AndExplainsMissingPhone()
    {
        var (records, _, _) = await CreateRecordsAsync();
        string? opened = null;
        records.OpenUrl = url => { opened = url; return true; };

        records.RemindByWhatsApp(new PatientAppointment { PatientName = "Ana", PatientPhone = "55 1234 5678", Date = new DateTime(2026, 10, 20), Time = "10:00" });
        Assert.IsNotNull(opened);
        StringAssert.StartsWith(opened, "https://wa.me/525512345678?text=");

        opened = null;
        records.RemindByWhatsApp(new PatientAppointment { PatientName = "Luis", PatientPhone = "123" });
        Assert.IsNull(opened);
        Assert.IsTrue(records.IsInfoModalVisible);
    }

    [TestMethod]
    public async Task AttendAppointment_StartsConsultationWithReason_AndChargingMarksItAttended()
    {
        var (records, pos, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        await _service.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Dolor de garganta"
        });
        await records.LoadTodayAppointmentsAsync();
        var appointment = records.TodayAppointments.Single(a => a.PatientRecordId == patient.Id);

        records.AttendAppointment(appointment);

        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.IsConsultationPage);
        Assert.AreEqual(patient.Id, records.SelectedPatient?.Id);
        Assert.AreEqual("Dolor de garganta", records.ConsultationChiefComplaint);

        records.ConsultationDiagnosis = "J02 Faringitis aguda";
        await records.FinalizeConsultationAsync();
        pos.SelectedPaymentMethod = "Efectivo";
        pos.CashReceived = 5000m;
        await pos.ProcessSaleAsync();

        await WaitUntilAsync(() => !consultations.HasActiveConsultation && records.IsHomePage);
        await WaitUntilAsync(() => records.TodayAppointments.Any(a => a.Id == appointment.Id && a.IsAttended));
        Assert.AreEqual("Atendida", (await _service.GetAppointmentsByDayAsync(DateTime.Today)).Single(a => a.Id == appointment.Id).Status);
    }

    [TestMethod]
    public async Task AttendAppointment_WithoutRecord_SearchesByName()
    {
        var (records, _, consultations) = await CreateRecordsAsync();

        records.AttendAppointment(new PatientAppointment { PatientRecordId = 0, PatientName = "Paciente Sin Expediente", Date = DateTime.Today });

        Assert.IsFalse(consultations.HasActiveConsultation);
        Assert.IsTrue(records.IsHomePage);
        Assert.AreEqual("Paciente Sin Expediente", records.HomeSearchText);
    }

    [TestMethod]
    public async Task DeactivatePatient_HidesItFromSearch_ButKeepsTheRecord()
    {
        var (records, _, _) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        records.OpenPatientChart(patient);

        await records.DeactivateSelectedPatientAsync();

        Assert.IsTrue(records.IsHomePage);
        Assert.IsNull(records.SelectedPatient);
        Assert.IsFalse(records.Patients.Any(p => p.Id == patient.Id), "Deja de aparecer");
        Assert.IsFalse(MedicalRecordsViewModel.RankPatients(records.Patients, patient.FullName).Any(p => p.Id == patient.Id));

        using var db = new AppDbContext(_options);
        var stored = db.Patients.Single(p => p.Id == patient.Id);
        Assert.IsFalse(stored.IsActive, "Baja lógica: el expediente se conserva");
    }
}
