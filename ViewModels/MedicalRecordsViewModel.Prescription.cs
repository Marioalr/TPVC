using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// El expediente durante la consulta en curso. La receta empieza vacía: la última del paciente no
/// se arrastra (antes se imprimía y guardaba en la consulta nueva si no se borraba a mano), pero
/// se puede repetir con un clic. Si la consulta termina sin receta, la última emitida sigue siendo
/// la del expediente; si se descarta, el expediente vuelve a como estaba antes de empezarla.
/// </summary>
public partial class MedicalRecordsViewModel
{
    private PatientRecord? _consultationRecordPatient;
    private List<PatientPrescriptionItem> _previousPrescription = new();
    private PatientSummarySnapshot? _summaryBeforeConsultation;

    /// <summary>false si la consulta se recuperó de un borrador sin la receta anterior (versiones viejas).</summary>
    private bool _previousPrescriptionKnown;

    /// <summary>Medicamentos de la receta anterior que aún no están en la de esta consulta.</summary>
    private List<PatientPrescriptionItem> PendingPreviousPrescription
    {
        get
        {
            if (_consultationRecordPatient == null || SelectedPatient?.Id != _consultationRecordPatient.Id) return new();
            var current = _consultationRecordPatient.ActivePrescription;
            return _previousPrescription
                .Where(p => !current.Any(c => SameMedicine(c, p)))
                .ToList();
        }
    }

    public bool CanRepeatPreviousPrescription => HasActiveConsultation && PendingPreviousPrescription.Count > 0;

    public string PreviousPrescriptionSummary => string.Join(" · ", PendingPreviousPrescription.Select(p => p.MedicineName));

    /// <summary>"Última receta" de la ficha: durante la consulta, la emitida antes de ella.</summary>
    public IReadOnlyList<PatientPrescriptionItem> ChartPrescription =>
        IsConsultationForSelectedPatient && _previousPrescriptionKnown
            ? _previousPrescription
            : (IReadOnlyList<PatientPrescriptionItem>?)SelectedPatient?.ActivePrescription ?? Array.Empty<PatientPrescriptionItem>();

    private static bool SameMedicine(PatientPrescriptionItem a, PatientPrescriptionItem b) =>
        string.Equals(a.MedicineName?.Trim(), b.MedicineName?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void NotifyPrescriptionStateChanged()
    {
        OnPropertyChanged(nameof(CanRepeatPreviousPrescription));
        OnPropertyChanged(nameof(PreviousPrescriptionSummary));
        OnPropertyChanged(nameof(ChartPrescription));
        OnPropertyChanged(nameof(HasActivePrescription));
    }

    /// <summary>Al iniciar una consulta nueva: foto del expediente y receta vacía.</summary>
    private void BeginConsultationOnRecord(PatientRecord patient)
    {
        _consultationRecordPatient = patient;
        _summaryBeforeConsultation = PatientSummarySnapshot.Of(patient);
        _previousPrescription = patient.ActivePrescription.Select(ClonePrescription).ToList();
        foreach (var item in _previousPrescription) item.DispenseInClinic = false;
        _previousPrescriptionKnown = true;

        patient.ActivePrescription = new List<PatientPrescriptionItem>();
        patient.SyncPrescriptionJson();
        OnPropertyChanged(nameof(SelectedPatient));
        NotifyPrescriptionStateChanged();
    }

    /// <summary>Al recuperar una consulta: su receta en curso, la anterior y la foto, tal como se guardaron.</summary>
    private void ResumeConsultationOnRecord(PatientRecord patient, ConsultationDraft draft)
    {
        // Se enlazan las mismas instancias del expediente cuando existen para que "Se surte aquí"
        // siga funcionando sobre la lista visible
        var resumed = draft.Prescriptions
            .Select(saved => patient.ActivePrescription.FirstOrDefault(p => p.MedicineName == saved.MedicineName && p.Dosage == saved.Dosage) ?? saved)
            .ToList();

        _consultationRecordPatient = patient;
        _summaryBeforeConsultation = draft.PatientBefore;
        _previousPrescriptionKnown = draft.PreviousPrescriptions != null;
        _previousPrescription = draft.PreviousPrescriptions?.Select(ClonePrescription).ToList() ?? new();

        patient.ActivePrescription = resumed;
        patient.SyncPrescriptionJson();
        NotifyPrescriptionStateChanged();
    }

    /// <summary>
    /// Al terminar o descartar la consulta. Descartada: el expediente vuelve a como estaba
    /// (resumen y receta). Terminada sin receta: la última emitida sigue siendo la anterior.
    /// Terminada con receta: queda la nueva.
    /// </summary>
    private async Task EndConsultationOnRecordAsync(bool discarded)
    {
        var patient = _consultationRecordPatient;
        if (patient == null) return;
        _consultationRecordPatient = null;
        var previous = _previousPrescription;
        var known = _previousPrescriptionKnown;
        var summary = _summaryBeforeConsultation;
        _previousPrescription = new();
        _previousPrescriptionKnown = false;
        _summaryBeforeConsultation = null;

        bool changed = false;
        if (discarded && summary != null)
        {
            summary.ApplyTo(patient);
            changed = true;
        }
        if (known && (discarded || patient.ActivePrescription.Count == 0)
            && !(previous.Count == 0 && patient.ActivePrescription.Count == 0))
        {
            patient.ActivePrescription = previous;
            patient.SyncPrescriptionJson();
            changed = true;
        }

        if (changed && _inventoryService != null && patient.Id > 0)
        {
            try
            {
                await _inventoryService.SavePatientAsync(patient);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo regresar el expediente a como estaba antes de la consulta", ex);
            }
        }

        if (SelectedPatient?.Id == patient.Id) OnPropertyChanged(nameof(SelectedPatient));
        NotifyPrescriptionStateChanged();
    }

    /// <summary>
    /// Copia a esta consulta los medicamentos de la receta anterior que falten. Se revisan otra vez
    /// las alergias (pudieron registrarse después) y las existencias para "Se surte aquí".
    /// </summary>
    [RelayCommand]
    public async Task RepeatPreviousPrescriptionAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (!HasActiveConsultation || SelectedPatient == null) return;

        var skipped = new List<string>();
        foreach (var item in PendingPreviousPrescription)
        {
            var product = FindCatalogProduct(item.MedicineName);
            if (InventoryService.CheckMedicationAllergy(SelectedPatient, product, item.MedicineName).HasConflict)
            {
                skipped.Add(item.MedicineName);
                continue;
            }
            await AddPrescriptionItemInternalAsync(product, item.MedicineName, item.Dosage, Math.Max(1, item.RecommendedQuantity));
        }

        NotifyPrescriptionStateChanged();
        if (skipped.Count > 0)
        {
            InfoModalMessage = $"No se repitió {string.Join(", ", skipped)}: coincide con las alergias registradas del paciente. Si de todos modos debe recetarse, agréguelo a mano para confirmar la alerta.";
            IsInfoModalVisible = true;
        }
    }
}
