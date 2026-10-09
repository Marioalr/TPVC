using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Helpers.WpfPlatform;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

public partial class MedicalRecordsViewModel : ObservableObject, IDisposable
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly Action? _onInventoryChangedHandler;
    private readonly Action? _onProfileChangedHandler;

    // Hilo de la interfaz: se crea con el ViewModel, en el mismo hilo y momento en que antes se tomaba
    // el contexto de sincronización del guardado automático (el constructor no cambia)
    private readonly IUiThread _ui = new WpfUiThread();

    // Confirmaciones y enlaces (WPF: DialogService y el programa del sistema)
    private readonly IUserDialogs _dialogs = new WpfUserDialogs();
    private readonly IUrlLauncher _urls = new ShellUrlLauncher();

    public Action? OnBackToHubRequested { get; set; }

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    // El alta y la edición de expediente viven en PatientForm (PatientFormViewModel).

    // La nota de la consulta en curso, el CIE-10, las secciones, el cobro (Checkout) y "Terminar consulta"
    // viven en Consultation (ConsultationViewModel).

    public MedicalRecordsViewModel() : this(null, null, null, null, null, null)
    {
    }

    public MedicalRecordsViewModel(
        InventoryService? inventoryService, 
        ConsultationService? consultationService = null, 
        ClinicProfileService? clinicProfileService = null,
        PrintingService? printingService = null,
        ConsultationDraftStore? consultationDraftStore = null,
        NotificationService? notificationService = null,
        ConsultationHandoffService? consultationHandoffService = null)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        CreateChildren(notificationService, clinicProfileService, consultationDraftStore, printingService);
        InitializeAutosave(consultationDraftStore);
        PatientForm.AttachPropertyRules();
        InitializePacientes();
        InitializeHandoff(consultationHandoffService);
        _clinicProfileService = clinicProfileService;

        if (_clinicProfileService != null)
        {
            _onProfileChangedHandler = () => Clinic.RefreshProfileData();
            _clinicProfileService.ProfileChanged += _onProfileChangedHandler;
            Clinic.RefreshProfileData();
        }

        if (_consultationService != null)
        {
            _consultationService.OnActiveConsultationChanged += SyncActiveConsultation;
        }

        if (_inventoryService != null)
        {
            _onInventoryChangedHandler = () =>
            {
                try
                {
                    if (_ui.IsOffUiThread)
                    {
                        _ui.InvokeAsync(async () => await LoadPatientsAsync());
                        return;
                    }
                }
                catch
                {
                    // Fallback para entornos donde WPF Application/Dispatcher no está disponible (ej. Linux tests)
                }

                _ = LoadPatientsAsync();
            };
            _inventoryService.OnPatientsChanged += _onInventoryChangedHandler;
            InitialLoad = LoadInitialDataAsync();
        }
        else
        {
            Catalog.LoadFallbackServices();
            if (_consultationService != null && _consultationService.Patients.Count > 0)
            {
                Session.Patients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
                if (_consultationService.HasActiveConsultation)
                {
                    var activeName = _consultationService.CurrentConsultation?.PatientName;
                    Session.SelectedPatient = Session.Patients.FirstOrDefault(p => p.FullName == activeName);
                }
                else
                {
                    Session.SelectedPatient = null;
                }
            }
            else
            {
                Session.LoadMockPatients();
            }
        }

        SyncActiveConsultation();
    }

    public async Task LoadInitialDataAsync()
    {
        await LoadPatientsAsync();
        await LoadServicesAsync();
        await Templates.LoadPrescriptionTemplatesAsync();
        await Home.LoadTodayAppointmentsAsync();
        Autosave.TryRestoreConsultationDraft();
        await PatientForm.TryRestorePatientFormDraftAsync();
    }

    public async Task LoadPatientsAsync()
    {
        if (_inventoryService != null)
        {
            // Listado ligero: las notas de consulta solo se cargan para el expediente abierto
            var dbPatients = await _inventoryService.GetPatientSummariesAsync();
            var prevSelected = Session.SelectedPatient;
            var prevSelectedId = prevSelected?.Id ?? 0;

            // Se conserva la misma instancia del paciente seleccionado (la UI y los formularios
            // abiertos trabajan sobre ella); solo se toma de la base su historial de consultas.
            PatientRecord? keptSelected = null;
            bool replacedSelected = false;
            if (prevSelected != null && prevSelectedId > 0)
            {
                var index = dbPatients.FindIndex(p => p.Id == prevSelectedId);
                if (index >= 0)
                {
                    // Si otro equipo cambió el expediente (llegó con la sincronización), se toma la
                    // versión de la base: guardar después la instancia vieja borraría esos cambios.
                    // Se decide después de leer: mientras tanto pudo cambiar la selección o iniciarse
                    // una consulta con ese paciente.
                    var fresh = dbPatients[index];
                    var consultations = await _inventoryService.GetPatientConsultationsAsync(prevSelectedId);
                    bool forced = Session.ForceFreshPatientIds.Contains(prevSelectedId);
                    if (ReferenceEquals(Session.SelectedPatient, prevSelected)
                        && (forced || fresh.UpdatedAtUtc > prevSelected.UpdatedAtUtc)
                        && CanReplaceSelectedPatientInstance(prevSelected))
                    {
                        Session.ForceFreshPatientIds.Remove(prevSelectedId);
                        fresh.Consultations = consultations;
                        keptSelected = fresh;
                        replacedSelected = true;
                    }
                    else
                    {
                        prevSelected.Consultations = consultations;
                        dbPatients[index] = prevSelected;
                        keptSelected = prevSelected;
                    }
                }
            }

            Session.Patients = new ObservableCollection<PatientRecord>(dbPatients);

            _consultationService?.LoadPatientsFromDatabase(dbPatients);

            if (prevSelectedId > 0)
            {
                if (!ReferenceEquals(Session.SelectedPatient, prevSelected))
                {
                    // Se eligió otro paciente mientras se leía la base: no se toca su ficha
                }
                else if (keptSelected == null)
                {
                    Session.SelectedPatient = null; // El expediente fue dado de baja
                }
                else if (replacedSelected)
                {
                    Chart.ReplaceSelectedPatientKeepingView(keptSelected);
                }
                else
                {
                    Session.RefreshConsultationsHistory(keptSelected);
                }
            }
            else if (_consultationService != null && _consultationService.HasActiveConsultation)
            {
                var activeName = _consultationService.CurrentConsultation?.PatientName;
                Session.SelectedPatient = Session.Patients.FirstOrDefault(p => p.FullName == activeName);
            }
        }
    }

    /// <summary>
    /// La instancia abierta se puede cambiar por la de la base salvo que alguien trabaje sobre ella
    /// (alta o edición abierta, o es el paciente de la consulta en curso).
    /// </summary>
    private bool CanReplaceSelectedPatientInstance(PatientRecord patient) =>
        !PatientForm.IsPatientModalOpen
        && (Record.Patient == null || Record.Patient.Id != patient.Id)
        && !(Active.HasActiveConsultation && Active.ActiveConsultation?.PatientId == patient.Id);

    private bool NeedsConsultationsLoad(PatientRecord patient) =>
        _inventoryService != null && patient.Id > 0 && patient.Consultations.Count == 0 && patient.ConsultationCount > 0;

    /// <summary>Carga bajo demanda el historial de un expediente del listado ligero.</summary>
    private async Task LoadConsultationsForAsync(PatientRecord patient, bool refreshSelectedHistory)
    {
        if (_inventoryService == null) return;
        try
        {
            patient.Consultations = await _inventoryService.GetPatientConsultationsAsync(patient.Id);
            if (refreshSelectedHistory && ReferenceEquals(Session.SelectedPatient, patient))
            {
                Session.RefreshConsultationsHistory(patient);
                RefreshVitalSignsEvolution(patient);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"No se pudo cargar el historial del expediente {patient.RecordNumber}", ex);
        }
    }

    public void SyncActiveConsultation()
    {
        Active.RaiseConsultationChanged();
        NotifyAccountChanged();

        if (Active.HasActiveConsultation)
        {
            Active.StartConsultationTimer();
        }
        else
        {
            Active.StopConsultationTimer();
            Account.FeeServiceOverrideId = null;
            _receivedHandoffId = null;
            _receivedHandoffToken = null;
            // Cualquier otro cierre (p. ej. cobrada desde el Punto de Venta) también cierra su receta
            if (Record.Patient != null) _ = Record.EndConsultationOnRecordAsync(discarded: false);
        }

        Autosave.OnActiveConsultationStateChangedForAutosave();
        NotifyPatientsPageState();
        NotifyPrescriptionStateChanged();
        NotifyHandoffState();
    }

    public void Dispose()
    {
        if (_consultationService != null)
        {
            _consultationService.OnActiveConsultationChanged -= SyncActiveConsultation;
        }
        if (_inventoryService != null && _onInventoryChangedHandler != null)
        {
            _inventoryService.OnPatientsChanged -= _onInventoryChangedHandler;
        }
        if (_clinicProfileService != null && _onProfileChangedHandler != null)
        {
            _clinicProfileService.ProfileChanged -= _onProfileChangedHandler;
        }
        Active.StopConsultationTimer();
        DisposePacientes();
        DisposeHandoff();
        FlushConsultationDraft();
        Autosave.DisposeTimer();
        PatientForm.DisposeFormDraftTimer();
        GC.SuppressFinalize(this);
    }

    public async Task LoadServicesAsync()
    {
        await Catalog.LoadAsync();

        Prescription.ApplyPrescriptionSuggestions(Prescription.PrescriptionSearchText ?? string.Empty);
        Supplies.ApplySupplySuggestions(Supplies.SupplySearchText ?? string.Empty);
        ClinicalServices.ApplyServiceModalSuggestions(ClinicalServices.ServiceModalSearchText ?? string.Empty);
        CatalogBrowser.ApplyCatalogBrowserFilter();
    }

    /// <summary>Se cobró la consulta (o se terminó sin cargos): se cierra y se vuelve al inicio.</summary>
    private async Task OnConsultationChargedAsync(SaleReceipt? receipt)
    {
        await HandleSaleCompletedFromConsultationAsync();
        if (receipt != null) Feedback.ShowSuccess($"Consulta cobrada · {receipt.Folio}");
    }

    public async Task HandleSaleCompletedFromConsultationAsync()
    {
        try
        {
            // 0. Si por alguna razón no se guardó la nota de consulta en BD, asegurarse de persistirla
            if (!Consultation.IsCurrentConsultationSaved && Session.SelectedPatient != null)
            {
                try
                {
                    await Consultation.SaveActiveConsultationToDbAsync(DateTime.Now);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("No se pudo guardar la nota de consulta al cobrar", ex);
                }
            }
            Consultation.IsCurrentConsultationSaved = false;
            await Record.EndConsultationOnRecordAsync(discarded: false);

            // 1. Cerrar "Terminar consulta" y cualquier otra hoja secundaria
            Consultation.CloseFinishConsultation();
        Prescription.IsPrescriptionModalOpen = false;
        Supplies.IsSuppliesModalOpen = false;
        ClinicalServices.IsServicesModalOpen = false;
        PatientForm.IsPatientModalOpen = false;
        CatalogBrowser.IsCatalogBrowserModalOpen = false;
        Info.DismissInfoModal();

        // 2. Finalizar y cerrar la consulta médica en el servicio y detener cronómetro
        _consultationService?.CloseActiveConsultation();
        SyncActiveConsultation();
        Active.StopConsultationTimer();

        // 3. Limpiar los campos de la consulta
        Consultation.ClearConsultationFields();

        // 4. Salir de todo y volver a la vista inicial (sin paciente seleccionado)
        Session.SelectedPatient = null;
        Session.ConsultationsHistory.Clear();

        // 6. La cita que se atendía queda como atendida y se vuelve al inicio, listo para el siguiente
        await CompleteAttendedAppointmentAsync();
        Navigation.GoToPatientsHome();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al finalizar consulta tras venta completada", ex);
        }
    }

    // Las hojas de la consulta viven en Prescription (receta, alerta de alergia, posología, receta
    // anterior y "Se surte aquí"), Supplies (insumos) y ClinicalServices (servicios).

    // El historial de citas del paciente es de Chart (el panel de agenda y el estado de las citas, de Agenda).

    // ==========================================
    // EVOLUCIÓN DE SIGNOS VITALES (HISTORIAL Y GRÁFICAS): VitalsChart
    // ==========================================

    /// <summary>Recalcula la gráfica de signos del paciente dado o, si no se da, del paciente abierto.</summary>
    public void RefreshVitalSignsEvolution(PatientRecord? patient = null) => VitalsChart.RefreshVitalSignsEvolution(patient ?? Session.SelectedPatient);
}
