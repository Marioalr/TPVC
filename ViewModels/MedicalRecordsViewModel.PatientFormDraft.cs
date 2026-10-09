using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Alta o edición de paciente en curso: se guarda sola en un archivo local tras una pausa al
/// escribir y, si el programa se cierra o se va la luz, se recupera al abrir. Se borra al guardar
/// o cancelar el formulario. Como el de la consulta, no escribe en la base mientras se captura.
/// </summary>
public partial class MedicalRecordsViewModel
{
    /// <summary>
    /// Campos que se guardan, en el orden en que se restauran (sexo y edad al final para que el formulario
    /// se ajuste). Cada fila dice cómo leer el campo como texto y cómo devolverlo: antes se buscaba cada
    /// propiedad por su nombre con reflexión, que dejaría de encontrarla en silencio al moverla a otro
    /// ViewModel. Las claves (los nombres) son las mismas y los valores se convierten igual.
    /// </summary>
    private static readonly (string Key, Func<MedicalRecordsViewModel, string> Get, Action<MedicalRecordsViewModel, string> Set)[] PatientFormDraftFields =
    {
        TextField(nameof(FormRecordNumber), vm => vm.FormRecordNumber, (vm, value) => vm.FormRecordNumber = value),
        TextField(nameof(FormFirstName), vm => vm.FormFirstName, (vm, value) => vm.FormFirstName = value),
        TextField(nameof(FormLastName), vm => vm.FormLastName, (vm, value) => vm.FormLastName = value),
        TextField(nameof(FormMaternalLastName), vm => vm.FormMaternalLastName, (vm, value) => vm.FormMaternalLastName = value),
        TextField(nameof(FormPhone), vm => vm.FormPhone, (vm, value) => vm.FormPhone = value),
        TextField(nameof(FormEmail), vm => vm.FormEmail, (vm, value) => vm.FormEmail = value),
        TextField(nameof(FormAddress), vm => vm.FormAddress, (vm, value) => vm.FormAddress = value),
        TextField(nameof(FormBloodType), vm => vm.FormBloodType, (vm, value) => vm.FormBloodType = value),
        TextField(nameof(FormAllergies), vm => vm.FormAllergies, (vm, value) => vm.FormAllergies = value),
        TextField(nameof(FormChronicDiseases), vm => vm.FormChronicDiseases, (vm, value) => vm.FormChronicDiseases = value),
        TextField(nameof(FormEmergencyContactName), vm => vm.FormEmergencyContactName, (vm, value) => vm.FormEmergencyContactName = value),
        TextField(nameof(FormEmergencyContactPhone), vm => vm.FormEmergencyContactPhone, (vm, value) => vm.FormEmergencyContactPhone = value),
        TextField(nameof(FormOccupation), vm => vm.FormOccupation, (vm, value) => vm.FormOccupation = value),
        TextField(nameof(FormReligion), vm => vm.FormReligion, (vm, value) => vm.FormReligion = value),
        TextField(nameof(FormFamilyHistory), vm => vm.FormFamilyHistory, (vm, value) => vm.FormFamilyHistory = value),
        TextField(nameof(FormSurgeries), vm => vm.FormSurgeries, (vm, value) => vm.FormSurgeries = value),
        TextField(nameof(FormTransfusions), vm => vm.FormTransfusions, (vm, value) => vm.FormTransfusions = value),
        TextField(nameof(FormHospitalizations), vm => vm.FormHospitalizations, (vm, value) => vm.FormHospitalizations = value),
        TextField(nameof(FormImmunizationSchedule), vm => vm.FormImmunizationSchedule, (vm, value) => vm.FormImmunizationSchedule = value),
        TextField(nameof(FormMenarcheAge), vm => vm.FormMenarcheAge, (vm, value) => vm.FormMenarcheAge = value),
        TextField(nameof(FormSexualOnsetAge), vm => vm.FormSexualOnsetAge, (vm, value) => vm.FormSexualOnsetAge = value),
        TextField(nameof(FormSexualPartnersCount), vm => vm.FormSexualPartnersCount, (vm, value) => vm.FormSexualPartnersCount = value),
        TextField(nameof(FormPeriodDuration), vm => vm.FormPeriodDuration, (vm, value) => vm.FormPeriodDuration = value),
        TextField(nameof(FormIsPeriodHeavy), vm => vm.FormIsPeriodHeavy, (vm, value) => vm.FormIsPeriodHeavy = value),
        TextField(nameof(FormPapSmear), vm => vm.FormPapSmear, (vm, value) => vm.FormPapSmear = value),
        IntField(nameof(FormPregnanciesCount), vm => vm.FormPregnanciesCount, (vm, value) => vm.FormPregnanciesCount = value),
        IntField(nameof(FormCesareansCount), vm => vm.FormCesareansCount, (vm, value) => vm.FormCesareansCount = value),
        IntField(nameof(FormAbortionsCount), vm => vm.FormAbortionsCount, (vm, value) => vm.FormAbortionsCount = value),
        BoolField(nameof(FormIsCurrentlyPregnant), vm => vm.FormIsCurrentlyPregnant, (vm, value) => vm.FormIsCurrentlyPregnant = value),
        TextField(nameof(FormGestationalWeeks), vm => vm.FormGestationalWeeks, (vm, value) => vm.FormGestationalWeeks = value),
        TextField(nameof(FormDevelopmentalMilestones), vm => vm.FormDevelopmentalMilestones, (vm, value) => vm.FormDevelopmentalMilestones = value),
        TextField(nameof(FormDiet), vm => vm.FormDiet, (vm, value) => vm.FormDiet = value),
        TextField(nameof(FormHousingConditions), vm => vm.FormHousingConditions, (vm, value) => vm.FormHousingConditions = value),
        TextField(nameof(FormZoonosis), vm => vm.FormZoonosis, (vm, value) => vm.FormZoonosis = value),
        TextField(nameof(FormClinicalNotes), vm => vm.FormClinicalNotes, (vm, value) => vm.FormClinicalNotes = value),
        TextField(nameof(FormGender), vm => vm.FormGender, (vm, value) => vm.FormGender = value),
        TextField(nameof(FormAgeText), vm => vm.FormAgeText, (vm, value) => vm.FormAgeText = value),
        TextField(nameof(FormBirthDateText), vm => vm.FormBirthDateText, (vm, value) => vm.FormBirthDateText = value),
        TextField(nameof(FormOpenSection), vm => vm.FormOpenSection, (vm, value) => vm.FormOpenSection = value)
    };

