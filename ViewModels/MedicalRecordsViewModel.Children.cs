using System.Diagnostics.CodeAnalysis;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Hijos del coordinador (fase 6, plan §1.2). Se crean una sola vez, lo primero del constructor (antes
/// de que algo pueda poner un paciente), y sin efectos: no se suscriben a servicios ni ponen
/// propiedades que disparen ganchos. Cada uno se expone con una propiedad de solo lectura que nunca se
/// reemplaza; las vistas llegan a sus miembros por esa ruta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    private InfoDialogState _info;
    private PatientsFeedback _feedback;
    private PatientsNavigation _navigation;
    private ClinicInfo _clinic;
    private CatalogStore _catalog;
    private PrescriptionTemplateStore _templates;
    private PatientSession _session;
    private ActiveConsultationSession _active;
    private ConsultationRecordState _record;
    private VitalSignsChartViewModel _vitalsChart;
    private AgendaPanelViewModel _agenda;
    private PatientFormViewModel _patientForm;
    private PatientsHomeViewModel _home;

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

    /// <summary>Evolución de signos vitales de la ficha (gráfica y tabla).</summary>
    public VitalSignsChartViewModel VitalsChart => _vitalsChart;

    /// <summary>Agenda de la semana (panel lateral).</summary>
    public AgendaPanelViewModel Agenda => _agenda;

    /// <summary>Alta y edición de expediente (página del formulario y su borrador).</summary>
    public PatientFormViewModel PatientForm => _patientForm;

    /// <summary>Inicio: buscador, pacientes recientes y citas de hoy.</summary>
    public PatientsHomeViewModel Home => _home;

    [MemberNotNull(nameof(_info), nameof(_feedback), nameof(_navigation), nameof(_clinic), nameof(_catalog), nameof(_templates), nameof(_session), nameof(_active), nameof(_record), nameof(_vitalsChart), nameof(_agenda), nameof(_patientForm), nameof(_home))]
    private void CreateChildren(NotificationService? notificationService, ClinicProfileService? clinicProfileService, ConsultationDraftStore? consultationDraftStore)
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
        _vitalsChart = new VitalSignsChartViewModel(this);
        _agenda = new AgendaPanelViewModel(_inventoryService, _session, _info, _feedback, _clinic, _dialogs, _urls, this);
        _patientForm = new PatientFormViewModel(_inventoryService, _consultationService, consultationDraftStore, _session, _navigation, _active,
            _info, _feedback, this, _dialogs, _ui, _autosaveOptions, this);
        _home = new PatientsHomeViewModel(_inventoryService, _session, _navigation, this);
    }
}
