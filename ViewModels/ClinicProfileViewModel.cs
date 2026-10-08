using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class ClinicProfileViewModel : ObservableObject
{
    private readonly ClinicProfileService _profileService;

    [ObservableProperty]
    private string _doctorName = string.Empty;

    [ObservableProperty]
    private string _professionalLicense = string.Empty;

    [ObservableProperty]
    private string _specialty = string.Empty;

    [ObservableProperty]
    private string _university = string.Empty;

    [ObservableProperty]
    private string _clinicName = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _phone = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _taxId = string.Empty;

    [ObservableProperty]
    private string _additionalNotes = string.Empty;

    /// <summary>
    /// Controla si los campos están desbloqueados para edición o bloqueados en solo lectura.
    /// </summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// Leyenda del botón principal: "Editar" cuando los datos están guardados y bloqueados,
    /// o "Guardar" cuando los campos están abiertos para captura o edición.
    /// </summary>
    [ObservableProperty]
    private string _actionButtonText = "Guardar";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isSuccess = true;

    /// <summary>Resumen para el menú de Configuración.</summary>
    public string Summary => !string.IsNullOrWhiteSpace(DoctorName) ? DoctorName.Trim()
        : !string.IsNullOrWhiteSpace(ClinicName) ? ClinicName.Trim()
        : "Sin configurar (sale en recetas y tickets)";

    partial void OnDoctorNameChanged(string value) => OnPropertyChanged(nameof(Summary));
    partial void OnClinicNameChanged(string value) => OnPropertyChanged(nameof(Summary));

    public ClinicProfileViewModel(ClinicProfileService profileService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        LoadFromProfile();
    }

    public void LoadFromProfile()
    {
        var p = _profileService.CurrentProfile;
        DoctorName = p.DoctorName ?? string.Empty;
        ProfessionalLicense = p.ProfessionalLicense ?? string.Empty;
        Specialty = p.Specialty ?? string.Empty;
        University = p.University ?? string.Empty;
        ClinicName = p.ClinicName ?? string.Empty;
        Address = p.Address ?? string.Empty;
        Phone = p.Phone ?? string.Empty;
        Email = p.Email ?? string.Empty;
        TaxId = p.TaxId ?? string.Empty;
        AdditionalNotes = p.AdditionalNotes ?? string.Empty;

        // Si ya están cargados los datos, mostrarlos bloqueados y que el botón diga "Editar".
        // Si no están cargados (vacíos), mostrarlos desbloqueados y con el botón diciendo "Guardar".
        if (p.IsConfigured || !string.IsNullOrWhiteSpace(p.DoctorName) || !string.IsNullOrWhiteSpace(p.ClinicName))
        {
            IsEditing = false;
            ActionButtonText = "Editar";
            StatusMessage = "Datos guardados y activos en recetas, tickets y cortes de caja.";
            IsSuccess = true;
        }
        else
        {
            IsEditing = true;
            ActionButtonText = "Guardar";
            StatusMessage = "Ingrese los datos del médico y consultorio y presione Guardar.";
            IsSuccess = true;
        }
    }

    [RelayCommand]
    public void ToggleEditOrSave()
    {
        if (ActionButtonText == "Editar")
        {
            // Al dar click en editar, se desbloquean los campos y el botón cambia a Guardar
            IsEditing = true;
            ActionButtonText = "Guardar";
            StatusMessage = "Modo de edición activado. Realice los cambios necesarios y presione Guardar.";
            IsSuccess = true;
        }
        else
        {
            // Validar campos mínimos
            if (string.IsNullOrWhiteSpace(DoctorName) && string.IsNullOrWhiteSpace(ClinicName))
            {
                StatusMessage = "Por favor ingrese al menos el Nombre del Médico o el Nombre del Consultorio.";
                IsSuccess = false;
                return;
            }

            var profile = new ClinicProfile
            {
                DoctorName = DoctorName.Trim(),
                ProfessionalLicense = ProfessionalLicense.Trim(),
                Specialty = Specialty.Trim(),
                University = University.Trim(),
                ClinicName = ClinicName.Trim(),
                Address = Address.Trim(),
                Phone = Phone.Trim(),
                Email = Email.Trim(),
                TaxId = TaxId.Trim(),
                AdditionalNotes = AdditionalNotes.Trim(),
                IsConfigured = true,
                IsInitialSetupCompleted = true
            };

            _profileService.SaveProfile(profile);

            // Una vez guardado, se vuelven a bloquear los campos y el botón cambia a Editar
            IsEditing = false;
            ActionButtonText = "Editar";
            StatusMessage = "✓ Datos del consultorio y médico guardados exitosamente.";
            IsSuccess = true;
        }
    }
}
