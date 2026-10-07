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
    /// <summary>Campos que se guardan, en el orden en que se restauran (sexo y edad al final para que el formulario se ajuste).</summary>
    private static readonly string[] PatientFormDraftFields =
    {
        nameof(FormRecordNumber), nameof(FormFirstName), nameof(FormLastName), nameof(FormMaternalLastName),
        nameof(FormPhone), nameof(FormEmail), nameof(FormAddress), nameof(FormBloodType),
        nameof(FormAllergies), nameof(FormChronicDiseases), nameof(FormEmergencyContactName), nameof(FormEmergencyContactPhone),
        nameof(FormOccupation), nameof(FormReligion), nameof(FormFamilyHistory), nameof(FormSurgeries),
        nameof(FormTransfusions), nameof(FormHospitalizations), nameof(FormImmunizationSchedule),
        nameof(FormMenarcheAge), nameof(FormSexualOnsetAge), nameof(FormSexualPartnersCount), nameof(FormPeriodDuration),
        nameof(FormIsPeriodHeavy), nameof(FormPapSmear), nameof(FormPregnanciesCount), nameof(FormCesareansCount),
        nameof(FormAbortionsCount), nameof(FormIsCurrentlyPregnant), nameof(FormGestationalWeeks),
        nameof(FormDevelopmentalMilestones), nameof(FormDiet), nameof(FormHousingConditions), nameof(FormZoonosis),
        nameof(FormClinicalNotes),
        nameof(FormGender), nameof(FormAgeText), nameof(FormBirthDateText), nameof(FormOpenSection)
    };

    private static readonly HashSet<string> PatientFormDraftFieldSet = new(PatientFormDraftFields, StringComparer.Ordinal);

    private readonly object _formDraftGate = new();
    private Timer? _formDraftTimer;
    private long _formDraftGeneration;
    private Dictionary<string, string>? _patientFormBaseline;
    private bool _suspendFormDraft;

    private void SchedulePatientFormDraft()
    {
        if (_draftStore == null || _suspendFormDraft || !IsPatientModalOpen) return;
        _formDraftTimer ??= new Timer(_ => PostToUi(SavePatientFormDraftNow), null, Timeout.Infinite, Timeout.Infinite);
        _formDraftTimer.Change(AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    private Dictionary<string, string> CapturePatientFormFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PatientFormDraftFields)
        {
            var value = typeof(MedicalRecordsViewModel).GetProperty(name)?.GetValue(this);
            fields[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return fields;
    }

    private void ApplyPatientFormFields(IReadOnlyDictionary<string, string> fields)
    {
        foreach (var name in PatientFormDraftFields)
        {
            if (!fields.TryGetValue(name, out var text)) continue;
            var property = typeof(MedicalRecordsViewModel).GetProperty(name);
            if (property == null || !property.CanWrite) continue;

            if (property.PropertyType == typeof(string)) property.SetValue(this, text);
            else if (property.PropertyType == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) property.SetValue(this, number);
            else if (property.PropertyType == typeof(bool) && bool.TryParse(text, out var flag)) property.SetValue(this, flag);
        }
    }

    /// <summary>Guarda ya el formulario abierto (tras la pausa al escribir y al cerrar el programa).</summary>
    private void SavePatientFormDraftNow()
    {
        if (_draftStore == null || _suspendFormDraft || !IsPatientModalOpen) return;

        var generation = Interlocked.Read(ref _formDraftGeneration);
        var fields = CapturePatientFormFields();
        lock (_formDraftGate)
        {
            // Si el formulario se cerró mientras esperaba, no se resucita su borrador
            if (generation != Interlocked.Read(ref _formDraftGeneration)) return;

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

        _notifications?.Info(draft.PatientId > 0
            ? "Se recuperó la edición del expediente que no se había guardado"
            : "Se recuperó el alta de paciente que no se había guardado");
        AppLogger.Info($"Formulario de paciente recuperado (expediente {draft.PatientId}, guardado {draft.SavedAt:dd/MM HH:mm}).");
    }
}
