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
    private VitalSignsChartViewModel _vitalsChart;

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

    /// <summary>Evolución de signos vitales de la ficha (gráfica y tabla).</summary>
    public VitalSignsChartViewModel VitalsChart => _vitalsChart;

    [MemberNotNull(nameof(_info), nameof(_feedback), nameof(_navigation), nameof(_clinic), nameof(_catalog), nameof(_templates), nameof(_vitalsChart))]
    private void CreateChildren(NotificationService? notificationService, ClinicProfileService? clinicProfileService)
    {
        _info = new InfoDialogState(_ui);
        _feedback = new PatientsFeedback(notificationService, _info);
        _navigation = new PatientsNavigation(this);
        _clinic = new ClinicInfo(clinicProfileService);
        _catalog = new CatalogStore(_inventoryService, this);
        _templates = new PrescriptionTemplateStore(_inventoryService, this);
        _vitalsChart = new VitalSignsChartViewModel(this);
    }
}
