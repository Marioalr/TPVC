using CommunityToolkit.Mvvm.ComponentModel;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Datos del médico y del consultorio para la receta y los recordatorios (fase 6, paso 6a). Se toman del
/// perfil del consultorio; sin perfil quedan los valores por defecto.
/// </summary>
public partial class ClinicInfo : ObservableObject
{
    private readonly ClinicProfileService? _clinicProfileService;

    public ClinicInfo(ClinicProfileService? clinicProfileService)
    {
        _clinicProfileService = clinicProfileService;
    }

    // ==========================================
    // DATOS DE IDENTIFICACIÓN MÉDICA (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private string _doctorName = "Dr. Titular";

    [ObservableProperty]
    private string _doctorLicense = "";

    [ObservableProperty]
    private string _doctorSpecialty = "Médico General";

    [ObservableProperty]
    private string _clinicName = "Consultorio Médico";

    [ObservableProperty]
    private string _clinicAddress = "";

    [ObservableProperty]
    private string _clinicPhone = "";

    // Membrete de la receta (vista previa igual a la impresión, fase 7.4)
    [ObservableProperty]
    private string _university = "";

    /// <summary>Ruta del escudo del membrete; null sin imagen.</summary>
    [ObservableProperty]
    private string? _crestImagePath;

    /// <summary>Ruta de la imagen del encabezado (trazo de signos vitales); null sin imagen.</summary>
    [ObservableProperty]
    private string? _headerImagePath;

    public void RefreshProfileData(ClinicProfile? profile = null)
    {
        var p = profile ?? _clinicProfileService?.CurrentProfile;
        if (p != null)
        {
            DoctorName = string.IsNullOrWhiteSpace(p.DoctorName) ? PrescriptionSheet.DefaultDoctorName : p.DoctorName;
            DoctorLicense = p.DoctorLicense ?? string.Empty;
            DoctorSpecialty = string.IsNullOrWhiteSpace(p.DoctorSpecialty) ? "Médico General" : p.DoctorSpecialty;
            ClinicName = string.IsNullOrWhiteSpace(p.ClinicName) ? "Consultorio Médico" : p.ClinicName;
            ClinicAddress = p.ClinicAddress ?? string.Empty;
            ClinicPhone = p.ClinicPhone ?? string.Empty;
            University = p.University ?? string.Empty;
            CrestImagePath = p.CrestImagePath;
            HeaderImagePath = p.HeaderImagePath;
        }
    }
}
