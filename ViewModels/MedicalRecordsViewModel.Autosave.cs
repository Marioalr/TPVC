using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Guardado automático de la consulta en curso. Cada cambio toma una copia (barata, en el hilo de
/// la UI) y la escribe en disco en segundo plano tras una pausa al escribir, o cada 10 s como
/// máximo si no se deja de escribir. Si la app se cierra o el equipo se apaga, la consulta se
/// recupera tal cual al abrir. Es un archivo local: no genera escrituras en la base ni tráfico de
/// sincronización mientras se escribe.
/// </summary>
public partial class MedicalRecordsViewModel
{
    private static readonly TimeSpan AutosaveMaxWait = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> DraftProperties = new(StringComparer.Ordinal)
    {
        nameof(ConsultationChiefComplaint),
        nameof(ConsultationSystemsReview),
        nameof(ConsultationPhysicalExam),
        nameof(ConsultationDiagnosis),
        nameof(ConsultationTreatmentPlan),
        nameof(ConsultationNotes),
        nameof(ConsultationGeneralIndications),
        nameof(ConsultationTA),
        nameof(ConsultationFC),
        nameof(ConsultationFR),
        nameof(ConsultationTemp),
        nameof(ConsultationWeight),
        nameof(ConsultationHeight),
        nameof(ConsultationSpO2),
        nameof(ConsultationGlucose),
        nameof(ConsultationSection)
    };

    private readonly object _draftGate = new();
    private readonly SynchronizationContext? _autosaveUiContext = SynchronizationContext.Current;
    private ConsultationDraftStore? _draftStore;
    private Timer? _autosaveTimer;
    private ConsultationDraft? _pendingDraft;
    private long _draftGeneration;
    private DateTime _lastDraftWriteUtc = DateTime.MinValue;
    private bool _draftSessionActive;
    private bool _suspendAutosave;
    private bool _draftRestoreAttempted;

    /// <summary>Pausa al escribir antes de guardar (las pruebas la acortan).</summary>
    public TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Carga inicial lanzada por el constructor (expedientes, servicios y consulta recuperada).</summary>
    public Task InitialLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Texto discreto en la barra de la consulta: "Guardado 10:42:15".</summary>
    [ObservableProperty]
    private string _autosaveStatus = string.Empty;

    /// <summary>La consulta en pantalla se recuperó de un cierre sin terminarla.</summary>
    [ObservableProperty]
    private bool _isConsultationRecovered;

    public bool IsAutosaveEnabled => _draftStore != null;

    private void InitializeAutosave(ConsultationDraftStore? store)
    {
        _draftStore = store;
        if (store != null) PropertyChanged += OnPropertyChangedForAutosave;
    }

    private void OnPropertyChangedForAutosave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && DraftProperties.Contains(e.PropertyName)) ScheduleAutosave();
    }

    /// <summary>Inicio, cargos a la cuenta y cierre de la consulta (desde SyncActiveConsultation).</summary>
    private void OnActiveConsultationStateChangedForAutosave()
    {
        if (_draftStore == null) return;

        if (HasActiveConsultation)
        {
            _draftSessionActive = true;
            ScheduleAutosave();
        }
        else if (_draftSessionActive)
        {
            // Terminada (cobrada) o descartada: el borrador ya no debe recuperarse
            _draftSessionActive = false;
            ClearConsultationDraft();
        }
    }

    private void ScheduleAutosave()
    {
        if (_draftStore == null || _suspendAutosave || !HasActiveConsultation) return;

        var draft = BuildConsultationDraft();
        if (draft == null) return;
        Volatile.Write(ref _pendingDraft, draft);

        var sinceLastWrite = DateTime.UtcNow - _lastDraftWriteUtc;
        var due = sinceLastWrite >= AutosaveMaxWait ? TimeSpan.Zero : AutosaveDelay;
        _autosaveTimer ??= new Timer(_ => WritePendingDraft(), null, Timeout.Infinite, Timeout.Infinite);
        _autosaveTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Guarda ya la consulta en curso (Ctrl+S y al cerrar la aplicación).</summary>
    [RelayCommand]
    public void SaveConsultationDraftNow()
    {
        if (_draftStore == null) return;
        if (HasActiveConsultation && !_suspendAutosave)
        {
            Volatile.Write(ref _pendingDraft, BuildConsultationDraft());
        }
        WritePendingDraft();
    }

    public void FlushConsultationDraft() => SaveConsultationDraftNow();

    private ConsultationDraft? BuildConsultationDraft()
    {
        var active = _consultationService?.CurrentConsultation;
        if (active == null) return null;

        return new ConsultationDraft
        {
            Generation = Interlocked.Read(ref _draftGeneration),
            PatientId = active.PatientId,
            PatientName = active.PatientName,
            Folio = active.Folio,
            StartedAt = active.StartedAt,
            ChiefComplaintLabel = active.ChiefComplaint,
            ChiefComplaint = ConsultationChiefComplaint ?? string.Empty,
            SystemsReview = ConsultationSystemsReview ?? string.Empty,
            PhysicalExam = ConsultationPhysicalExam ?? string.Empty,
            Diagnosis = ConsultationDiagnosis ?? string.Empty,
            TreatmentPlan = ConsultationTreatmentPlan ?? string.Empty,
            Notes = ConsultationNotes ?? string.Empty,
            GeneralIndications = ConsultationGeneralIndications ?? string.Empty,
            TA = ConsultationTA ?? string.Empty,
            FC = ConsultationFC ?? string.Empty,
            FR = ConsultationFR ?? string.Empty,
            Temp = ConsultationTemp ?? string.Empty,
            Weight = ConsultationWeight ?? string.Empty,
            Height = ConsultationHeight ?? string.Empty,
            SpO2 = ConsultationSpO2 ?? string.Empty,
            Glucose = ConsultationGlucose ?? string.Empty,
            Section = ConsultationSection,
            SavedToRecord = _isCurrentConsultationSaved,
            // Copias: el archivo se escribe en otro hilo mientras la consulta sigue cambiando
            Items = active.Items.Select(CloneItem).ToList(),
            Prescriptions = ConsultationPrescriptions.Select(ClonePrescription).ToList()
        };
    }

    private void WritePendingDraft()
    {
        var draft = Interlocked.Exchange(ref _pendingDraft, null);
        if (draft == null || _draftStore == null) return;

        bool saved;
        lock (_draftGate)
        {
            // Si la consulta terminó mientras esperaba, no se resucita su borrador
            if (draft.Generation != Interlocked.Read(ref _draftGeneration)) return;
            draft.SavedAt = DateTime.Now;
            saved = _draftStore.Save(draft);
            if (saved) _lastDraftWriteUtc = DateTime.UtcNow;
        }

        var status = saved
            ? $"Guardado {draft.SavedAt:HH:mm:ss}"
            : "No se pudo guardar automáticamente";
        PostToUi(() =>
        {
            // Bajo el mismo candado que el cierre: un guardado que termina justo al cobrar no
            // deja el indicador "Guardado" en una consulta ya cerrada
            lock (_draftGate)
            {
                if (draft.Generation == Interlocked.Read(ref _draftGeneration) && HasActiveConsultation)
                {
                    AutosaveStatus = status;
                }
            }
        });
    }

    private void ClearConsultationDraft()
    {
        lock (_draftGate)
        {
            Interlocked.Increment(ref _draftGeneration);
            Volatile.Write(ref _pendingDraft, null);
            _autosaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _draftStore?.Delete();
            _lastDraftWriteUtc = DateTime.MinValue;
            AutosaveStatus = string.Empty;
            IsConsultationRecovered = false;
        }
    }

    /// <summary>
    /// Al abrir la app: si quedó una consulta sin terminar, se retoma con su nota, signos, receta,
    /// cuenta, folio y hora de inicio.
    /// </summary>
    private void TryRestoreConsultationDraft()
    {
        if (_draftStore == null || _draftRestoreAttempted || _consultationService == null) return;
        _draftRestoreAttempted = true;
        if (HasActiveConsultation) return;

        var draft = _draftStore.Load();
        if (draft == null) return;

        var patient = Patients.FirstOrDefault(p => p.Id == draft.PatientId);
        if (patient == null)
        {
            AppLogger.Warn($"Se descartó la consulta en curso {draft.Folio}: el expediente {draft.PatientId} ya no existe.");
            _draftStore.Delete();
            return;
        }

        _suspendAutosave = true;
        try
        {
            SelectPatient(patient);
            var active = _consultationService.StartConsultation(patient, draft.ChiefComplaintLabel);
            if (!string.IsNullOrWhiteSpace(draft.Folio)) active.Folio = draft.Folio;
            if (draft.StartedAt != default) active.StartedAt = draft.StartedAt;
            foreach (var item in draft.Items.Where(i => i.Quantity > 0))
            {
                active.Items.Add(CloneItem(item));
            }
            active.NotifyItemsChanged();

            ClearConsultationFields();
            ConsultationFolio = active.Folio;
            ConsultationChiefComplaint = draft.ChiefComplaint;
            ConsultationSystemsReview = draft.SystemsReview;
            ConsultationPhysicalExam = draft.PhysicalExam;
            ConsultationDiagnosis = draft.Diagnosis;
            ConsultationTreatmentPlan = draft.TreatmentPlan;
            ConsultationNotes = draft.Notes;
            ConsultationGeneralIndications = draft.GeneralIndications;
            ConsultationTA = draft.TA;
            ConsultationFC = draft.FC;
            ConsultationFR = draft.FR;
            ConsultationTemp = draft.Temp;
            ConsultationWeight = draft.Weight;
            ConsultationHeight = draft.Height;
            ConsultationSpO2 = draft.SpO2;
            ConsultationGlucose = draft.Glucose;

            // La receta vive en el expediente; se enlazan las mismas instancias para que
            // "Se surte aquí" siga funcionando sobre la lista visible
            foreach (var saved in draft.Prescriptions)
            {
                var current = patient.ActivePrescription.FirstOrDefault(p =>
                    p.MedicineName == saved.MedicineName && p.Dosage == saved.Dosage);
                ConsultationPrescriptions.Add(current ?? saved);
            }

            ConsultationSection = Math.Clamp(draft.Section, 1, ConsultationSectionCount);
            _isCurrentConsultationSaved = draft.SavedToRecord;
            _draftSessionActive = true;
            SyncActiveConsultation();
        }
        finally
        {
            _suspendAutosave = false;
        }

        IsConsultationRecovered = true;
        PatientsPage = PageConsultation;
        AutosaveStatus = $"Consulta recuperada · iniciada {draft.StartedAt:dd/MM HH:mm}";
        AppLogger.Info($"Consulta en curso recuperada: {draft.Folio} ({draft.PatientName}).");
    }

    private void PostToUi(Action action)
    {
        if (_autosaveUiContext != null) _autosaveUiContext.Post(_ => action(), null);
        else action();
    }

    private static ConsultationProductItem CloneItem(ConsultationProductItem item) => new()
    {
        ProductId = item.ProductId,
        Code = item.Code,
        Name = item.Name,
        GenericName = item.GenericName,
        Presentation = item.Presentation,
        Unit = item.Unit,
        UnitPrice = item.UnitPrice,
        Quantity = item.Quantity,
        AddedAt = item.AddedAt
    };

    private static PatientPrescriptionItem ClonePrescription(PatientPrescriptionItem item) => new()
    {
        MedicineName = item.MedicineName,
        Dosage = item.Dosage,
        RecommendedQuantity = item.RecommendedQuantity,
        DispenseInClinic = item.DispenseInClinic
    };
}
