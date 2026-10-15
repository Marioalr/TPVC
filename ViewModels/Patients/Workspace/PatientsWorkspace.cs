using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Coordinador del apartado Pacientes (fase 6, plan §1.3), sin WPF. Crea una sola vez a sus hijos (los
/// dueños de cada parte de la pantalla), corre los flujos que cruzan a varios dueños en el orden de
/// siempre (PatientsWorkspace.Flows.cs) y responde los puertos con que los hijos le avisan o le piden
/// algo (.Hubs.cs y .Ports.cs). Lo de la plataforma (hilo de la interfaz, confirmaciones, enlaces e
/// impresión de la receta) llega en PatientsPlatform: en WPF lo arma MedicalRecordsViewModel, la subclase
/// que usan las vistas, MainViewModel y App; otra interfaz (MAUI) armaría la suya.
/// </summary>
public partial class PatientsWorkspace : ObservableObject, IDisposable, IPatientsWorkflow, IPatientsHubs, IConsultationGate, IDraftHandoffParticipant, IPatientsSection
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly Action? _onInventoryChangedHandler;
    private readonly Action? _onProfileChangedHandler;

    // Hilo de la interfaz: llega con la plataforma, que se crea con el ViewModel (en WPF, en el mismo hilo
    // y momento en que antes se tomaba el contexto de sincronización del guardado automático)
    private readonly IUiThread _ui;

    // Confirmaciones y enlaces (WPF: DialogService y el programa del sistema)
    private readonly IUserDialogs _dialogs;
    private readonly IUrlLauncher _urls;

    public Action? OnBackToHubRequested { get; set; }

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    // El alta y la edición de expediente viven en PatientForm (PatientFormViewModel).

    // La nota de la consulta en curso, el CIE-10, las secciones, el cobro (Checkout) y "Terminar consulta"
    // viven en Consultation (ConsultationViewModel).

    public PatientsWorkspace(
        PatientsPlatform platform,
        InventoryService? inventoryService,
        ConsultationService? consultationService = null,
        ClinicProfileService? clinicProfileService = null,
        ConsultationDraftStore? consultationDraftStore = null,
        NotificationService? notificationService = null,
        ConsultationHandoffService? consultationHandoffService = null,
        LocalPreferencesStore? localPreferences = null,
        LabResultService? labResults = null)
    {
        _ui = platform.Ui;
        _dialogs = platform.Dialogs;
        _urls = platform.Urls;
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        CreateChildren(notificationService, clinicProfileService, consultationDraftStore, platform.Printer, localPreferences, labResults, platform.LabFiles);
        LabResults.Attach();
        Autosave.Attach();
        PatientForm.AttachPropertyRules();
        InitializePacientes();
        Handoff.InitializeHandoff(consultationHandoffService);
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
        Handoff.DisposeHandoff();
        LabResults.Detach();
        FlushConsultationDraft();
        Autosave.DisposeTimer();
        PatientForm.DisposeFormDraftTimer();
        GC.SuppressFinalize(this);
    }

    // ================================================================== citas (suscripción del coordinador)

    private Action? _onAppointmentsChangedHandler;
    private Action? _onSyncNoticesChangedHandler;

    private void InitializePacientes()
    {
        if (_inventoryService != null)
        {
            // Paso 8.23: lo que llegó de otro equipo y choca con lo de aquí (citas empalmadas y posibles duplicados): los avisos
            // del inicio de Pacientes, que se quedan hasta que el médico los descarta
            _onSyncNoticesChangedHandler = () =>
            {
                try
                {
                    if (_ui.IsOffUiThread)
                    {
                        _ui.InvokeAsync(async () => await Home.LoadSyncNoticesAsync());
                        return;
                    }
                }
                catch
                {
                    // Sin WPF (pruebas en Linux)
                }
                _ = Home.LoadSyncNoticesAsync();
            };
            _inventoryService.OnSyncNoticesChanged += _onSyncNoticesChangedHandler;

            _onAppointmentsChangedHandler = () =>
            {
                try
                {
                    if (_ui.IsOffUiThread)
                    {
                        _ui.InvokeAsync(async () => await RefreshAppointmentViewsAsync());
                        return;
                    }
                }
                catch
                {
                    // Sin WPF (pruebas en Linux)
                }
                _ = RefreshAppointmentViewsAsync();
            };
            _inventoryService.OnAppointmentsChanged += _onAppointmentsChangedHandler;
        }
    }

    private void DisposePacientes()
    {
        if (_inventoryService != null && _onAppointmentsChangedHandler != null)
        {
            _inventoryService.OnAppointmentsChanged -= _onAppointmentsChangedHandler;
        }
        if (_inventoryService != null && _onSyncNoticesChangedHandler != null)
        {
            _inventoryService.OnSyncNoticesChanged -= _onSyncNoticesChangedHandler;
        }
    }

    /// <summary>
    /// Paso 8.23: «Ver en la agenda» del aviso de citas empalmadas: abre la agenda en el día del primer empalme (el aviso se
    /// queda hasta «Entendido»).
    /// </summary>
    [RelayCommand]
    public Task ShowOverlapsInAgendaAsync() =>
        Agenda.OpenAgendaOnDateAsync(Home.OverlapPairs.FirstOrDefault()?.First.Date ?? ClinicClock.Today);

    // ================================================================== páginas

    // Las páginas son de Navigation; los nombres siguen aquí para quien ya los usa
    public const string PageHome = PatientsNavigation.PageHome;
    public const string PageChart = PatientsNavigation.PageChart;
    public const string PageForm = PatientsNavigation.PageForm;
    public const string PageConsultation = PatientsNavigation.PageConsultation;

    /// <summary>Aviso "Consulta en curso · Volver" cuando se está fuera de la consulta.</summary>
    public bool ShowsConsultationBanner => Active.HasActiveConsultation && !Navigation.IsConsultationPage && !Navigation.IsFormPage;

    public string ConsultationBannerText => !Active.HasActiveConsultation
        ? string.Empty
        : Handoff.IsConsultationHandedOff
            ? $"Consulta de {Active.ActiveConsultationPatientName} enviada a {Handoff.HandoffTargetName}: esperando que la acepte"
            : $"Consulta en curso: {Active.ActiveConsultationPatientName} · {Active.ActiveConsultationElapsedTime}";

    // ================================================================== guardado automático y carga inicial

    // Guardado automático de la consulta en curso: vive en Autosave (ConsultationAutosave), con todo el
    // contrato del borrador. Aquí quedan la carga inicial, la pausa compartida con el alta de paciente y el
    // guardado al cerrar el programa (FlushConsultationDraft, en .Flows.cs; el constructor lo conecta con
    // Autosave.Attach()).

    // La pausa es la misma para la consulta y el alta de paciente (PatientForm la lee de aquí)
    private readonly AutosaveOptions _autosaveOptions = new();

    /// <summary>Pausa al escribir antes de guardar (las pruebas la acortan).</summary>
    public TimeSpan AutosaveDelay
    {
        get => _autosaveOptions.Delay;
        set => _autosaveOptions.Delay = value;
    }

    /// <summary>
    /// Reloj del guardado automático de la consulta (las pruebas ponen uno manual). Se pone antes de
    /// iniciar la consulta: el temporizador se crea con la primera copia.
    /// </summary>
    public TimeProvider AutosaveClock
    {
        get => _autosaveOptions.Clock;
        set => _autosaveOptions.Clock = value;
    }

    /// <summary>Carga inicial lanzada por el constructor (expedientes, servicios y consulta recuperada).</summary>
    public Task InitialLoad { get; private set; } = Task.CompletedTask;

    // ================================================================== hijos

    // Hijos del coordinador (fase 6, plan §1.2). Se crean una sola vez, lo primero del constructor (antes
    // de que algo pueda poner un paciente), y sin efectos: no se suscriben a servicios ni ponen
    // propiedades que disparen ganchos. Cada uno se expone con una propiedad de solo lectura que nunca se
    // reemplaza; las vistas llegan a sus miembros por esa ruta.

    private InfoDialogState _info;
    private PatientsFeedback _feedback;
    private PatientsNavigation _navigation;
    private ClinicInfo _clinic;
    private CatalogStore _catalog;
    private PrescriptionTemplateStore _templates;
    private PatientSession _session;
    private ActiveConsultationSession _active;
    private ConsultationRecordState _record;
    private ConsultationAccount _account;
    private VitalSignsChartViewModel _vitalsChart;
    private CatalogBrowserViewModel _catalogBrowser;
    private PrescriptionPreviewViewModel _preview;
    private ConsultationDetailViewModel _consultationDetail;
    private AgendaPanelViewModel _agenda;
    private PatientFormViewModel _patientForm;
    private PatientsHomeViewModel _home;
    private PatientChartViewModel _chart;
    private SuppliesSheetViewModel _supplies;
    private ClinicalServicesSheetViewModel _clinicalServices;
    private PrescriptionSheetViewModel _prescription;
    private ConsultationLabOrder _labOrder;
    private LabOrderViewModel _labs;
    private ConsultationViewModel _consultation;
    private ConsultationAutosave _autosave;
    private ConsultationHandoffViewModel _handoff;
    private LabResultsViewModel _labResults;

    /// <summary>Ventana de aviso ("Notificación clínica").</summary>
    public InfoDialogState Info => _info;

    /// <summary>Avisos breves (nota que se va sola o, sin servicio de avisos, la ventana de aviso).</summary>
    public PatientsFeedback Feedback => _feedback;

    /// <summary>Página que se ve (inicio, ficha, alta o consulta).</summary>
    public PatientsNavigation Navigation => _navigation;

    /// <summary>Médico y consultorio (receta, recordatorios de citas).</summary>
    public ClinicInfo Clinic => _clinic;

    /// <summary>Servicios clínicos y productos del inventario.</summary>
    public CatalogStore Catalog => _catalog;

    /// <summary>Plantillas de receta del consultorio (posologías, medidas generales, dosis pediátrica).</summary>
    public PrescriptionTemplateStore Templates => _templates;

    /// <summary>Expedientes, paciente seleccionado y su historial de consultas.</summary>
    public PatientSession Session => _session;

    /// <summary>Consulta en curso (folio, paciente, total) y su cronómetro.</summary>
    public ActiveConsultationSession Active => _active;

    /// <summary>El expediente durante la consulta en curso (receta anterior y resumen de antes).</summary>
    public ConsultationRecordState Record => _record;

    /// <summary>Cuenta de la consulta: honorario y cargos (receta que se surte aquí, insumos y servicios).</summary>
    public ConsultationAccount Account => _account;

    /// <summary>Evolución de signos vitales de la ficha (gráfica y tabla).</summary>
    public VitalSignsChartViewModel VitalsChart => _vitalsChart;

    /// <summary>Explorador del catálogo (medicamentos, insumos o servicios) que abren las hojas.</summary>
    public CatalogBrowserViewModel CatalogBrowser => _catalogBrowser;

    /// <summary>Vista previa de la receta, su impresión y su exportación.</summary>
    public PrescriptionPreviewViewModel Preview => _preview;

    /// <summary>La hoja de una consulta del historial: el detalle por datos y su receta (corregir e imprimir).</summary>
    public ConsultationDetailViewModel ConsultationDetail => _consultationDetail;

    /// <summary>Agenda de la semana (panel lateral).</summary>
    public AgendaPanelViewModel Agenda => _agenda;

    /// <summary>Alta y edición de expediente (página del formulario y su borrador).</summary>
    public PatientFormViewModel PatientForm => _patientForm;

    /// <summary>Inicio: buscador, pacientes recientes y citas de hoy.</summary>
    public PatientsHomeViewModel Home => _home;

    /// <summary>Ficha del paciente: antecedentes, historial, lo esencial, última receta y citas.</summary>
    public PatientChartViewModel Chart => _chart;

    /// <summary>Hoja de insumos y material clínico.</summary>
    public SuppliesSheetViewModel Supplies => _supplies;

    /// <summary>Hoja de servicios y procedimientos clínicos.</summary>
    public ClinicalServicesSheetViewModel ClinicalServices => _clinicalServices;

    /// <summary>Hoja de receta (posología, dosis pediátrica, alerta de alergia, receta anterior y "Se surte aquí").</summary>
    public PrescriptionSheetViewModel Prescription => _prescription;

    /// <summary>Laboratorio de la consulta en curso (sección 6): estudios, indicaciones y su solicitud impresa.</summary>
    public LabOrderViewModel Labs => _labs;

    /// <summary>Consulta en curso: la nota, sus secciones, el CIE-10 y "Terminar consulta" con su cobro (Checkout).</summary>
    public ConsultationViewModel Consultation => _consultation;

    /// <summary>Guardado automático de la consulta en curso (borrador local, indicador y consulta recuperada).</summary>
    public ConsultationAutosave Autosave => _autosave;

    /// <summary>Resultados de laboratorio del expediente (paso 8.12b): la lista de la ficha, agregar, abrir y quitar.</summary>
    public LabResultsViewModel LabResults => _labResults;

    /// <summary>Consulta entre equipos: "Enviar a…", solo lectura mientras está en otro equipo, recuperar, liberar y recibir.</summary>
    public ConsultationHandoffViewModel Handoff => _handoff;

    [MemberNotNull(nameof(_info), nameof(_feedback), nameof(_navigation), nameof(_clinic), nameof(_catalog), nameof(_templates), nameof(_session), nameof(_active), nameof(_record), nameof(_account), nameof(_vitalsChart), nameof(_catalogBrowser), nameof(_preview), nameof(_consultationDetail), nameof(_agenda), nameof(_patientForm), nameof(_home), nameof(_chart), nameof(_supplies), nameof(_clinicalServices), nameof(_prescription), nameof(_labOrder), nameof(_labs), nameof(_consultation), nameof(_autosave), nameof(_handoff), nameof(_labResults))]
    private void CreateChildren(NotificationService? notificationService, ClinicProfileService? clinicProfileService, ConsultationDraftStore? consultationDraftStore,
        IPrescriptionOutput? printingService, LocalPreferencesStore? localPreferences, LabResultService? labResults, ILabFilePicker? labFiles)
    {
        _info = new InfoDialogState(_ui);
        _feedback = new PatientsFeedback(notificationService, _info);
        _navigation = new PatientsNavigation(this);
        _clinic = new ClinicInfo(clinicProfileService);
        _catalog = new CatalogStore(_inventoryService, this);
        _templates = new PrescriptionTemplateStore(_inventoryService, this);
        _session = new PatientSession(this);
        _active = new ActiveConsultationSession(_consultationService, _ui, this);
        _record = new ConsultationRecordState(_session, _inventoryService, this);
        _account = new ConsultationAccount(_consultationService, _active, _catalog, _info, this, this);
        _vitalsChart = new VitalSignsChartViewModel(this);
        // Lo elegido en el catálogo se agrega con la hoja que lo abrió (las hojas se crean después:
        // usan el explorador para abrir el catálogo). El medicamento solo se elige: la indicación se escribe en
        // la hoja de receta y ahí se agrega (pedido del 2026-10-14)
        _catalogBrowser = new CatalogBrowserViewModel(_catalog)
        {
            ChooseMedication = product => Prescription.ChoosePrescriptionProduct(product),
            AddSupply = product => Supplies.SelectAndAddSupplyProductAsync(product),
            AddService = service => ClinicalServices.SelectAndAddServiceModalSuggestionAsync(service)
        };
        // Las indicaciones de la receta leen el plan que escribe el médico en la consulta (Consultation se
        // crea después: se lee al momento)
        _preview = new PrescriptionPreviewViewModel(_session, _active, _clinic, _info, clinicProfileService, printingService, () => Consultation.ConsultationTreatmentPlan);
        _consultationDetail = new ConsultationDetailViewModel(_inventoryService, _session, _active, _catalog, _clinic, _info, _feedback, clinicProfileService, printingService);
        _agenda = new AgendaPanelViewModel(_inventoryService, _session, _info, _feedback, _clinic, _dialogs, _urls, this);
        _patientForm = new PatientFormViewModel(_inventoryService, _consultationService, consultationDraftStore, _session, _navigation, _active,
            _info, _feedback, this, _dialogs, _ui, _autosaveOptions, this);
        _home = new PatientsHomeViewModel(_inventoryService, _session, _navigation, this);
        _chart = new PatientChartViewModel(_inventoryService, _session, _active, _record, _navigation, _info, _feedback, this, _dialogs, this);
        _supplies = new SuppliesSheetViewModel(_inventoryService, _catalog, _session, _active, _account, _info, _feedback, _catalogBrowser, this, this);
        _clinicalServices = new ClinicalServicesSheetViewModel(_inventoryService, _catalog, _session, _active, _account, _info, _feedback, _catalogBrowser, this, this);
        // La dosis pediátrica sugiere el peso que se capturó en la consulta en curso (se lee al momento)
        _prescription = new PrescriptionSheetViewModel(_inventoryService, _catalog, _templates, _session, _active, _record, _account, _info, _feedback,
            _catalogBrowser, this, this, () => Consultation.ConsultationWeight);
        // La orden de laboratorio se guarda con la nota y viaja en el borrador; la hoja lleva el diagnóstico que se
        // escribe en la consulta (se lee al momento)
        _labOrder = new ConsultationLabOrder();
        _labs = new LabOrderViewModel(_inventoryService, _labOrder, _session, _clinic, _info, _feedback, clinicProfileService, printingService,
            () => Consultation.ConsultationDiagnosis);
        // Al final: el cobro (Checkout) se arma con la consulta, como antes justo después de los hijos y
        // antes de cualquier suscripción
        _consultation = new ConsultationViewModel(_inventoryService, _consultationService, clinicProfileService, _session, _active, _record, _account,
            _catalog, _navigation, _info, _feedback, this, _dialogs, _ui, this, localPreferences, _labOrder);
        _autosave = new ConsultationAutosave(_consultationService, consultationDraftStore, _consultation, _session, _active, _record, _account, _catalog,
            _navigation, _ui, _autosaveOptions, this, this, _labOrder);
        // Al final: el envío entre equipos usa la consulta y su borrador
        _handoff = new ConsultationHandoffViewModel(_inventoryService, _consultationService, consultationDraftStore, _session, _active, _navigation,
            _feedback, _record, _account, _consultation, _autosave, _dialogs, _ui, this);
        _labResults = new LabResultsViewModel(labResults, _session, _info, _feedback, _dialogs, _urls, labFiles, _ui);
    }
}
