using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Guardado automático de la consulta en curso (fase 6, paso 19). Cada cambio toma una copia (barata, en
/// el hilo de la UI) y la escribe en disco en segundo plano tras una pausa al escribir, o cada 10 s como
/// máximo si no se deja de escribir. Si la app se cierra o el equipo se apaga, la consulta se recupera
/// tal cual al abrir. Es un archivo local: no genera escrituras en la base ni tráfico de sincronización
/// mientras se escribe. El contrato del borrador (armarlo y aplicarlo) vive aquí completo; lo del envío
/// entre equipos (marca de enviada, consulta recibida) lo da IDraftHandoffParticipant.
/// </summary>
public partial class ConsultationAutosave : ObservableObject
{
    private readonly ConsultationService? _consultationService;
    private readonly ConsultationViewModel _consultation;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationRecordState _record;
    private readonly ConsultationAccount _account;
    private readonly CatalogStore _catalog;
    private readonly PatientsNavigation _nav;
    private readonly IUiThread _ui;
    private readonly AutosaveOptions _autosaveOptions;
    private readonly IDraftHandoffParticipant _handoff;
    private readonly IPatientsWorkflow _workflow;
    private readonly ConsultationLabOrder _labOrder;

    public ConsultationAutosave(
        ConsultationService? consultationService,
        ConsultationDraftStore? draftStore,
        ConsultationViewModel consultation,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationRecordState record,
        ConsultationAccount account,
        CatalogStore catalog,
        PatientsNavigation navigation,
        IUiThread ui,
        AutosaveOptions autosaveOptions,
        IDraftHandoffParticipant handoff,
        IPatientsWorkflow workflow,
        ConsultationLabOrder? labOrder = null)
    {
        _consultationService = consultationService;
        _draftStore = draftStore;
        _consultation = consultation;
        _session = session;
        _active = active;
        _record = record;
        _account = account;
        _catalog = catalog;
        _nav = navigation;
        _ui = ui;
        _autosaveOptions = autosaveOptions;
        _handoff = handoff;
        _workflow = workflow;
        _labOrder = labOrder ?? new ConsultationLabOrder();
    }

    private static readonly TimeSpan AutosaveMaxWait = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> DraftProperties = new(StringComparer.Ordinal)
    {
        nameof(ConsultationViewModel.ConsultationChiefComplaint),
        nameof(ConsultationViewModel.ConsultationSystemsReview),
        nameof(ConsultationViewModel.ConsultationPhysicalExam),
        nameof(ConsultationViewModel.ConsultationDiagnosis),
        nameof(ConsultationViewModel.ConsultationTreatmentPlan),
        nameof(ConsultationViewModel.ConsultationNotes),
        nameof(ConsultationViewModel.ConsultationGeneralIndications),
        nameof(ConsultationViewModel.ConsultationTA),
        nameof(ConsultationViewModel.ConsultationFC),
        nameof(ConsultationViewModel.ConsultationFR),
        nameof(ConsultationViewModel.ConsultationTemp),
        nameof(ConsultationViewModel.ConsultationWeight),
        nameof(ConsultationViewModel.ConsultationHeight),
        nameof(ConsultationViewModel.ConsultationSpO2),
        nameof(ConsultationViewModel.ConsultationGlucose),
        nameof(ConsultationViewModel.ConsultationSection)
    };

    private readonly object _draftGate = new();
    private ConsultationDraftStore? _draftStore;
    private ITimer? _autosaveTimer;
    private ConsultationDraft? _pendingDraft;
    private long _draftGeneration;

    // Cada copia lleva un número creciente: una copia vieja que termine de escribirse tarde (otro
    // hilo del temporizador) nunca sobrescribe a una más nueva
    private long _draftSequence;
    private long _lastWrittenDraftSequence;
    private DateTime _lastDraftWriteUtc = DateTime.MinValue;
    private bool _draftSessionActive;
    private bool _suspendAutosave;
    private bool _draftRestoreAttempted;

    private DateTime UtcNow => _autosaveOptions.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Texto discreto en la barra de la consulta: "Guardado 10:42:15".</summary>
    [ObservableProperty]
    private string _autosaveStatus = string.Empty;

    /// <summary>La consulta en pantalla se recuperó de un cierre sin terminarla.</summary>
    [ObservableProperty]
    private bool _isConsultationRecovered;

    /// <summary>
    /// Con almacén de borradores, cada cambio de la nota programa la copia. El coordinador lo llama donde
    /// antes corría InitializeAutosave (después de crear los hijos y antes de la regla del alta).
    /// </summary>
    public void Attach()
    {
        if (_draftStore == null) return;
        _consultation.PropertyChanged += OnPropertyChangedForAutosave;
        // La orden de laboratorio (sección 6) también va en la copia
        _labOrder.Changed += ScheduleAutosave;
    }

    private void OnPropertyChangedForAutosave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null) return;
        if (DraftProperties.Contains(e.PropertyName)) ScheduleAutosave();
    }

    /// <summary>Inicio, cargos a la cuenta y cierre de la consulta (desde SyncActiveConsultation).</summary>
    public void OnActiveConsultationStateChangedForAutosave()
    {
        if (_draftStore == null) return;

        if (_active.HasActiveConsultation)
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

    public void ScheduleAutosave()
    {
        if (_draftStore == null || _suspendAutosave || !_active.HasActiveConsultation) return;

        var draft = BuildConsultationDraft();
        if (draft == null) return;
        Volatile.Write(ref _pendingDraft, draft);

        var sinceLastWrite = UtcNow - _lastDraftWriteUtc;
        var due = sinceLastWrite >= AutosaveMaxWait ? TimeSpan.Zero : _autosaveOptions.Delay;
        _autosaveTimer ??= _autosaveOptions.Clock.CreateTimer(_ => WritePendingDraft(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _autosaveTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Guarda ya la consulta en curso (Ctrl+S y al cerrar la aplicación). Regresa con la copia en disco,
    /// aunque el temporizador la esté escribiendo en ese momento.
    /// </summary>
    [RelayCommand]
    public void SaveConsultationDraftNow()
    {
        if (_draftStore == null) return;
        if (_active.HasActiveConsultation && !_suspendAutosave)
        {
            Volatile.Write(ref _pendingDraft, BuildConsultationDraft());
        }
        WritePendingDraft();
    }

    /// <summary>
    /// Escribe ya la copia y dice si quedó en disco. Lo usan los pasos que no deben seguir sin ella
    /// (enviar la consulta a otro equipo, recibirla): una copia vieja pendiente ya no la sobrescribe.
    /// </summary>
    public bool SaveConsultationDraftChecked()
    {
        if (_draftStore == null) return true;
        var draft = BuildConsultationDraft();
        if (draft == null) return false;

        bool saved;
        lock (_draftGate)
        {
            if (draft.Generation != Interlocked.Read(ref _draftGeneration)) return false;
            draft.SavedAt = DateTime.Now;
            saved = _draftStore.Save(draft);
            if (saved)
            {
                _lastDraftWriteUtc = UtcNow;
                _lastWrittenDraftSequence = Math.Max(_lastWrittenDraftSequence, draft.Sequence);
                _draftSessionActive = true;
            }
        }
        if (saved) _handoff.OnReceivedHandoffPersisted(draft);
        return saved;
    }

    public ConsultationDraft? BuildConsultationDraft()
    {
        var active = _consultationService?.CurrentConsultation;
        if (active == null) return null;

        return new ConsultationDraft
        {
            Generation = Interlocked.Read(ref _draftGeneration),
            Sequence = Interlocked.Increment(ref _draftSequence),
            PatientId = active.PatientId,
            PatientName = active.PatientName,
            Folio = active.Folio,
            StartedAt = active.StartedAt,
            ChiefComplaintLabel = active.ChiefComplaint,
            ChiefComplaint = _consultation.ConsultationChiefComplaint ?? string.Empty,
            SystemsReview = _consultation.ConsultationSystemsReview ?? string.Empty,
            PhysicalExam = _consultation.ConsultationPhysicalExam ?? string.Empty,
            Diagnosis = _consultation.ConsultationDiagnosis ?? string.Empty,
            TreatmentPlan = _consultation.ConsultationTreatmentPlan ?? string.Empty,
            Notes = _consultation.ConsultationNotes ?? string.Empty,
            GeneralIndications = _consultation.ConsultationGeneralIndications ?? string.Empty,
            TA = _consultation.ConsultationTA ?? string.Empty,
            FC = _consultation.ConsultationFC ?? string.Empty,
            FR = _consultation.ConsultationFR ?? string.Empty,
            Temp = _consultation.ConsultationTemp ?? string.Empty,
            Weight = _consultation.ConsultationWeight ?? string.Empty,
            Height = _consultation.ConsultationHeight ?? string.Empty,
            SpO2 = _consultation.ConsultationSpO2 ?? string.Empty,
            Glucose = _consultation.ConsultationGlucose ?? string.Empty,
            Section = _consultation.ConsultationSection,
            SavedToRecord = _consultation.IsCurrentConsultationSaved,
            // Copias: el archivo se escribe en otro hilo mientras la consulta sigue cambiando
            Items = active.Items.Select(CloneItem).ToList(),
            Prescriptions = ((_record.Patient ?? _session.SelectedPatient)?.ActivePrescription ?? new List<PatientPrescriptionItem>())
                .Select(ConsultationRecordState.ClonePrescription).ToList(),
            // La receta anterior con su RowId y sus campos desconocidos (paso 8.18): al restaurarla son los mismos renglones
            PreviousPrescriptions = _record.PreviousPrescriptionKnown ? _record.PreviousPrescription.Select(ConsultationRecordState.ClonePrescriptionKeepingIdentity).ToList() : null,
            LabOrder = _labOrder.IsBlank ? null : _labOrder.Snapshot(),
            PatientBefore = _record.SummaryBeforeConsultation,
            AttendingAppointmentId = _consultation.AttendingAppointmentId,
            FeeServiceSyncId = _account.FeeServiceOverrideId is int feeId ? _catalog.Services.FirstOrDefault(s => s.Id == feeId)?.SyncId : null,
            ReceivedHandoffId = _handoff.ReceivedHandoffId,
            ReceivedHandoffToken = _handoff.ReceivedHandoffToken,
            // La marca de "enviada a otro equipo" va en TODAS las copias (también al cerrar el
            // programa): sin ella, al volver a abrir la consulta sería editable aquí y allá
            Handoff = CloneMarker(_handoff.OutgoingMarker)
        };
    }

    private void WritePendingDraft()
    {
        if (_draftStore == null) return;

        ConsultationDraft? draft;
        bool saved;
        lock (_draftGate)
        {
            // La copia se toma dentro del candado. Si el temporizador se lleva la que Guardar ya acaba de
            // dejar, Guardar ya espera aquí a que termine de escribirse; antes regresaba sin ella en disco
            // y, al cerrar el programa, esa escritura podía quedar a medias y perderse lo último escrito
            draft = Interlocked.Exchange(ref _pendingDraft, null);
            if (draft == null) return;

            // Si la consulta terminó mientras esperaba, no se resucita su borrador
            if (draft.Generation != Interlocked.Read(ref _draftGeneration)) return;
            if (draft.Sequence <= _lastWrittenDraftSequence) return;
            draft.SavedAt = DateTime.Now;
            saved = _draftStore.Save(draft);
            if (saved)
            {
                _lastDraftWriteUtc = UtcNow;
                _lastWrittenDraftSequence = draft.Sequence;
            }
        }
        if (saved) _handoff.OnReceivedHandoffPersisted(draft);

        var status = saved
            ? $"Guardado {draft.SavedAt:HH:mm:ss}"
            : "No se pudo guardar automáticamente";
        _ui.Post(() =>
        {
            // Bajo el mismo candado que el cierre: un guardado que termina justo al cobrar no
            // deja el indicador "Guardado" en una consulta ya cerrada
            lock (_draftGate)
            {
                if (draft.Generation == Interlocked.Read(ref _draftGeneration) && _active.HasActiveConsultation)
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
            _autosaveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _draftStore?.Delete();
            _lastDraftWriteUtc = DateTime.MinValue;
            AutosaveStatus = string.Empty;
            IsConsultationRecovered = false;
        }
    }

    /// <summary>
    /// Al abrir la app: si quedó una consulta sin terminar, se retoma con su nota, signos, receta,
    /// cuenta, folio y hora de inicio. Si se había enviado a otro equipo, vuelve en solo lectura y
    /// el envío se resuelve contra la nube.
    /// </summary>
    public void TryRestoreConsultationDraft()
    {
        if (_draftStore == null || _draftRestoreAttempted || _consultationService == null) return;
        _draftRestoreAttempted = true;
        if (_active.HasActiveConsultation) return;

        var draft = _draftStore.Load();
        if (draft == null) return;

        var patient = _session.Patients.FirstOrDefault(p => p.Id == draft.PatientId);
        if (patient == null)
        {
            if (draft.Handoff != null)
            {
                // Enviada a otro equipo: no se puede volver a abrir aquí, pero tampoco se borra sin rastro
                _draftStore.ArchiveDraft($"{draft.Handoff.Id}-sin-expediente");
                AppLogger.Warn($"Consulta enviada {draft.Folio} apartada: el expediente {draft.PatientId} ya no está activo en este equipo.");
            }
            else
            {
                AppLogger.Warn($"Se descartó la consulta en curso {draft.Folio}: el expediente {draft.PatientId} ya no existe.");
            }
            _draftStore.Delete();
            return;
        }

        ApplyConsultationDraft(patient, draft);
        if (draft.Handoff != null) _handoff.RestoreOutgoingHandoff(draft.Handoff);
        // Una consulta recibida que ya estaba guardada aquí: su copia de la nube ya no hace falta
        _handoff.OnReceivedHandoffPersisted(draft);

        IsConsultationRecovered = true;
        _nav.PatientsPage = PatientsNavigation.PageConsultation;
        AutosaveStatus = draft.Handoff != null
            ? $"Enviada a {draft.Handoff.ToDeviceName}"
            : $"Consulta recuperada · iniciada {draft.StartedAt:dd/MM HH:mm}";
        AppLogger.Info($"Consulta en curso recuperada: {draft.Folio} ({draft.PatientName}).");
    }

    /// <summary>
    /// Pone en pantalla una copia de la consulta: al recuperarla tras un cierre o al recibirla de otro
    /// equipo (con los Id ya traducidos a los de este equipo).
    /// </summary>
    public void ApplyConsultationDraft(PatientRecord patient, ConsultationDraft draft)
    {
        _suspendAutosave = true;
        try
        {
            // Antes de iniciarla: la cuenta se muestra ya con el honorario y la cita correctos
            _account.FeeServiceOverrideId = draft.FeeServiceSyncId is Guid feeSyncId
                ? _catalog.Services.FirstOrDefault(s => s.IsActive && s.SyncId == feeSyncId)?.Id
                : null;
            _handoff.ReceivedHandoffId = draft.ReceivedHandoffId;
            _handoff.ReceivedHandoffToken = draft.ReceivedHandoffToken;
            _session.SelectPatient(patient);
            var active = _consultationService!.StartConsultation(patient, draft.ChiefComplaintLabel);
            if (!string.IsNullOrWhiteSpace(draft.Folio)) active.Folio = draft.Folio;
            if (draft.StartedAt != default) active.StartedAt = draft.StartedAt;
            foreach (var item in draft.Items.Where(i => i.Quantity > 0))
            {
                active.Items.Add(CloneItem(item));
            }
            active.NotifyItemsChanged();

            _consultation.ClearConsultationFields();
            _consultation.ConsultationFolio = active.Folio;
            _consultation.ConsultationChiefComplaint = draft.ChiefComplaint;
            _consultation.ConsultationSystemsReview = draft.SystemsReview;
            _consultation.ConsultationPhysicalExam = draft.PhysicalExam;
            _consultation.ConsultationDiagnosis = draft.Diagnosis;
            _consultation.ConsultationTreatmentPlan = draft.TreatmentPlan;
            _consultation.ConsultationNotes = draft.Notes;
            _consultation.ConsultationGeneralIndications = draft.GeneralIndications;
            _consultation.ConsultationTA = draft.TA;
            _consultation.ConsultationFC = draft.FC;
            _consultation.ConsultationFR = draft.FR;
            _consultation.ConsultationTemp = draft.Temp;
            _consultation.ConsultationWeight = draft.Weight;
            _consultation.ConsultationHeight = draft.Height;
            _consultation.ConsultationSpO2 = draft.SpO2;
            _consultation.ConsultationGlucose = draft.Glucose;

            _labOrder.Load(draft.LabOrder);
            _record.ResumeConsultationOnRecord(patient, draft);

            _consultation.ConsultationSection = Math.Clamp(draft.Section, 1, ConsultationViewModel.ConsultationSectionCount);
            _consultation.IsCurrentConsultationSaved = draft.SavedToRecord;
            _consultation.AttendingAppointmentId = draft.AttendingAppointmentId;
            _draftSessionActive = true;
            _workflow.SyncActiveConsultation();
        }
        finally
        {
            _suspendAutosave = false;
        }
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
        AddedAt = item.AddedAt,
        CatalogSyncId = item.CatalogSyncId,
        LineKind = item.LineKind
    };

    private static ConsultationHandoffMarker? CloneMarker(ConsultationHandoffMarker? marker) => marker == null ? null : new()
    {
        Id = marker.Id,
        ToDeviceId = marker.ToDeviceId,
        ToDeviceName = marker.ToDeviceName,
        FromDeviceId = marker.FromDeviceId,
        FromDeviceName = marker.FromDeviceName,
        DatabaseUrl = marker.DatabaseUrl,
        PatientSyncId = marker.PatientSyncId,
        PatientName = marker.PatientName,
        Folio = marker.Folio,
        Phase = marker.Phase,
        CreatedAt = marker.CreatedAt
    };

    /// <summary>Al cerrar el coordinador, después de guardar el borrador.</summary>
    public void DisposeTimer() => _autosaveTimer?.Dispose();
}
