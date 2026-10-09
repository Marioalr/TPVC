using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1b): contratos de los archivos que escribe Pacientes y que deben seguir iguales al
/// separar MedicalRecordsViewModel: el borrador de la consulta (guardado automático y copia que viaja a
/// otro equipo) y el del alta de paciente. Los archivos de referencia se tomaron del código de la v1.18;
/// las fechas, los GUID y los folios con hora se normalizan (Fase6Assets.NormalizeDraftJson).
/// </summary>
[TestClass]
[DoNotParallelize]
public class DraftContractTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    // ================================================================== consulta en curso

    [TestMethod]
    public async Task ConsultationDraft_OfAScriptedConsultation_MatchesTheGolden()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);

        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        await Fase6Scenarios.FillConsultationAsync(records, consultations);
        records.FlushConsultationDraft();
        records.Dispose();

        var json = File.ReadAllText(clinic.DraftPath);
        Fase6Assets.AssertMatchesGolden("consultation-draft.golden.json", Fase6Assets.NormalizeDraftJson(json), "Borrador de la consulta");
    }

    [TestMethod]
    public async Task ConsultationDraft_v1_18_IsRestoredAndWrittenBackUnchanged()
    {
        using var clinic = new Fase6Clinic();
        var fixture = Fase6Assets.ReadAsset("consultation-draft.v1_18.json");
        Directory.CreateDirectory(Path.GetDirectoryName(clinic.DraftPath)!);
        File.WriteAllText(clinic.DraftPath, fixture);

        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);

        // Se retomó tal cual: en solo lectura (estaba enviada a la tablet), con su folio y su cuenta
        Assert.IsTrue(records.HasActiveConsultation);
        Assert.IsTrue(records.IsConsultationRecovered);
        Assert.IsTrue(records.Handoff.IsConsultationHandedOff);
        Assert.AreEqual("CNS-20261007-183015", consultations.CurrentConsultation!.Folio);
        Assert.AreEqual(6, consultations.CurrentConsultation.Items.Count);
        Assert.AreEqual(4, records.Consultation.ConsultationSection);
        Assert.AreEqual("Paracetamol 500 mg (Caja con 20 tabletas)", records.SelectedPatient!.ActivePrescription.Single().MedicineName);

        records.FlushConsultationDraft();
        records.Dispose();

        var written = Fase6Assets.NormalizeDraftJson(File.ReadAllText(clinic.DraftPath));
        var expected = Fase6Assets.NormalizeDraftJson(fixture);
        Assert.AreEqual(expected, written, "El borrador de v1.18 se vuelve a escribir igual:\n" + Fase6Assets.Diff(expected, written));
    }

    [TestMethod]
    public async Task HandoffPayload_ReceivedByTheCloud_MatchesTheGolden()
    {
        using var clinic = new Fase6Clinic();
        await clinic.ConnectCloudAsync();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);

        // Una cita atendida y un avance de nota guardado: la copia los lleva por su identificador de sincronización
        var appointment = await clinic.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Control mensual"
        });
        records.AttendAppointment(appointment);
        await Fase6Scenarios.FillConsultationAsync(records, consultations);
        Assert.IsNotNull(await records.Consultation.SaveActiveConsultationToDbAsync());

        await records.Handoff.OpenSendConsultationSheetAsync();
        records.Handoff.SelectedHandoffDevice = records.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet");
        await records.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(records.Handoff.IsConsultationHandedOff, records.Handoff.HandoffSheetError ?? records.Info.InfoModalMessage);

        var payload = clinic.CloudScalar("SELECT Payload FROM ConsultationHandoffs");
        Assert.AreNotEqual(string.Empty, payload);
        Assert.AreEqual("1", clinic.CloudScalar("SELECT PayloadVersion FROM ConsultationHandoffs"));
        records.Dispose();

        Fase6Assets.AssertMatchesGolden("handoff-payload.golden.json", Fase6Assets.NormalizeDraftJson(payload), "Copia de la consulta enviada a otro equipo");
    }

    // ================================================================== alta de paciente en curso

    [TestMethod]
    public async Task PatientFormDraft_Keys_MatchTheGolden()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false));
        await records.NewPatientAsync();
        records.PatientForm.FormFirstName = "Lucía";
        records.FlushConsultationDraft();
        records.Dispose();

        // Las claves, en el orden en que se guardan y se restauran (sexo, edad y fecha al final)
        var draft = JsonNode.Parse(File.ReadAllText(clinic.Store().PatientFormPath))!;
        var keys = draft["Fields"]!.AsObject().Select(kv => kv.Key).ToList();
        Fase6Assets.AssertMatchesGolden("patient-form-draft-keys.txt", Fase6Assets.Lines(keys), "Claves del borrador del alta de paciente");
    }

    [TestMethod]
    public async Task PatientFormDraft_v1_18_IsRestoredAndWrittenBackUnchanged()
    {
        using var clinic = new Fase6Clinic();
        var fixture = Fase6Assets.ReadAsset("patient-form-draft.v1_18.json");
        var path = clinic.Store().PatientFormPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, fixture);

        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false));
        Assert.IsTrue(records.Navigation.IsFormPage, "Se reabre el alta");
        Assert.IsTrue(records.PatientForm.IsFormNew);
        Assert.AreEqual("EXP-2026-777", records.PatientForm.FormRecordNumber);
        Assert.AreEqual("Gineco", records.PatientForm.FormOpenSection);
        Assert.AreEqual(2, records.PatientForm.FormPregnanciesCount);
        Assert.IsTrue(records.PatientForm.FormIsCurrentlyPregnant);
        Assert.IsTrue(records.PatientForm.FormShowsGyneco);

        records.FlushConsultationDraft();
        records.Dispose();

        // La edad se recalcula de la fecha de nacimiento (cambia con el día en que corre la prueba)
        var birth = PatientFormNormalize(fixture, out _);
        var written = PatientFormNormalize(File.ReadAllText(path), out var writtenAge);
        Assert.AreEqual(PatientRecord.CalculateAge(new DateTime(1992, 3, 14), DateTime.Today).ToString(System.Globalization.CultureInfo.InvariantCulture), writtenAge);
        Assert.AreEqual(birth, written, "El borrador del alta de v1.18 se vuelve a escribir igual:\n" + Fase6Assets.Diff(birth, written));
    }

    /// <summary>
    /// Cada clave del borrador del alta se guarda y se restaura con el campo de su mismo nombre (el
    /// paso 2c cambió la búsqueda por nombre con reflexión por una tabla escrita a mano: una fila con
    /// el campo equivocado se vería aquí, aunque las claves y el archivo sigan iguales).
    /// </summary>
    [TestMethod]
    public async Task PatientFormDraft_EveryField_IsSavedAndRestoredUnderItsOwnName()
    {
        using var clinic = new Fase6Clinic();
        var keys = Fase6Assets.NormalizeNewLines(Fase6Assets.ReadAsset("patient-form-draft-keys.txt"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var properties = keys.ToDictionary(k => k, k => typeof(PatientFormViewModel).GetProperty(k)
            ?? throw new AssertFailedException($"{k} no es una propiedad pública del ViewModel"));

        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false));
        await records.NewPatientAsync();

        // Un valor distinto en cada campo (los que el formulario ajusta solo, con valores que acepta)
        var special = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["FormPhone"] = "5512340001",
            ["FormEmergencyContactPhone"] = "5512340002",
            ["FormGender"] = "Femenino",
            ["FormBirthDateText"] = "14/03/1992",
            ["FormOpenSection"] = "Gineco"
        };
        int next = 0;
        foreach (var key in keys)
        {
            if (key == "FormAgeText") continue; // sale de la fecha de nacimiento
            next++;
            var type = properties[key].PropertyType;
            object value = special.TryGetValue(key, out var fixedValue) ? fixedValue
                : type == typeof(int) ? 100 + next
                : type == typeof(bool) ? true
                : $"Dato {next:00} de {key}";
            properties[key].SetValue(records.PatientForm, value);
        }

        var expected = keys.ToDictionary(k => k, k => properties[k].GetValue(records.PatientForm));
        var texts = expected.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.AreEqual(texts.Count, texts.Distinct(StringComparer.Ordinal).Count(), "Cada campo lleva un valor distinto: " + string.Join(" | ", texts));

        records.FlushConsultationDraft();
        records.Dispose();

        var fields = JsonNode.Parse(File.ReadAllText(clinic.Store().PatientFormPath))!["Fields"]!.AsObject();
        CollectionAssert.AreEqual(keys, fields.Select(kv => kv.Key).ToArray(), "Claves en el mismo orden");
        foreach (var key in keys)
        {
            Assert.AreEqual(Convert.ToString(expected[key], System.Globalization.CultureInfo.InvariantCulture), fields[key]!.GetValue<string>(), $"{key} se guarda con su propio valor");
        }

        var reopened = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false));
        Assert.IsTrue(reopened.Navigation.IsFormPage, "Se reabre el alta");
        foreach (var key in keys)
        {
            Assert.AreEqual(expected[key], properties[key].GetValue(reopened.PatientForm), $"{key} se restaura en su propio campo");
        }
        reopened.Dispose();
    }

    private static string PatientFormNormalize(string json, out string? age)
    {
        var node = JsonNode.Parse(json)!;
        var fields = node["Fields"]!.AsObject();
        age = fields["FormAgeText"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(fields["FormBirthDateText"]?.GetValue<string>())) fields["FormAgeText"] = "<edad>";
        return Fase6Assets.NormalizeDraftJson(node.ToJsonString());
    }
}