    private static readonly HashSet<string> PatientFormDraftFieldSet = new(PatientFormDraftFields.Select(f => f.Key), StringComparer.Ordinal);

    /// <summary>Texto tal como lo guardaba la versión con reflexión (cultura invariable; null → vacío).</summary>
    private static string DraftText(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static (string, Func<MedicalRecordsViewModel, string>, Action<MedicalRecordsViewModel, string>) TextField(
        string key, Func<MedicalRecordsViewModel, string> get, Action<MedicalRecordsViewModel, string> set) =>
        (key, vm => DraftText(get(vm)), set);

    /// <summary>Un número que no se puede leer deja el campo como está.</summary>
    private static (string, Func<MedicalRecordsViewModel, string>, Action<MedicalRecordsViewModel, string>) IntField(
        string key, Func<MedicalRecordsViewModel, int> get, Action<MedicalRecordsViewModel, int> set) =>
        (key, vm => DraftText(get(vm)), (vm, text) =>
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) set(vm, number);
        });

    /// <summary>Un sí/no que no se puede leer deja el campo como está.</summary>
    private static (string, Func<MedicalRecordsViewModel, string>, Action<MedicalRecordsViewModel, string>) BoolField(
        string key, Func<MedicalRecordsViewModel, bool> get, Action<MedicalRecordsViewModel, bool> set) =>
        (key, vm => DraftText(get(vm)), (vm, text) =>
        {
            if (bool.TryParse(text, out var flag)) set(vm, flag);
        });

    private readonly object _formDraftGate = new();
    private Timer? _formDraftTimer;
    private long _formDraftGeneration;
    private long _formDraftSequence;
    private long _lastWrittenFormDraftSequence;
    private Dictionary<string, string>? _patientFormBaseline;
    private bool _suspendFormDraft;

    private void SchedulePatientFormDraft()
    {
        if (_draftStore == null || _suspendFormDraft || !IsPatientModalOpen) return;
        _formDraftTimer ??= new Timer(_ => _ui.Post(SavePatientFormDraftNow), null, Timeout.Infinite, Timeout.Infinite);
        _formDraftTimer.Change(AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    private Dictionary<string, string> CapturePatientFormFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in PatientFormDraftFields)
        {
            fields[field.Key] = field.Get(this);
        }
        return fields;
    }

    private void ApplyPatientFormFields(IReadOnlyDictionary<string, string> fields)
    {
        foreach (var field in PatientFormDraftFields)
        {
            if (!fields.TryGetValue(field.Key, out var text)) continue;
            field.Set(this, text);
        }
    }

    /// <summary>Guarda ya el formulario abierto (tras la pausa al escribir y al cerrar el programa).</summary>
    private void SavePatientFormDraftNow()
    {
        if (_draftStore == null || _suspendFormDraft || !IsPatientModalOpen) return;

        var generation = Interlocked.Read(ref _formDraftGeneration);
        var sequence = Interlocked.Increment(ref _formDraftSequence);
        var fields = CapturePatientFormFields();
        lock (_formDraftGate)
        {
            // Si el formulario se cerró mientras esperaba, no se resucita su borrador; una copia
            // tomada antes que otra ya escrita tampoco la sobrescribe
            if (generation != Interlocked.Read(ref _formDraftGeneration)) return;
            if (sequence <= _lastWrittenFormDraftSequence) return;
            _lastWrittenFormDraftSequence = sequence;

            // Tal como se abrió (cambiar de sección no es un cambio): no hay nada que recuperar
            var baseline = _patientFormBaseline;
            if (baseline != null && fields.All(f => f.Key == nameof(FormOpenSection) || (baseline.TryGetValue(f.Key, out var v) && v == f.Value)))
            {
                _draftStore.DeletePatientForm();
                return;
            }
            _draftStore.SavePatientForm(new PatientFormDraft { SavedAt = DateTime.Now, PatientId = FormPatientId, Fields = fields });
        }
    }

    /// <summary>Al abrir el formulario (ya con sus datos iniciales): lo que hay no es un cambio.</summary>
    private void OnPatientFormOpenedForDraft() => _patientFormBaseline = CapturePatientFormFields();

    /// <summary>Guardado o cancelado: el borrador ya no debe recuperarse.</summary>
    private void OnPatientFormClosedForDraft()
    {
        _formDraftTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _patientFormBaseline = null;
        lock (_formDraftGate)
        {
            Interlocked.Increment(ref _formDraftGeneration);
            _draftStore?.DeletePatientForm();
        }
    }

    /// <summary>Al abrir la app: si quedó un alta o una edición sin guardar, se reabre tal cual.</summary>
    private async Task TryRestorePatientFormDraftAsync()
    {
        if (_draftStore == null) return;
        var draft = _draftStore.LoadPatientForm();
        if (draft == null) return;

        if (draft.PatientId > 0)
        {
            var patient = Patients.FirstOrDefault(p => p.Id == draft.PatientId);
            if (patient == null)
            {
                AppLogger.Warn($"Se descartó la edición sin guardar del expediente {draft.PatientId}: ya no existe.");
                _draftStore.DeletePatientForm();
                return;
            }
            SelectedPatient = patient;
            EditPatient();
        }
        else
        {
            await NewPatientAsync();
        }

        _suspendFormDraft = true;
        try
        {
            ApplyPatientFormFields(draft.Fields);
        }
        finally
        {
            _suspendFormDraft = false;
        }

        Feedback.Info(draft.PatientId > 0
            ? "Se recuperó la edición del expediente que no se había guardado"
            : "Se recuperó el alta de paciente que no se había guardado");
        AppLogger.Info($"Formulario de paciente recuperado (expediente {draft.PatientId}, guardado {draft.SavedAt:dd/MM HH:mm}).");
    }
}
