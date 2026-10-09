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
    [NotifyCanExecuteChangedFor(nameof(ChooseCrestImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCrestImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseHeaderImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearHeaderImageCommand))]
    private bool _isEditing;

    // ==========================================
    // MEMBRETE DE LA RECETA (escudo e imagen del encabezado)
    // ==========================================

    /// <summary>Escudo o logotipo que se ve (el guardado o el recién elegido); null sin imagen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrestImage))]
    private string? _crestImagePath;

    /// <summary>Imagen del encabezado que se ve (el trazo de signos vitales del Excel); null sin imagen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeaderImage))]
    private string? _headerImagePath;

    public bool HasCrestImage => !string.IsNullOrEmpty(CrestImagePath);
    public bool HasHeaderImage => !string.IsNullOrEmpty(HeaderImagePath);

    private const string ImageFileFilter = "Imágenes (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg";

    /// <summary>Cambio de imagen pendiente hasta "Guardar": la ruta elegida, o null para quitarla.</summary>
    private sealed record PendingImage(string? SourcePath);

    private PendingImage? _pendingCrest;
    private PendingImage? _pendingHeader;
    private readonly Func<string, string, string?> _pickImageFile;

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

    /// <summary>Vista previa de la receta: lo que falta se ve como texto de ejemplo.</summary>
    public string PreviewDoctorName => string.IsNullOrWhiteSpace(DoctorName) ? "Nombre del médico" : DoctorName.Trim();
    public string PreviewClinicName => string.IsNullOrWhiteSpace(ClinicName) ? "Nombre del consultorio" : ClinicName.Trim();
    public bool HasDoctorName => !string.IsNullOrWhiteSpace(DoctorName);
    public bool HasClinicName => !string.IsNullOrWhiteSpace(ClinicName);

    partial void OnDoctorNameChanged(string value)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PreviewDoctorName));
        OnPropertyChanged(nameof(HasDoctorName));
    }

    partial void OnClinicNameChanged(string value)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PreviewClinicName));
        OnPropertyChanged(nameof(HasClinicName));
    }

    /// <param name="pickImageFile">Selector de archivo (filtro, título); por defecto el diálogo de Windows.</param>
    public ClinicProfileViewModel(ClinicProfileService profileService, Func<string, string, string?>? pickImageFile = null)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _pickImageFile = pickImageFile ?? ((filter, title) => DialogService.ShowOpenFileDialog(filter, title));
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
        _pendingCrest = null;
        _pendingHeader = null;
        CrestImagePath = p.CrestImagePath;
        HeaderImagePath = p.HeaderImagePath;

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
            var imageError = ApplyPendingImages();

            // Una vez guardado, se vuelven a bloquear los campos y el botón cambia a Editar
            IsEditing = false;
            ActionButtonText = "Editar";
            StatusMessage = imageError == null
                ? "✓ Datos del consultorio y médico guardados exitosamente."
                : $"Datos guardados, pero la imagen del membrete no: {imageError}";
            IsSuccess = imageError == null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    public void ChooseCrestImage() => ChooseImage(LetterheadImageKind.Crest, "Elegir escudo o logotipo");

    [RelayCommand(CanExecute = nameof(IsEditing))]
    public void ClearCrestImage()
    {
        _pendingCrest = new PendingImage(null);
        CrestImagePath = null;
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    public void ChooseHeaderImage() => ChooseImage(LetterheadImageKind.Header, "Elegir imagen del encabezado");

    [RelayCommand(CanExecute = nameof(IsEditing))]
    public void ClearHeaderImage()
    {
        _pendingHeader = new PendingImage(null);
        HeaderImagePath = null;
    }

    private void ChooseImage(LetterheadImageKind kind, string title)
    {
        var path = _pickImageFile(ImageFileFilter, title);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!ClinicProfileService.IsSupportedImageFile(path))
        {
            StatusMessage = "Elija una imagen PNG o JPG de hasta 10 MB.";
            IsSuccess = false;
            return;
        }

        if (kind == LetterheadImageKind.Crest)
        {
            _pendingCrest = new PendingImage(path);
            CrestImagePath = path;
        }
        else
        {
            _pendingHeader = new PendingImage(path);
            HeaderImagePath = path;
        }
        StatusMessage = "Imagen elegida: queda en la receta al presionar Guardar.";
        IsSuccess = true;
    }

    /// <summary>Guarda o quita las imágenes elegidas; devuelve el error, o null si todo se guardó.</summary>
    private string? ApplyPendingImages()
    {
        string? error = null;
        foreach (var (kind, pending) in new[] { (LetterheadImageKind.Crest, _pendingCrest), (LetterheadImageKind.Header, _pendingHeader) })
        {
            if (pending == null) continue;
            try
            {
                if (pending.SourcePath != null) _profileService.SaveLetterheadImage(kind, pending.SourcePath);
                else _profileService.ClearLetterheadImage(kind);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
        }

        _pendingCrest = null;
        _pendingHeader = null;
        CrestImagePath = _profileService.CurrentProfile.CrestImagePath;
        HeaderImagePath = _profileService.CurrentProfile.HeaderImagePath;
        return error;
    }
}
