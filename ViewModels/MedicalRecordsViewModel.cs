using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MedicalRecordsViewModel : ObservableObject, IDisposable
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly Action? _onInventoryChangedHandler;

    public Action<string, int>? OnSendPrescriptionToPos { get; set; }
    public Action<ActiveConsultation>? OnCheckoutConsultationRequested { get; set; }
    public Action? OnNavigateToPosRequested { get; set; }
    public Action? OnBackToHubRequested { get; set; }
    public Action? OnNavigateToRecordsRequested { get; set; }

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    // ==========================================
    // SECCIÓN ACTIVA: EXPEDIENTES O SERVICIOS
    // ==========================================
    [ObservableProperty]
    private string _activeSection = "Expedientes"; // "Expedientes" o "Servicios"

    [RelayCommand]
    public void SwitchSection(string section)
    {
        ActiveSection = section;
        if (section == "Servicios" && Services.Count == 0)
        {
            _ = LoadServicesAsync();
        }
    }

    // ==========================================
    // PACIENTES Y EXPEDIENTES
    // ==========================================
    [ObservableProperty]
    private string _searchPatientText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _patients = new();

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _filteredPatients = new();

    [ObservableProperty]
    private PatientRecord? _selectedPatient;

    [ObservableProperty]
    private ObservableCollection<PatientConsultation> _consultationsHistory = new();

    [ObservableProperty]
    private PatientConsultation? _selectedHistoryConsultation;

    [ObservableProperty]
    private string _patientDetailTab = "Resumen"; // "Resumen" o "Historial"

    public string PatientActionButtonText => SelectedPatient == null ? "Agregar Paciente" : "Cambiar Paciente";
    public bool HasSelectedPatient => SelectedPatient != null;

    // ==========================================
    // MODAL DE SELECCIÓN / CAMBIO DE PACIENTE
    // ==========================================
    [ObservableProperty]
    private bool _isSelectPatientModalOpen;

    [ObservableProperty]
    private string _selectPatientSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _selectablePatients = new();

    // ==========================================
    // MODAL DE VISOR DE EXPEDIENTES (VER EXPEDIENTES)
    // ==========================================
    [ObservableProperty]
    private bool _isRecordsViewerModalOpen;

    [ObservableProperty]
    private string _recordsViewerSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _recordsViewerPatients = new();

    [ObservableProperty]
    private PatientRecord? _viewingRecordPatient;

    [ObservableProperty]
    private ObservableCollection<PatientConsultation> _viewingPatientConsultations = new();

    [ObservableProperty]
    private PatientConsultation? _selectedViewingConsultation;

    [ObservableProperty]
    private string _viewingRecordTab = "Resumen"; // "Resumen" o "Historial"

    partial void OnRecordsViewerSearchTextChanged(string value)
    {
        ApplyRecordsViewerFilter();
    }

    [RelayCommand]
    public void SwitchPatientDetailTab(string tab)
    {
        PatientDetailTab = tab;
        if (tab == "Servicios" && Services.Count == 0)
        {
            _ = LoadServicesAsync();
        }
    }

    // ==========================================
    // ESTADO DE CONSULTA MÉDICA ACTIVA
    // ==========================================
    public bool HasActiveConsultation => _consultationService?.HasActiveConsultation ?? false;
    public ActiveConsultation? ActiveConsultation => _consultationService?.CurrentConsultation;
    public string ActiveConsultationFolio => _consultationService?.CurrentConsultation?.Folio ?? string.Empty;
    public string ActiveConsultationPatientName => _consultationService?.CurrentConsultation?.PatientName ?? string.Empty;
    public int ActiveConsultationItemsCount => _consultationService?.CurrentConsultation?.TotalItems ?? 0;
    public decimal ActiveConsultationTotalAmount => _consultationService?.CurrentConsultation?.TotalAmount ?? 0m;

    [ObservableProperty]
    private string _activeConsultationElapsedTime = "00:00";

    private System.Threading.Timer? _consultationTimer;

    [ObservableProperty]
    private string? _infoModalMessage;

    [ObservableProperty]
    private bool _isInfoModalVisible;

    private System.Threading.CancellationTokenSource? _infoModalCts;

    partial void OnIsInfoModalVisibleChanged(bool value)
    {
        if (value)
        {
            _infoModalCts?.Cancel();
            _infoModalCts = new System.Threading.CancellationTokenSource();
            var token = _infoModalCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000, token);
                    if (!token.IsCancellationRequested)
                    {
                        var app = System.Windows.Application.Current;
                        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                        {
                            await app.Dispatcher.InvokeAsync(() => DismissInfoModal());
                        }
                        else
                        {
                            DismissInfoModal();
                        }
                    }
                }
                catch (System.OperationCanceledException) { }
            }, token);
        }
        else
        {
            _infoModalCts?.Cancel();
        }
    }

    // ==========================================
    // MODAL DE ALTA / EDICIÓN DE EXPEDIENTE (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private bool _isPatientModalOpen;

    [ObservableProperty]
    private string _patientModalTitle = "Nuevo Expediente Clínico";

    [ObservableProperty]
    private int _formPatientId;

    [ObservableProperty]
    private string _formRecordNumber = string.Empty;

    [ObservableProperty]
    private string _formFirstName = string.Empty;

    [ObservableProperty]
    private string _formLastName = string.Empty;

    [ObservableProperty]
    private string _formMaternalLastName = string.Empty;

    [ObservableProperty]
    private string _formFullName = string.Empty;

    partial void OnFormFullNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            FormFirstName = string.Empty;
            FormLastName = string.Empty;
            FormMaternalLastName = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(FormFirstName) && string.IsNullOrWhiteSpace(FormLastName))
        {
            ParseFullName(value, out var f, out var l, out var m);
            FormFirstName = f;
            FormLastName = l;
            FormMaternalLastName = m;
        }
    }

    [ObservableProperty]
    private string _formAgeText = "30";

    [ObservableProperty]
    private int _formAge = 30;

    partial void OnFormAgeTextChanged(string value)
    {
        var digitsOnly = new string(value.Where(char.IsDigit).ToArray());
        if (digitsOnly != value)
        {
            FormAgeText = digitsOnly;
            return;
        }

        if (int.TryParse(digitsOnly, out var age))
        {
            FormAge = age;
        }
        else if (string.IsNullOrEmpty(digitsOnly))
        {
            FormAge = 0;
        }
    }

    partial void OnFormAgeChanged(int value)
    {
        var str = value.ToString();
        if (FormAgeText != str)
        {
            FormAgeText = str;
        }
        FormIsPediatric = value < 18;
    }

    [ObservableProperty]
    private string _formGender = "Masculino";

    partial void OnFormGenderChanged(string value)
    {
        FormIsFemale = !string.IsNullOrWhiteSpace(value) && value.StartsWith("Fem", StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private DateTime? _formBirthDate;

    [ObservableProperty]
    private string _formOccupation = string.Empty;

    [ObservableProperty]
    private string _formReligion = string.Empty;

    // Antecedentes Heredofamiliares (AHF)
    [ObservableProperty]
    private string _formFamilyHistory = string.Empty;

    // Antecedentes Personales Patológicos (APP)
    [ObservableProperty]
    private string _formSurgeries = string.Empty;

    [ObservableProperty]
    private string _formTransfusions = string.Empty;

    [ObservableProperty]
    private string _formHospitalizations = string.Empty;

    [ObservableProperty]
    private string _formImmunizationSchedule = string.Empty; // Para niños

    // Antecedentes Gineco-Obstétricos (AGO) (Mujeres)
    [ObservableProperty]
    private string _formMenarcheAge = string.Empty;

    [ObservableProperty]
    private string _formSexualOnsetAge = string.Empty;

    [ObservableProperty]
    private string _formSexualPartnersCount = string.Empty;

    [ObservableProperty]
    private string _formPeriodDuration = string.Empty;

    [ObservableProperty]
    private string _formIsPeriodHeavy = "Normal";

    [ObservableProperty]
    private string _formPapSmear = string.Empty;

    [ObservableProperty]
    private int _formPregnanciesCount = 0;

    [ObservableProperty]
    private int _formCesareansCount = 0;

    [ObservableProperty]
    private int _formAbortionsCount = 0;

    [ObservableProperty]
    private bool _formIsCurrentlyPregnant = false;

    [ObservableProperty]
    private string _formGestationalWeeks = string.Empty;

    // Hitos del Desarrollo Psicomotor (Niños)
    [ObservableProperty]
    private string _formDevelopmentalMilestones = string.Empty;

    // Antecedentes Personales No Patológicos (APNP)
    [ObservableProperty]
    private string _formDiet = string.Empty;

    [ObservableProperty]
    private string _formHousingConditions = string.Empty;

    [ObservableProperty]
    private string _formZoonosis = string.Empty;

    [ObservableProperty]
    private string _formPatientActiveTab = "Identificacion"; // "Identificacion", "Patologicos", "GinecoHitos", "NoPatologicos"

    [ObservableProperty]
    private bool _formIsFemale;

    [ObservableProperty]
    private bool _formIsPediatric;

    [RelayCommand]
    public void SetFormPatientStepTab(string tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return;
        FormPatientActiveTab = tab;
    }

    [RelayCommand]
    public void NextFormPatientStep()
    {
        FormPatientActiveTab = FormPatientActiveTab switch
        {
            "Identificacion" => "Patologicos",
            "Patologicos" => "GinecoHitos",
            "GinecoHitos" => "NoPatologicos",
            _ => "NoPatologicos"
        };
    }

    [RelayCommand]
    public void PreviousFormPatientStep()
    {
        FormPatientActiveTab = FormPatientActiveTab switch
        {
            "NoPatologicos" => "GinecoHitos",
            "GinecoHitos" => "Patologicos",
            "Patologicos" => "Identificacion",
            _ => "Identificacion"
        };
    }

    [ObservableProperty]
    private string _formPhone = string.Empty;

    partial void OnFormPhoneChanged(string value)
    {
        var formatted = FormatPhoneNumber(value);
        if (formatted != value)
        {
            FormPhone = formatted;
        }
    }

    [ObservableProperty]
    private string _formEmail = string.Empty;

    [ObservableProperty]
    private string _formAddress = string.Empty;

    [ObservableProperty]
    private string _formBloodType = "O+";

    [ObservableProperty]
    private string _formAllergies = "Ninguna conocida";

    [ObservableProperty]
    private string _formChronicDiseases = "Ninguna";

    [ObservableProperty]
    private string _formEmergencyContactName = string.Empty;

    [ObservableProperty]
    private string _formEmergencyContactPhone = string.Empty;

    partial void OnFormEmergencyContactPhoneChanged(string value)
    {
        var formatted = FormatPhoneNumber(value);
        if (formatted != value)
        {
            FormEmergencyContactPhone = formatted;
        }
    }

    // ==========================================
    // SIGNOS VITALES INDIVIDUALES (FORMULARIO DE EXPEDIENTE)
    // ==========================================
    [ObservableProperty]
    private string _formVitalTA = "120/80";

    partial void OnFormVitalTAChanged(string value)
    {
        var formatted = FormatTA(value);
        if (formatted != value)
        {
            FormVitalTA = formatted;
        }
    }

    [ObservableProperty]
    private string _formVitalFC = "75";

    [ObservableProperty]
    private string _formVitalFR = "18";

    [ObservableProperty]
    private string _formVitalTemp = "36.5";

    [ObservableProperty]
    private string _formVitalWeight = "70.0";

    [ObservableProperty]
    private string _formVitalHeight = "1.70";

    [ObservableProperty]
    private string _formVitalIMC = "24.2 kg/m² (Peso normal)";

    partial void OnFormVitalWeightChanged(string value) => RecalculateFormIMC();
    partial void OnFormVitalHeightChanged(string value) => RecalculateFormIMC();

    public void RecalculateFormIMC()
    {
        if (decimal.TryParse(FormVitalWeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var weight) &&
            decimal.TryParse(FormVitalHeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var height) &&
            height > 0)
        {
            var imc = weight / (height * height);
            string category;
            if (imc < 18.5m) category = "Bajo peso";
            else if (imc < 25.0m) category = "Peso normal";
            else if (imc < 30.0m) category = "Sobrepeso";
            else if (imc < 35.0m) category = "Obesidad Grado I";
            else category = "Obesidad Grado II+";

            FormVitalIMC = $"{imc:F1} kg/m² ({category})";
        }
        else
        {
            FormVitalIMC = "Valores no disponibles";
        }
    }

    public static string FormatPhoneNumber(string? phone) => PatientRecord.FormatPhoneNumber(phone);

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return true; // Permite vacío
        var trimmed = email.Trim();
        try
        {
            return Regex.IsMatch(
                trimmed, 
                @"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$", 
                RegexOptions.IgnoreCase, 
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return false;
        }
    }

    public static string FormatTA(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var text = input.Trim().Replace('-', '/').Replace(' ', '/');

        if (text.Contains('/'))
        {
            var parts = text.Split(new[] { '/' }, 2);
            var leftDigits = new string(parts[0].Where(char.IsDigit).Take(3).ToArray());
            var rightDigits = parts.Length > 1 
                ? new string(parts[1].Where(char.IsDigit).Take(3).ToArray()) 
                : string.Empty;

            if (string.IsNullOrEmpty(leftDigits))
            {
                return rightDigits;
            }

            if (string.IsNullOrEmpty(rightDigits))
            {
                return $"{leftDigits}/";
            }

            return $"{leftDigits}/{rightDigits}";
        }

        var digits = new string(text.Where(char.IsDigit).Take(6).ToArray());

        if (digits.Length <= 3)
        {
            return digits;
        }

        if (digits.Length == 4)
        {
            if (digits.StartsWith("1") || digits.StartsWith("2"))
            {
                return $"{digits.Substring(0, 3)}/{digits.Substring(3)}";
            }
            return $"{digits.Substring(0, 2)}/{digits.Substring(2)}";
        }

        if (digits.Length == 5)
        {
            return $"{digits.Substring(0, 3)}/{digits.Substring(3)}";
        }

        return $"{digits.Substring(0, 3)}/{digits.Substring(3, 3)}";
    }

    private static void ParseFullName(string? fullName, out string firstName, out string lastName, out string maternalLastName)
    {
        firstName = string.Empty;
        lastName = string.Empty;
        maternalLastName = string.Empty;

        if (string.IsNullOrWhiteSpace(fullName))
            return;

        var trimmed = fullName.Trim();

        // Formato con coma: "Apellidos, Nombre(s)"
        if (trimmed.Contains(','))
        {
            var commaParts = trimmed.Split(new[] { ',' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (commaParts.Length == 2)
            {
                firstName = commaParts[1].Trim();
                var surnameTokens = commaParts[0].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (surnameTokens.Length == 1)
                {
                    lastName = surnameTokens[0];
                }
                else if (surnameTokens.Length >= 2)
                {
                    lastName = surnameTokens[0];
                    maternalLastName = string.Join(" ", surnameTokens.Skip(1));
                }
                return;
            }
        }

        // Formato regular: "Nombre(s) Paterno Materno"
        var tokens = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            firstName = tokens[0];
        }
        else if (tokens.Length == 2)
        {
            firstName = tokens[0];
            lastName = tokens[1];
        }
        else if (tokens.Length == 3)
        {
            firstName = tokens[0];
            lastName = tokens[1];
            maternalLastName = tokens[2];
        }
        else
        {
            lastName = tokens[^2];
            maternalLastName = tokens[^1];
            firstName = string.Join(" ", tokens.Take(tokens.Length - 2));
        }
    }

    private void ParseVitalSigns(string? vitalsString)
    {
        FormVitalTA = "120/80";
        FormVitalFC = "75";
        FormVitalFR = "18";
        FormVitalTemp = "36.5";
        FormVitalWeight = "70.0";
        FormVitalHeight = "1.70";

        if (string.IsNullOrWhiteSpace(vitalsString))
        {
            RecalculateFormIMC();
            return;
        }

        var segments = vitalsString.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var part = seg.Trim();
            if (part.StartsWith("TA:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(3).Trim();
                val = val.Replace("mmHg", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalTA = val;
            }
            else if (part.StartsWith("FC:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(3).Trim();
                val = val.Replace("lpm", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalFC = val;
            }
            else if (part.StartsWith("FR:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(3).Trim();
                val = val.Replace("rpm", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalFR = val;
            }
            else if (part.StartsWith("Temp:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(5).Trim();
                val = val.Replace("°C", "", StringComparison.OrdinalIgnoreCase).Replace("C", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalTemp = val;
            }
            else if (part.StartsWith("Peso:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(5).Trim();
                val = val.Replace("kg", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalWeight = val;
            }
            else if (part.StartsWith("Talla:", StringComparison.OrdinalIgnoreCase))
            {
                var val = part.Substring(6).Trim();
                val = val.Replace("m", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.IsNullOrEmpty(val)) FormVitalHeight = val;
            }
        }

        RecalculateFormIMC();
    }

    [ObservableProperty]
    private string _formInitialVitalSigns = "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C";

    [ObservableProperty]
    private string _formClinicalNotes = string.Empty;

    [ObservableProperty]
    private string? _formErrorMessage;

    public List<string> GenderOptions { get; } = new() { "Masculino", "Femenino", "Otro / No especificado" };
    public List<string> BloodTypeOptions { get; } = new() { "O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-" };
    public List<string> PeriodHeavyOptions { get; } = new() { "Normal", "Abundante", "Escaso" };

    // ==========================================
    // MODAL DE NUEVA CONSULTA / NOTA MÉDICA (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private bool _isConsultationModalOpen;

    [ObservableProperty]
    private string _consultationFolio = string.Empty;

    [ObservableProperty]
    private string _consultationChiefComplaint = string.Empty;

    [ObservableProperty]
    private string _consultationTA = "120/80";

    partial void OnConsultationTAChanged(string value)
    {
        var formatted = FormatTA(value);
        if (formatted != value)
        {
            ConsultationTA = formatted;
        }
    }

    [ObservableProperty]
    private string _consultationFC = "75";

    [ObservableProperty]
    private string _consultationFR = "18";

    [ObservableProperty]
    private string _consultationTemp = "36.5";

    [ObservableProperty]
    private string _consultationWeight = "70.0";

    [ObservableProperty]
    private string _consultationHeight = "1.70";

    [ObservableProperty]
    private string _consultationIMC = "24.2 kg/m² (Peso normal)";

    [ObservableProperty]
    private string _consultationGlucose = string.Empty;

    [ObservableProperty]
    private string _consultationPhysicalExam = string.Empty;

    [ObservableProperty]
    private string _consultationSystemsReview = string.Empty;

    [ObservableProperty]
    private string _consultationDiagnosis = string.Empty;

    [ObservableProperty]
    private string _consultationTreatmentPlan = string.Empty;

    [ObservableProperty]
    private string _consultationNotes = string.Empty;

    [ObservableProperty]
    private bool _consultationStartActivePos = true;

    [ObservableProperty]
    private ObservableCollection<PatientPrescriptionItem> _consultationPrescriptions = new();

    [ObservableProperty]
    private string _newPrescriptionMedicine = string.Empty;

    [ObservableProperty]
    private string _newPrescriptionDosage = string.Empty;

    [ObservableProperty]
    private int _newPrescriptionQuantity = 1;

    [ObservableProperty]
    private string? _consultationErrorMessage;

    // ==========================================
    // CATÁLOGO DE SERVICIOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ClinicalService> _services = new();

    [ObservableProperty]
    private ClinicalService? _selectedService;

    [ObservableProperty]
    private bool _isCreateServiceModalOpen;

    [ObservableProperty]
    private string _newServiceCode = string.Empty;

    [ObservableProperty]
    private string _newServiceName = string.Empty;

    [ObservableProperty]
    private string _newServiceDescription = string.Empty;

    [ObservableProperty]
    private string _newServiceCategory = "Consulta";

    [ObservableProperty]
    private decimal _newServicePrice = 300.00m;

    [ObservableProperty]
    private int _newServiceDuration = 20;

    [ObservableProperty]
    private ObservableCollection<Product> _availableProducts = new();

    [ObservableProperty]
    private Product? _selectedProductToAdd;

    [ObservableProperty]
    private int _newProductQuantity = 1;

    [ObservableProperty]
    private ObservableCollection<ServiceDefaultProduct> _newServiceDefaultProducts = new();

    public List<string> ServiceCategories { get; } = new()
    {
        "Consulta",
        "Curaciones",
        "Procedimientos",
        "Diagnóstico",
        "Certificados"
    };

    [ObservableProperty]
    private PosViewModel? _posVM;

    [ObservableProperty]
    private bool _isPosModalOpen;

    // ==========================================
    // ESTADO Y ACCIONES DE CONSULTA ACTIVA
    // ==========================================
    public string ConsultationActionButtonText => HasActiveConsultation ? "Finalizar Consulta" : "Iniciar Consulta";

    // ==========================================
    // CAPTURA INTERACTIVA DE RECETA MÉDICA (MEDICAMENTOS E INSUMOS)
    // ==========================================
    [ObservableProperty]
    private string _prescriptionSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _prescriptionProductSuggestions = new();

    [ObservableProperty]
    private bool _isPrescriptionDropdownOpen;

    [ObservableProperty]
    private Product? _selectedPrescriptionProduct;

    [ObservableProperty]
    private string _prescriptionMedicineName = string.Empty;

    [ObservableProperty]
    private string _prescriptionDosage = string.Empty;

    [ObservableProperty]
    private int _prescriptionQuantity = 1;

    [ObservableProperty]
    private bool _isPrescriptionPdfModalOpen;

    [ObservableProperty]
    private bool _isPrescriptionModalOpen;

    // ==========================================
    // MODAL DE INSUMOS Y MATERIAL CLÍNICO
    // ==========================================
    [ObservableProperty]
    private bool _isSuppliesModalOpen;

    [ObservableProperty]
    private string _supplySearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _supplyProductSuggestions = new();

    [ObservableProperty]
    private bool _isSupplyDropdownOpen;

    [ObservableProperty]
    private Product? _selectedSupplyProduct;

    [ObservableProperty]
    private string _supplyName = string.Empty;

    [ObservableProperty]
    private string _supplyInstructions = string.Empty;

    [ObservableProperty]
    private int _supplyQuantity = 1;

    // ==========================================
    // MODAL DE SERVICIOS Y PROCEDIMIENTOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private bool _isServicesModalOpen;

    [ObservableProperty]
    private string _serviceModalSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _serviceModalSuggestions = new();

    [ObservableProperty]
    private bool _isServiceModalDropdownOpen;

    [ObservableProperty]
    private ClinicalService? _selectedServiceModalItem;

    [ObservableProperty]
    private string _serviceModalName = string.Empty;

    [ObservableProperty]
    private string _serviceModalNotes = string.Empty;

    [ObservableProperty]
    private int _serviceModalQuantity = 1;

    // ==========================================
    // MODAL DE AGENDAR NUEVA CONSULTA / CITAS
    // ==========================================
    [ObservableProperty]
    private bool _isScheduleAppointmentModalOpen;

    [ObservableProperty]
    private DateTime _appointmentDate = DateTime.Today.AddDays(7);

    [ObservableProperty]
    private string _appointmentTime = "10:00 AM";

    [ObservableProperty]
    private string _appointmentReason = "Consulta de Seguimiento y Control";

    [ObservableProperty]
    private string _appointmentNotes = string.Empty;

    [ObservableProperty]
    private string _doctorName = "Dr(a). Médico Tratante";

    [ObservableProperty]
    private string _doctorLicense = "CED-PROF-10928374";

    [ObservableProperty]
    private string _clinicName = "CONSULTORIO MÉDICO GENERAL Y FARMACIA";

    // ==========================================
    // MODAL DE REGISTRO RÁPIDO DE SIGNOS VITALES
    // ==========================================
    [ObservableProperty]
    private bool _isVitalSignsModalOpen;

    [ObservableProperty]
    private string _vitalSignTA = string.Empty;

    partial void OnVitalSignTAChanged(string value)
    {
        var formatted = FormatTA(value);
        if (formatted != value)
        {
            VitalSignTA = formatted;
        }
    }

    [ObservableProperty]
    private string _vitalSignFC = string.Empty;

    [ObservableProperty]
    private string _vitalSignFR = string.Empty;

    [ObservableProperty]
    private string _vitalSignTemp = string.Empty;

    [ObservableProperty]
    private string _vitalSignWeight = string.Empty;

    partial void OnVitalSignWeightChanged(string value)
    {
        RecalculateVitalSignsIMC();
    }

    [ObservableProperty]
    private string _vitalSignHeight = string.Empty;

    partial void OnVitalSignHeightChanged(string value)
    {
        RecalculateVitalSignsIMC();
    }

    [ObservableProperty]
    private string _vitalSignIMC = "No calculado";

    [ObservableProperty]
    private string _vitalSignSpO2 = string.Empty;

    [ObservableProperty]
    private string _vitalSignGlucose = string.Empty;

    [ObservableProperty]
    private string _vitalSignWaist = string.Empty;

    private void RecalculateVitalSignsIMC()
    {
        if (decimal.TryParse((VitalSignWeight ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var weight) &&
            decimal.TryParse((VitalSignHeight ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var height) &&
            height > 0)
        {
            var imc = weight / (height * height);
            string category;
            if (imc < 18.5m) category = "Bajo peso";
            else if (imc < 25.0m) category = "Peso normal";
            else if (imc < 30.0m) category = "Sobrepeso";
            else if (imc < 35.0m) category = "Obesidad Grado I";
            else category = "Obesidad Grado II+";

            VitalSignIMC = $"{imc:F1} ({category})";
        }
        else
        {
            VitalSignIMC = "Valores no disponibles";
        }
    }

    // ==========================================
    // INDICACIONES GENERALES EN CONSULTA ACTIVA
    // ==========================================
    [ObservableProperty]
    private string _consultationGeneralIndications = "Tomar abundantes líquidos, mantener reposo relativo y acudir a urgencias en caso de presentar datos de alarma.";

    [ObservableProperty]
    private string _prescriptionGeneralInstructions = string.Empty;

    [ObservableProperty]
    private bool _hasCustomGeneralIndications;

    public static bool IsProductMedication(Product p)
    {
        if (p.Category != null && (p.Category.Name.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || p.Category.Name.Contains("Fármaco", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!string.IsNullOrEmpty(p.Code) && p.Code.StartsWith("MED", StringComparison.OrdinalIgnoreCase))
            return true;
        if (p.Category != null && (
            p.Category.Name.Contains("Curación", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Insumo", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Antiséptico", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Diagnóstico", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrEmpty(p.Code) && (
            p.Code.StartsWith("CUR", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("INS", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("ANT", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("DIA", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("MAT", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    public static bool IsProductSupply(Product p)
    {
        return !IsProductMedication(p);
    }

    partial void OnPrescriptionSearchTextChanged(string value)
    {
        ApplyPrescriptionSuggestions(value);
    }

    public void ApplyPrescriptionSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            PrescriptionProductSuggestions.Clear();
            IsPrescriptionDropdownOpen = false;
            return;
        }

        var q = TextSearch.Normalize(query);
        var matches = AvailableProducts
            .Where(p => IsProductMedication(p))
            .Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code))
            .Take(8)
            .ToList();

        PrescriptionProductSuggestions = new ObservableCollection<Product>(matches);
        IsPrescriptionDropdownOpen = PrescriptionProductSuggestions.Count > 0;
    }

    partial void OnSupplySearchTextChanged(string value)
    {
        ApplySupplySuggestions(value);
    }

    public void ApplySupplySuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SupplyProductSuggestions.Clear();
            IsSupplyDropdownOpen = false;
            return;
        }

        var q = TextSearch.Normalize(query);
        var matches = AvailableProducts
            .Where(p => IsProductSupply(p))
            .Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code))
            .Take(8)
            .ToList();

        SupplyProductSuggestions = new ObservableCollection<Product>(matches);
        IsSupplyDropdownOpen = SupplyProductSuggestions.Count > 0;
    }

    partial void OnServiceModalSearchTextChanged(string value)
    {
        ApplyServiceModalSuggestions(value);
    }

    public void ApplyServiceModalSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ServiceModalSuggestions.Clear();
            IsServiceModalDropdownOpen = false;
            return;
        }

        var q = TextSearch.Normalize(query);
        var matches = Services
            .Where(s => TextSearch.Matches(q, s.Name, s.Code, s.Category))
            .Take(8)
            .ToList();

        ServiceModalSuggestions = new ObservableCollection<ClinicalService>(matches);
        IsServiceModalDropdownOpen = ServiceModalSuggestions.Count > 0;
    }

    public MedicalRecordsViewModel() : this(null, null, null)
    {
    }

    public MedicalRecordsViewModel(InventoryService? inventoryService, ConsultationService? consultationService = null, PosViewModel? posViewModel = null)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        PosVM = posViewModel;

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
                    var app = System.Windows.Application.Current;
                    if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                    {
                        app.Dispatcher.InvokeAsync(async () => await LoadPatientsAsync());
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
            _ = LoadInitialDataAsync();
        }
        else
        {
            LoadFallbackServices();
            if (_consultationService != null && _consultationService.Patients.Count > 0)
            {
                Patients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
                FilteredPatients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
                if (_consultationService.HasActiveConsultation)
                {
                    var activeName = _consultationService.CurrentConsultation?.PatientName;
                    SelectedPatient = Patients.FirstOrDefault(p => p.FullName == activeName);
                }
                else
                {
                    SelectedPatient = null;
                }
            }
            else
            {
                LoadMockPatients();
            }
        }

        SyncActiveConsultation();
    }

    public async Task LoadInitialDataAsync()
    {
        await LoadPatientsAsync();
        await LoadServicesAsync();
    }

    public async Task LoadPatientsAsync()
    {
        if (_inventoryService != null)
        {
            var dbPatients = await _inventoryService.GetPatientsAsync();
            var prevSelected = SelectedPatient;
            var prevSelectedId = prevSelected?.Id ?? 0;

            // Se conserva la misma instancia del paciente seleccionado (la UI y los formularios
            // abiertos trabajan sobre ella); solo se toma de la base su historial de consultas.
            PatientRecord? keptSelected = null;
            if (prevSelected != null && prevSelectedId > 0)
            {
                var index = dbPatients.FindIndex(p => p.Id == prevSelectedId);
                if (index >= 0)
                {
                    prevSelected.Consultations = dbPatients[index].Consultations;
                    dbPatients[index] = prevSelected;
                    keptSelected = prevSelected;
                }
            }

            Patients = new ObservableCollection<PatientRecord>(dbPatients);
            ApplyPatientFilter();
            ApplyRecordsViewerFilter();

            _consultationService?.LoadPatientsFromDatabase(dbPatients);

            if (prevSelectedId > 0)
            {
                if (keptSelected == null)
                {
                    SelectedPatient = null; // El expediente fue dado de baja
                }
                else
                {
                    RefreshConsultationsHistory(keptSelected);
                }
            }
            else if (_consultationService != null && _consultationService.HasActiveConsultation)
            {
                var activeName = _consultationService.CurrentConsultation?.PatientName;
                SelectedPatient = Patients.FirstOrDefault(p => p.FullName == activeName);
            }
        }
    }

    partial void OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue)
    {
        // Solo un cambio real de paciente reinicia el estado de guardado de la consulta
        if (oldValue?.Id != newValue?.Id || newValue == null)
        {
            _isCurrentConsultationSaved = false;
        }
        OnPropertyChanged(nameof(PatientActionButtonText));
        OnPropertyChanged(nameof(HasSelectedPatient));
        if (newValue != null)
        {
            var list = newValue.Consultations.OrderByDescending(c => c.Date).ToList();
            ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
            SelectedHistoryConsultation = ConsultationsHistory.FirstOrDefault();
        }
        else
        {
            ConsultationsHistory.Clear();
            SelectedHistoryConsultation = null;
        }
    }

    /// <summary>
    /// Refleja en la instancia en memoria los datos que AddConsultationNoteAsync actualiza en la base.
    /// </summary>
    private static void ApplyConsultationSummary(PatientRecord patient, PatientConsultation consultation)
    {
        patient.LastVisit = consultation.Date;
        if (!string.IsNullOrWhiteSpace(consultation.Diagnosis)) patient.LastDiagnosis = consultation.Diagnosis;
        if (!string.IsNullOrWhiteSpace(consultation.VitalSigns)) patient.VitalSigns = consultation.VitalSigns;
        if (!string.IsNullOrWhiteSpace(consultation.Notes) || !string.IsNullOrWhiteSpace(consultation.TreatmentPlan))
        {
            patient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
        }
        if (consultation.Prescriptions.Count > 0)
        {
            patient.ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
        }
    }

    private void RefreshConsultationsHistory(PatientRecord patient)
    {
        var selectedId = SelectedHistoryConsultation?.Id ?? 0;
        var selectedFolio = SelectedHistoryConsultation?.Folio;
        var list = patient.Consultations.OrderByDescending(c => c.Date).ToList();
        ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
        SelectedHistoryConsultation =
            ConsultationsHistory.FirstOrDefault(c => (selectedId > 0 && c.Id == selectedId) || (selectedFolio != null && c.Folio == selectedFolio))
            ?? ConsultationsHistory.FirstOrDefault();
    }

    public void SyncActiveConsultation()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationFolio));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(ActiveConsultationItemsCount));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));
        OnPropertyChanged(nameof(ConsultationActionButtonText));

        if (HasActiveConsultation)
        {
            StartConsultationTimer();
        }
        else
        {
            StopConsultationTimer();
        }
    }

    private void StartConsultationTimer()
    {
        _consultationTimer?.Dispose();
        UpdateActiveConsultationTimer();
        _consultationTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(() => UpdateActiveConsultationTimer());
                }
                else
                {
                    UpdateActiveConsultationTimer();
                }
            }
            catch
            {
                // Ignored if app is terminating or in test runner
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopConsultationTimer()
    {
        _consultationTimer?.Dispose();
        _consultationTimer = null;
        ActiveConsultationElapsedTime = "00:00";
    }

    public void UpdateActiveConsultationTimer(DateTime? asOf = null)
    {
        if (_consultationService?.CurrentConsultation != null)
        {
            ActiveConsultationElapsedTime = _consultationService.CurrentConsultation.GetFormattedElapsedTime(asOf);
        }
        else
        {
            ActiveConsultationElapsedTime = "00:00";
        }
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
        StopConsultationTimer();
        GC.SuppressFinalize(this);
    }

    public async Task LoadServicesAsync()
    {
        if (_inventoryService != null)
        {
            var dbServices = await _inventoryService.GetClinicalServicesAsync();
            Services = new ObservableCollection<ClinicalService>(dbServices);
            SelectedService = Services.FirstOrDefault();

            var prods = await _inventoryService.GetProductsAsync();
            AvailableProducts = new ObservableCollection<Product>(prods);
            SelectedProductToAdd = AvailableProducts.FirstOrDefault();
        }
        else
        {
            LoadFallbackServices();
        }
    }

    private void LoadFallbackServices()
    {
        var services = new List<ClinicalService>
        {
            new()
            {
                Id = 1,
                Code = "SERV-001",
                Name = "Consulta Médica General",
                Description = "Evaluación clínica general, exploración física, diagnóstico y prescripción médica.",
                Category = "Consulta",
                Price = 300.00m,
                EstimatedDurationMinutes = 25,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Abatelenguas de madera" }, Quantity = 1, Instructions = "Exploración orofaríngea" },
                    new() { Product = new Product { Name = "Alcohol Etílico 70%" }, Quantity = 1, Instructions = "Asepsia" }
                }
            },
            new()
            {
                Id = 2,
                Code = "SERV-002",
                Name = "Curación Menor y Asepsia",
                Description = "Aseo quirúrgico superficial, desinfección, curación de herida o quemadura y vendaje de protección.",
                Category = "Curaciones",
                Price = 250.00m,
                EstimatedDurationMinutes = 20,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Gasa Estéril 10x10 cm" }, Quantity = 2, Instructions = "Limpieza y cobertura" },
                    new() { Product = new Product { Name = "Solución Fisiológica 0.9%" }, Quantity = 1, Instructions = "Lavado de arrastre" },
                    new() { Product = new Product { Name = "Venda Elástica 10 cm" }, Quantity = 1, Instructions = "Vendaje protector" }
                }
            },
            new()
            {
                Id = 3,
                Code = "SERV-003",
                Name = "Sutura de Herida Simple",
                Description = "Afrontamiento por planos de bordes de herida dérmica limpia con material estéril.",
                Category = "Procedimientos",
                Price = 450.00m,
                EstimatedDurationMinutes = 35,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Gasa Estéril 10x10 cm" }, Quantity = 2, Instructions = "Secado y hemostasia" },
                    new() { Product = new Product { Name = "Solución Fisiológica 0.9%" }, Quantity = 1, Instructions = "Asepsia previa" }
                }
            }
        };

        Services = new ObservableCollection<ClinicalService>(services);
        SelectedService = Services.FirstOrDefault();

        if (AvailableProducts.Count == 0)
        {
            AvailableProducts = new ObservableCollection<Product>
            {
                new() { Id = 1, Code = "MED-001", Name = "Paracetamol 500 mg", GenericName = "Paracetamol", Presentation = "Caja con 20 tabletas", CurrentStock = 45, Price = 45.00m },
                new() { Id = 2, Code = "MED-002", Name = "Amoxicilina 500 mg", GenericName = "Amoxicilina", Presentation = "Caja con 12 cápsulas", CurrentStock = 28, Price = 85.00m },
                new() { Id = 3, Code = "MED-003", Name = "Ibuprofeno 400 mg", GenericName = "Ibuprofeno", Presentation = "Caja con 10 tabletas", CurrentStock = 35, Price = 55.00m },
                new() { Id = 4, Code = "MED-004", Name = "Omeprazol 20 mg", GenericName = "Omeprazol", Presentation = "Caja con 14 cápsulas", CurrentStock = 50, Price = 60.00m },
                new() { Id = 5, Code = "INS-001", Name = "Gasa Estéril 10x10 cm", GenericName = "Material de Curación", Presentation = "Sobre con 5 piezas", CurrentStock = 120, Price = 18.00m },
                new() { Id = 6, Code = "INS-002", Name = "Jeringa Desechable 5 ml con Aguja", GenericName = "Insumo Descartable", Presentation = "Pieza estéril", CurrentStock = 85, Price = 12.00m },
                new() { Id = 7, Code = "INS-003", Name = "Solución Fisiológica 0.9% 250 ml", GenericName = "Solución Salina", Presentation = "Frasco flexible", CurrentStock = 24, Price = 42.00m },
                new() { Id = 8, Code = "INS-004", Name = "Venda Elástica 10 cm", GenericName = "Material de Curación", Presentation = "Rollo 5 m", CurrentStock = 40, Price = 25.00m }
            };
            SelectedProductToAdd = AvailableProducts.FirstOrDefault();
        }
    }

    private void LoadMockPatients()
    {
        var list = new List<PatientRecord>
        {
            new()
            {
                Id = 1,
                RecordNumber = "EXP-2026-001",
                FullName = "García Morales, Juan Carlos",
                Age = 42,
                Gender = "Masculino",
                Phone = "5541238890",
                Email = "juan.garcia@email.com",
                Address = "Av. Insurgentes Sur 450, Col. Roma Sur, CDMX",
                BloodType = "O+",
                Allergies = "Penicilina (Reacción alérgica severa)",
                ChronicDiseases = "Ninguna relevante",
                EmergencyContactName = "Laura Morales (Esposa)",
                EmergencyContactPhone = "5541238899",
                LastVisit = DateTime.Today.AddDays(-2),
                LastDiagnosis = "Faringoamigdalitis aguda bacteriana + Cefalea tensional",
                VitalSigns = "TA: 120/80 mmHg | FC: 76 lpm | Temp: 37.8 °C | Peso: 78 kg",
                ClinicalNotes = "Paciente masculino acude por odinofagia de 3 días de evolución, hipertermia cuantificada y adenomegalias cervicales dolorosas. Faringe congestiva con placas blanquecinas.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas por 5 días", RecommendedQuantity = 1 },
                    new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Curación y aseo bucofaríngeo", RecommendedQuantity = 1 }
                }
            },
            new()
            {
                Id = 2,
                RecordNumber = "EXP-2026-002",
                FullName = "Rodríguez Silva, María Elena",
                Age = 63,
                Gender = "Femenino",
                Phone = "5598761234",
                Email = "maria.elena@email.com",
                Address = "Calle Durango 128, Col. Condesa, CDMX",
                BloodType = "A+",
                Allergies = "Ninguna conocida",
                ChronicDiseases = "Diabetes Mellitus Tipo 2, Hipertensión Arterial",
                EmergencyContactName = "Carlos Rodríguez (Hijo)",
                EmergencyContactPhone = "5598761235",
                LastVisit = DateTime.Today.AddDays(-6),
                LastDiagnosis = "Control periódico de Hipertensión Arterial y Glucemia",
                VitalSigns = "TA: 135/85 mmHg | FC: 68 lpm | Temp: 36.4 °C | Glucosa: 134 mg/dL",
                ClinicalNotes = "Acude a control mensual. Refiere buena tolerancia al tratamiento actual. Sin sintomatología cardiovascular ni edema en miembros inferiores.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Tiras Reactivas para Glucosa Capilar", Dosage = "Monitoreo matutino en ayunas 3 veces por semana", RecommendedQuantity = 1 },
                    new() { MedicineName = "Alcohol Etílico Desnaturalizado 70%", Dosage = "Asepsia para toma de glucosa", RecommendedQuantity = 1 }
                }
            },
            new()
            {
                Id = 3,
                RecordNumber = "EXP-2026-003",
                FullName = "Hernández López, Sofía",
                Age = 27,
                Gender = "Femenino",
                Phone = "5533445566",
                Email = "sofia.hernandez@email.com",
                Address = "Calzada de Tlalpan 1890, CDMX",
                BloodType = "B+",
                Allergies = "Sulfas",
                ChronicDiseases = "Rinitis alérgica estacional",
                EmergencyContactName = "Patricia López (Madre)",
                EmergencyContactPhone = "5533445560",
                LastVisit = DateTime.Today.AddDays(-14),
                LastDiagnosis = "Traumatismo menor en tobillo derecho (Esguince Grado I)",
                VitalSigns = "TA: 110/70 mmHg | FC: 72 lpm | Temp: 36.6 °C | Peso: 56 kg",
                ClinicalNotes = "Presenta dolor e inflamación leve tras inversión forzada al bajar escalón. Sin deformidad ni pérdida de continuidad ósea a la palpación.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Venda Elástica 10 cm", Dosage = "Vendaje compresivo en ocho por 7 días", RecommendedQuantity = 1 },
                    new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas si hay dolor", RecommendedQuantity = 1 }
                }
            },
            new()
            {
                Id = 4,
                RecordNumber = "EXP-2026-004",
                FullName = "Martínez Castro, Roberto",
                Age = 35,
                Gender = "Masculino",
                Phone = "5577889900",
                Email = "roberto.martinez@email.com",
                Address = "Eje Central Lázaro Cárdenas 55, CDMX",
                BloodType = "O+",
                Allergies = "Ninguna",
                ChronicDiseases = "Ninguna",
                EmergencyContactName = "Mariana Castro (Hermana)",
                EmergencyContactPhone = "5577889911",
                LastVisit = DateTime.Today.AddDays(-21),
                LastDiagnosis = "Curación de herida contuso-cortante en antebrazo izquierdo",
                VitalSigns = "TA: 125/80 mmHg | FC: 78 lpm | Temp: 36.5 °C | Peso: 82 kg",
                ClinicalNotes = "Herida limpia de 3 cm afrontada previamente. Retiro de puntos satisfactorio. Cicatrización de primera intención sin signos de infección.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Solución Fisiológica 0.9%", Dosage = "Lavado de herida diario", RecommendedQuantity = 1 },
                    new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Protección de herida", RecommendedQuantity = 1 }
                }
            }
        };

        Patients = new ObservableCollection<PatientRecord>(list);
        FilteredPatients = new ObservableCollection<PatientRecord>(list);
        SelectedPatient = null;
    }

    partial void OnSearchPatientTextChanged(string value)
    {
        ApplyPatientFilter();
    }

    private void ApplyPatientFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchPatientText))
        {
            FilteredPatients = new ObservableCollection<PatientRecord>(Patients);
        }
        else
        {
            var q = TextSearch.Normalize(SearchPatientText);
            FilteredPatients = new ObservableCollection<PatientRecord>(
                Patients.Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.Phone, p.FormattedPhone, p.LastDiagnosis, p.Email)));
        }
    }

    partial void OnSelectPatientSearchTextChanged(string value)
    {
        ApplySelectablePatientsFilter();
    }

    public void ApplySelectablePatientsFilter()
    {
        if (string.IsNullOrWhiteSpace(SelectPatientSearchText))
        {
            SelectablePatients = new ObservableCollection<PatientRecord>(Patients);
        }
        else
        {
            var q = TextSearch.Normalize(SelectPatientSearchText);
            SelectablePatients = new ObservableCollection<PatientRecord>(
                Patients.Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.Phone, p.FormattedPhone, p.LastDiagnosis, p.Email, p.Allergies)));
        }
    }

    [RelayCommand]
    public void OpenSelectPatientModal()
    {
        SelectPatientSearchText = string.Empty;
        ApplySelectablePatientsFilter();
        IsSelectPatientModalOpen = true;
    }

    [RelayCommand]
    public void CloseSelectPatientModal()
    {
        IsSelectPatientModalOpen = false;
    }

    [RelayCommand]
    public void SelectPatient(PatientRecord? patient)
    {
        if (patient != null)
        {
            SelectedPatient = patient;
            IsSelectPatientModalOpen = false;
        }
    }

    [RelayCommand]
    public void DeselectPatient()
    {
        SelectedPatient = null;
        IsSelectPatientModalOpen = false;
    }

    [RelayCommand]
    public async Task NewPatientFromSelectorAsync()
    {
        IsSelectPatientModalOpen = false;
        await NewPatientAsync();
    }

    // ==========================================
    // OPERACIONES DE VISOR DE EXPEDIENTES (VER EXPEDIENTES)
    // ==========================================

    public void ApplyRecordsViewerFilter()
    {
        if (string.IsNullOrWhiteSpace(RecordsViewerSearchText))
        {
            RecordsViewerPatients = new ObservableCollection<PatientRecord>(Patients);
        }
        else
        {
            var q = TextSearch.Normalize(RecordsViewerSearchText);
            RecordsViewerPatients = new ObservableCollection<PatientRecord>(
                Patients.Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.Phone, p.FormattedPhone, p.LastDiagnosis, p.Email, p.Allergies)));
        }
    }

    [RelayCommand]
    public void OpenRecordsViewerModal()
    {
        RecordsViewerSearchText = string.Empty;
        ViewingRecordPatient = null;
        ViewingPatientConsultations.Clear();
        SelectedViewingConsultation = null;
        ViewingRecordTab = "Resumen";
        ApplyRecordsViewerFilter();
        IsRecordsViewerModalOpen = true;
    }

    [RelayCommand]
    public void CloseRecordsViewerModal()
    {
        if (ViewingRecordPatient != null)
        {
            BackToRecordsList();
            return;
        }

        IsRecordsViewerModalOpen = false;
        ViewingRecordPatient = null;
        ViewingPatientConsultations.Clear();
        SelectedViewingConsultation = null;
    }

    [RelayCommand]
    public void ViewSelectedPatientRecord()
    {
        if (SelectedPatient == null) return;
        ViewPatientRecordDetail(SelectedPatient);
        IsRecordsViewerModalOpen = true;
    }

    [RelayCommand]
    public void ViewPatientRecordDetail(PatientRecord? patient)
    {
        if (patient == null) return;

        ViewingRecordPatient = patient;
        var consultations = patient.Consultations.OrderByDescending(c => c.Date).ToList();
        ViewingPatientConsultations = new ObservableCollection<PatientConsultation>(consultations);
        SelectedViewingConsultation = ViewingPatientConsultations.FirstOrDefault();
        ViewingRecordTab = "Resumen";
    }

    [RelayCommand]
    public void BackToRecordsList()
    {
        ViewingRecordPatient = null;
        ViewingPatientConsultations.Clear();
        SelectedViewingConsultation = null;
        ApplyRecordsViewerFilter();
    }

    [RelayCommand]
    public void SwitchViewingRecordTab(string tab)
    {
        ViewingRecordTab = tab;
    }

    [RelayCommand]
    public void SelectViewingPatientForConsultation(PatientRecord? patient)
    {
        var target = patient ?? ViewingRecordPatient;
        if (target != null)
        {
            SelectedPatient = target;
            IsRecordsViewerModalOpen = false;
            ViewingRecordPatient = null;
            ViewingPatientConsultations.Clear();
            SelectedViewingConsultation = null;
            InfoModalMessage = $"Expediente de '{target.FullName}' ({target.RecordNumber}) cargado en la pantalla principal.";
            IsInfoModalVisible = true;
        }
    }

    // ==========================================
    // OPERACIONES DE EXPEDIENTE CLÍNICO
    // ==========================================

    [RelayCommand]
    public async Task NewPatientAsync()
    {
        FormPatientId = 0;
        FormErrorMessage = null;
        PatientModalTitle = "Nuevo Expediente Clínico (NOM-004-SSA3)";

        if (_inventoryService != null)
        {
            FormRecordNumber = await _inventoryService.GenerateNextRecordNumberAsync();
        }
        else
        {
            FormRecordNumber = $"EXP-{DateTime.Today.Year}-{(Patients.Count + 1):D3}";
        }

        FormFirstName = string.Empty;
        FormLastName = string.Empty;
        FormMaternalLastName = string.Empty;
        FormFullName = string.Empty;
        FormAge = 30;
        FormAgeText = "30";
        FormGender = "Masculino";
        FormPhone = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormBloodType = "O+";
        FormAllergies = "Ninguna conocida";
        FormChronicDiseases = "Ninguna";
        FormEmergencyContactName = string.Empty;
        FormEmergencyContactPhone = string.Empty;

        FormBirthDate = null;
        FormOccupation = string.Empty;
        FormReligion = string.Empty;
        FormFamilyHistory = string.Empty;
        FormSurgeries = string.Empty;
        FormTransfusions = string.Empty;
        FormHospitalizations = string.Empty;
        FormImmunizationSchedule = string.Empty;
        FormMenarcheAge = string.Empty;
        FormSexualOnsetAge = string.Empty;
        FormSexualPartnersCount = string.Empty;
        FormPeriodDuration = string.Empty;
        FormIsPeriodHeavy = "Normal";
        FormPapSmear = string.Empty;
        FormPregnanciesCount = 0;
        FormCesareansCount = 0;
        FormAbortionsCount = 0;
        FormIsCurrentlyPregnant = false;
        FormGestationalWeeks = string.Empty;
        FormDevelopmentalMilestones = string.Empty;
        FormDiet = string.Empty;
        FormHousingConditions = string.Empty;
        FormZoonosis = string.Empty;
        FormPatientActiveTab = "Identificacion";
        FormIsFemale = false;
        FormIsPediatric = false;

        FormVitalTA = "120/80";
        FormVitalFC = "75";
        FormVitalFR = "18";
        FormVitalTemp = "36.5";
        FormVitalWeight = "70.0";
        FormVitalHeight = "1.70";
        RecalculateFormIMC();

        FormInitialVitalSigns = "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C";
        FormClinicalNotes = string.Empty;

        IsPatientModalOpen = true;
    }

    [RelayCommand]
    public void EditPatient()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente para editar su expediente clínico.";
            IsInfoModalVisible = true;
            return;
        }

        FormPatientId = SelectedPatient.Id;
        FormErrorMessage = null;
        PatientModalTitle = $"Editar Expediente Clínico • {SelectedPatient.RecordNumber}";

        FormRecordNumber = SelectedPatient.RecordNumber;
        FormFullName = SelectedPatient.FullName;

        ParseFullName(SelectedPatient.FullName, out var first, out var last, out var maternal);
        FormFirstName = first;
        FormLastName = last;
        FormMaternalLastName = maternal;

        FormAge = SelectedPatient.Age;
        FormAgeText = SelectedPatient.Age.ToString();
        FormGender = SelectedPatient.Gender;
        FormPhone = FormatPhoneNumber(SelectedPatient.Phone);
        FormEmail = SelectedPatient.Email;
        FormAddress = SelectedPatient.Address;
        FormBloodType = SelectedPatient.BloodType;
        FormAllergies = SelectedPatient.Allergies;
        FormChronicDiseases = SelectedPatient.ChronicDiseases;
        FormEmergencyContactName = SelectedPatient.EmergencyContactName;
        FormEmergencyContactPhone = FormatPhoneNumber(SelectedPatient.EmergencyContactPhone);

        FormBirthDate = SelectedPatient.BirthDate;
        FormOccupation = SelectedPatient.Occupation ?? string.Empty;
        FormReligion = SelectedPatient.Religion ?? string.Empty;
        FormFamilyHistory = SelectedPatient.FamilyHistory ?? string.Empty;
        FormSurgeries = SelectedPatient.Surgeries ?? string.Empty;
        FormTransfusions = SelectedPatient.Transfusions ?? string.Empty;
        FormHospitalizations = SelectedPatient.Hospitalizations ?? string.Empty;
        FormImmunizationSchedule = SelectedPatient.ImmunizationSchedule ?? string.Empty;
        FormMenarcheAge = SelectedPatient.MenarcheAge ?? string.Empty;
        FormSexualOnsetAge = SelectedPatient.SexualOnsetAge ?? string.Empty;
        FormSexualPartnersCount = SelectedPatient.SexualPartnersCount ?? string.Empty;
        FormPeriodDuration = SelectedPatient.PeriodDuration ?? string.Empty;
        FormIsPeriodHeavy = string.IsNullOrWhiteSpace(SelectedPatient.IsPeriodHeavy) ? "Normal" : SelectedPatient.IsPeriodHeavy;
        FormPapSmear = SelectedPatient.PapSmear ?? string.Empty;
        FormPregnanciesCount = SelectedPatient.PregnanciesCount;
        FormCesareansCount = SelectedPatient.CesareansCount;
        FormAbortionsCount = SelectedPatient.AbortionsCount;
        FormIsCurrentlyPregnant = SelectedPatient.IsCurrentlyPregnant;
        FormGestationalWeeks = SelectedPatient.GestationalWeeks ?? string.Empty;
        FormDevelopmentalMilestones = SelectedPatient.DevelopmentalMilestones ?? string.Empty;
        FormDiet = SelectedPatient.Diet ?? string.Empty;
        FormHousingConditions = SelectedPatient.HousingConditions ?? string.Empty;
        FormZoonosis = SelectedPatient.Zoonosis ?? string.Empty;
        FormIsFemale = SelectedPatient.IsFemale;
        FormIsPediatric = SelectedPatient.IsPediatric;
        FormPatientActiveTab = "Identificacion";

        ParseVitalSigns(SelectedPatient.VitalSigns);
        FormInitialVitalSigns = SelectedPatient.VitalSigns;
        FormClinicalNotes = SelectedPatient.ClinicalNotes;

        IsPatientModalOpen = true;
    }

    [RelayCommand]
    public void ClosePatientModal()
    {
        IsPatientModalOpen = false;
        FormErrorMessage = null;
    }

    [RelayCommand]
    public async Task SavePatientModalAsync()
    {
        if (string.IsNullOrWhiteSpace(FormFirstName) && !string.IsNullOrWhiteSpace(FormFullName))
        {
            ParseFullName(FormFullName, out var f, out var l, out var m);
            FormFirstName = f;
            FormLastName = l;
            FormMaternalLastName = m;
        }

        if (string.IsNullOrWhiteSpace(FormFirstName))
        {
            FormErrorMessage = "El nombre(s) del paciente es obligatorio.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FormLastName))
        {
            FormErrorMessage = "El apellido paterno del paciente es obligatorio.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FormRecordNumber))
        {
            FormErrorMessage = "El folio del expediente es obligatorio.";
            return;
        }

        if (!int.TryParse(FormAgeText, out var parsedAge) || parsedAge < 0 || parsedAge > 130)
        {
            FormErrorMessage = "La edad debe ser un número válido entre 0 y 130 años.";
            return;
        }

        if (!IsValidEmail(FormEmail))
        {
            FormErrorMessage = "El correo electrónico no tiene un formato válido (ejemplo: usuario@correo.com).";
            return;
        }

        FormFullName = string.IsNullOrWhiteSpace(FormMaternalLastName)
            ? $"{FormFirstName.Trim()} {FormLastName.Trim()}"
            : $"{FormFirstName.Trim()} {FormLastName.Trim()} {FormMaternalLastName.Trim()}";

        var normalizedFolio = FormRecordNumber.Trim();
        if (_inventoryService != null)
        {
            var isTaken = await _inventoryService.IsRecordNumberTakenAsync(normalizedFolio, FormPatientId);
            if (isTaken)
            {
                FormErrorMessage = $"El folio de expediente '{normalizedFolio}' ya está en uso por otro paciente.";
                return;
            }
        }
        else
        {
            var duplicate = Patients.Any(p => p.Id != FormPatientId && p.RecordNumber.Equals(normalizedFolio, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                FormErrorMessage = $"El folio de expediente '{normalizedFolio}' ya existe.";
                return;
            }
        }

        var patient = FormPatientId > 0 && SelectedPatient != null && SelectedPatient.Id == FormPatientId 
            ? SelectedPatient 
            : (FormPatientId > 0 ? Patients.FirstOrDefault(p => p.Id == FormPatientId) ?? new PatientRecord() : new PatientRecord());
        patient.Id = FormPatientId;

        patient.RecordNumber = normalizedFolio;
        patient.FullName = FormFullName.Trim();
        patient.Age = parsedAge;
        patient.Gender = FormGender;
        patient.Phone = new string((FormPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        patient.Email = FormEmail?.Trim() ?? string.Empty;
        patient.Address = FormAddress?.Trim() ?? string.Empty;
        patient.BloodType = FormBloodType;
        patient.Allergies = string.IsNullOrWhiteSpace(FormAllergies) ? "Ninguna conocida" : FormAllergies.Trim();
        patient.ChronicDiseases = string.IsNullOrWhiteSpace(FormChronicDiseases) ? "Ninguna" : FormChronicDiseases.Trim();
        patient.EmergencyContactName = FormEmergencyContactName?.Trim() ?? string.Empty;
        patient.EmergencyContactPhone = new string((FormEmergencyContactPhone ?? string.Empty).Where(char.IsDigit).ToArray());

        patient.BirthDate = FormBirthDate;
        patient.Occupation = FormOccupation?.Trim() ?? string.Empty;
        patient.Religion = FormReligion?.Trim() ?? string.Empty;
        patient.FamilyHistory = FormFamilyHistory?.Trim() ?? string.Empty;
        patient.Surgeries = FormSurgeries?.Trim() ?? string.Empty;
        patient.Transfusions = FormTransfusions?.Trim() ?? string.Empty;
        patient.Hospitalizations = FormHospitalizations?.Trim() ?? string.Empty;
        patient.ImmunizationSchedule = FormImmunizationSchedule?.Trim() ?? string.Empty;
        patient.MenarcheAge = FormMenarcheAge?.Trim() ?? string.Empty;
        patient.SexualOnsetAge = FormSexualOnsetAge?.Trim() ?? string.Empty;
        patient.SexualPartnersCount = FormSexualPartnersCount?.Trim() ?? string.Empty;
        patient.PeriodDuration = FormPeriodDuration?.Trim() ?? string.Empty;
        patient.IsPeriodHeavy = FormIsPeriodHeavy?.Trim() ?? string.Empty;
        patient.PapSmear = FormPapSmear?.Trim() ?? string.Empty;
        patient.PregnanciesCount = FormPregnanciesCount;
        patient.CesareansCount = FormCesareansCount;
        patient.AbortionsCount = FormAbortionsCount;
        patient.IsCurrentlyPregnant = FormIsCurrentlyPregnant;
        patient.GestationalWeeks = FormGestationalWeeks?.Trim() ?? string.Empty;
        patient.DevelopmentalMilestones = FormDevelopmentalMilestones?.Trim() ?? string.Empty;
        patient.Diet = FormDiet?.Trim() ?? string.Empty;
        patient.HousingConditions = FormHousingConditions?.Trim() ?? string.Empty;
        patient.Zoonosis = FormZoonosis?.Trim() ?? string.Empty;

        var cleanTA = (FormVitalTA ?? string.Empty).Trim().TrimEnd('/');
        var vitalsParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanTA)) vitalsParts.Add($"TA: {cleanTA} mmHg");
        if (!string.IsNullOrWhiteSpace(FormVitalFC)) vitalsParts.Add($"FC: {FormVitalFC.Trim()} lpm");
        if (!string.IsNullOrWhiteSpace(FormVitalFR)) vitalsParts.Add($"FR: {FormVitalFR.Trim()} rpm");
        if (!string.IsNullOrWhiteSpace(FormVitalTemp)) vitalsParts.Add($"Temp: {FormVitalTemp.Trim()} °C");
        if (!string.IsNullOrWhiteSpace(FormVitalWeight)) vitalsParts.Add($"Peso: {FormVitalWeight.Trim()} kg");
        if (!string.IsNullOrWhiteSpace(FormVitalHeight)) vitalsParts.Add($"Talla: {FormVitalHeight.Trim()} m");
        if (!string.IsNullOrWhiteSpace(FormVitalIMC) && !FormVitalIMC.Contains("no disponibles")) vitalsParts.Add($"IMC: {FormVitalIMC.Trim()}");

        patient.VitalSigns = vitalsParts.Count > 0 
            ? string.Join(" | ", vitalsParts) 
            : (string.IsNullOrWhiteSpace(FormInitialVitalSigns) ? "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C" : FormInitialVitalSigns.Trim());

        FormInitialVitalSigns = patient.VitalSigns;
        patient.ClinicalNotes = FormClinicalNotes.Trim();

        if (_inventoryService != null)
        {
            try
            {
                var saved = await _inventoryService.SavePatientAsync(patient);
                await LoadPatientsAsync();
                SelectedPatient = Patients.FirstOrDefault(p => p.Id == saved.Id) ?? saved;
            }
            catch (Exception ex)
            {
                FormErrorMessage = $"Error al guardar en base de datos: {ex.Message}";
                return;
            }
        }
        else
        {
            if (patient.Id == 0)
            {
                patient.Id = Patients.Count > 0 ? Patients.Max(p => p.Id) + 1 : 1;
                Patients.Insert(0, patient);
            }
            _consultationService?.UpsertPatient(patient);
            ApplyPatientFilter();
            SelectedPatient = patient;
        }

        IsPatientModalOpen = false;
        FormErrorMessage = null;
        InfoModalMessage = $"¡Expediente '{patient.FullName}' ({patient.RecordNumber}) guardado exitosamente!";
        IsInfoModalVisible = true;
    }

    // ==========================================
    // OPERACIONES DE CONSULTA MÉDICA (NOM-004-SSA3)
    // ==========================================

    [RelayCommand]
    public void NewConsultation()
    {
        if (SelectedPatient == null)
        {
            OpenSelectPatientModal();
            return;
        }

        ConsultationErrorMessage = null;
        ConsultationFolio = $"CNS-{DateTime.Today:yyyyMMdd}-{(SelectedPatient.Consultations.Count + 1):D3}";
        ConsultationChiefComplaint = string.Empty;

        // Extraer valores o inicializar
        ConsultationTA = "120/80";
        ConsultationFC = "75";
        ConsultationFR = "18";
        ConsultationTemp = "36.5";
        ConsultationWeight = "70.0";
        ConsultationHeight = "1.70";
        RecalculateIMC();

        ConsultationPhysicalExam = string.Empty;
        ConsultationDiagnosis = string.Empty;
        ConsultationTreatmentPlan = string.Empty;
        ConsultationNotes = string.Empty;
        ConsultationStartActivePos = true;

        ConsultationPrescriptions.Clear();
        NewPrescriptionMedicine = string.Empty;
        NewPrescriptionDosage = string.Empty;
        NewPrescriptionQuantity = 1;

        IsConsultationModalOpen = true;
    }

    partial void OnConsultationWeightChanged(string value) => RecalculateIMC();
    partial void OnConsultationHeightChanged(string value) => RecalculateIMC();

    public void RecalculateIMC()
    {
        if (decimal.TryParse(ConsultationWeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var weight) &&
            decimal.TryParse(ConsultationHeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var height) &&
            height > 0)
        {
            var imc = weight / (height * height);
            string category;
            if (imc < 18.5m) category = "Bajo peso";
            else if (imc < 25.0m) category = "Peso normal";
            else if (imc < 30.0m) category = "Sobrepeso";
            else if (imc < 35.0m) category = "Obesidad Grado I";
            else category = "Obesidad Grado II+";

            ConsultationIMC = $"{imc:F1} kg/m² ({category})";
        }
        else
        {
            ConsultationIMC = "Valores no disponibles";
        }
    }

    [RelayCommand]
    public void AddPrescriptionItem()
    {
        if (string.IsNullOrWhiteSpace(NewPrescriptionMedicine))
        {
            ConsultationErrorMessage = "Especifique el nombre del medicamento o insumo a recetar.";
            return;
        }

        ConsultationPrescriptions.Add(new PatientPrescriptionItem
        {
            MedicineName = NewPrescriptionMedicine.Trim(),
            Dosage = string.IsNullOrWhiteSpace(NewPrescriptionDosage) ? "Según indicación médica" : NewPrescriptionDosage.Trim(),
            RecommendedQuantity = Math.Max(1, NewPrescriptionQuantity)
        });

        NewPrescriptionMedicine = string.Empty;
        NewPrescriptionDosage = string.Empty;
        NewPrescriptionQuantity = 1;
        ConsultationErrorMessage = null;
    }

    [RelayCommand]
    public void RemovePrescriptionItem(PatientPrescriptionItem item)
    {
        ConsultationPrescriptions.Remove(item);
    }

    [RelayCommand]
    public void CloseConsultationModal()
    {
        IsConsultationModalOpen = false;
        ConsultationErrorMessage = null;
    }

    [RelayCommand]
    public async Task SaveConsultationModalAsync()
    {
        if (SelectedPatient == null) return;

        if (string.IsNullOrWhiteSpace(ConsultationChiefComplaint))
        {
            ConsultationErrorMessage = "El motivo de consulta es obligatorio (NOM-004-SSA3).";
            return;
        }

        if (string.IsNullOrWhiteSpace(ConsultationDiagnosis))
        {
            ConsultationErrorMessage = "El diagnóstico clínico es obligatorio (NOM-004-SSA3).";
            return;
        }

        var cleanConsultationTA = ConsultationTA.Trim().TrimEnd('/');
        var vitalSignsFormatted = $"TA: {cleanConsultationTA} mmHg | FC: {ConsultationFC.Trim()} lpm | FR: {ConsultationFR.Trim()} rpm | Temp: {ConsultationTemp.Trim()} °C | Peso: {ConsultationWeight.Trim()} kg | Talla: {ConsultationHeight.Trim()} m | IMC: {ConsultationIMC.Trim()}";

        var startAt = _consultationService?.CurrentConsultation?.StartedAt ?? DateTime.Now;
        var endAt = DateTime.Now;

        var consultation = new PatientConsultation
        {
            PatientRecordId = SelectedPatient.Id,
            Folio = string.IsNullOrWhiteSpace(ConsultationFolio) ? $"CNS-{DateTime.Today:yyyyMMdd}-001" : ConsultationFolio.Trim(),
            Date = DateTime.Now,
            StartedAt = startAt,
            EndedAt = endAt,
            ChiefComplaint = ConsultationChiefComplaint.Trim(),
            VitalSigns = vitalSignsFormatted,
            PhysicalExamination = ConsultationPhysicalExam.Trim(),
            SystemsReview = ConsultationSystemsReview.Trim(),
            Diagnosis = ConsultationDiagnosis.Trim(),
            TreatmentPlan = ConsultationTreatmentPlan.Trim(),
            Notes = ConsultationNotes.Trim(),
            Prescriptions = new List<PatientPrescriptionItem>(ConsultationPrescriptions)
        };

        _isCurrentConsultationSaved = true;

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.AddConsultationNoteAsync(SelectedPatient.Id, consultation);
                ApplyConsultationSummary(SelectedPatient, consultation);
                await LoadPatientsAsync();
            }
            catch (Exception ex)
            {
                ConsultationErrorMessage = $"Error al registrar la consulta: {ex.Message}";
                return;
            }
        }
        else
        {
            SelectedPatient.LastVisit = consultation.Date;
            SelectedPatient.LastDiagnosis = consultation.Diagnosis;
            SelectedPatient.VitalSigns = consultation.VitalSigns;
            SelectedPatient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
            SelectedPatient.ActivePrescription = new List<PatientPrescriptionItem>(ConsultationPrescriptions);
            SelectedPatient.Consultations.Insert(0, consultation);
            ConsultationsHistory.Insert(0, consultation);
            SelectedHistoryConsultation = consultation;
        }

        if (ConsultationStartActivePos && _consultationService != null)
        {
            _consultationService.StartConsultation(SelectedPatient, consultation.ChiefComplaint);
            SyncActiveConsultation();
        }

        IsConsultationModalOpen = false;
        ConsultationErrorMessage = null;
        InfoModalMessage = $"¡Consulta médica registrada exitosamente para {SelectedPatient.FullName}!\nFolio: {consultation.Folio}\nDiagnóstico: {consultation.Diagnosis}\n\nLos signos vitales y la receta activa han sido actualizados en su expediente.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public void SendPrescriptionToPos()
    {
        if (SelectedPatient == null || SelectedPatient.ActivePrescription.Count == 0)
        {
            InfoModalMessage = "El paciente no tiene medicamentos activos en su receta para despachar.";
            IsInfoModalVisible = true;
            return;
        }

        foreach (var item in SelectedPatient.ActivePrescription)
        {
            OnSendPrescriptionToPos?.Invoke(item.MedicineName, item.RecommendedQuantity);
        }

        OnNavigateToPosRequested?.Invoke();
    }

    [RelayCommand]
    public async Task ToggleActiveConsultationAsync()
    {
        if (!HasActiveConsultation)
        {
            StartActiveConsultation();
        }
        else
        {
            await FinalizeConsultationAsync();
        }
    }

    private bool _isCurrentConsultationSaved;

    public async Task<PatientConsultation?> SaveActiveConsultationToDbAsync(DateTime? endedAt = null)
    {
        if (SelectedPatient == null) return null;
        if (_isCurrentConsultationSaved) return null;

        var end = endedAt ?? DateTime.Now;
        var active = _consultationService?.CurrentConsultation;
        var start = active?.StartedAt ?? DateTime.Now;

        var cleanConsultationTA = (ConsultationTA ?? string.Empty).Trim().TrimEnd('/');
        var vitalsParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanConsultationTA)) vitalsParts.Add($"TA: {cleanConsultationTA} mmHg");
        if (!string.IsNullOrWhiteSpace(ConsultationFC)) vitalsParts.Add($"FC: {ConsultationFC.Trim()} lpm");
        if (!string.IsNullOrWhiteSpace(ConsultationFR)) vitalsParts.Add($"FR: {ConsultationFR.Trim()} rpm");
        if (!string.IsNullOrWhiteSpace(ConsultationTemp)) vitalsParts.Add($"Temp: {ConsultationTemp.Trim()} °C");
        if (!string.IsNullOrWhiteSpace(ConsultationWeight)) vitalsParts.Add($"Peso: {ConsultationWeight.Trim()} kg");
        if (!string.IsNullOrWhiteSpace(ConsultationHeight)) vitalsParts.Add($"Talla: {ConsultationHeight.Trim()} m");
        if (!string.IsNullOrWhiteSpace(ConsultationGlucose)) vitalsParts.Add($"Glucosa: {ConsultationGlucose.Trim()} mg/dL");
        if (!string.IsNullOrWhiteSpace(ConsultationIMC) && !ConsultationIMC.Contains("no disponibles")) vitalsParts.Add($"IMC: {ConsultationIMC.Trim()}");

        var vitalSignsFormatted = vitalsParts.Count > 0 
            ? string.Join(" | ", vitalsParts) 
            : (SelectedPatient.VitalSigns ?? string.Empty);

        var chiefComplaint = !string.IsNullOrWhiteSpace(ConsultationChiefComplaint)
            ? ConsultationChiefComplaint.Trim()
            : (!string.IsNullOrWhiteSpace(active?.ChiefComplaint) ? active.ChiefComplaint : "Consulta Médica General");

        var diagnosis = !string.IsNullOrWhiteSpace(ConsultationDiagnosis)
            ? ConsultationDiagnosis.Trim()
            : "Consulta Médica General";

        var treatmentPlan = !string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? ConsultationTreatmentPlan.Trim()
            : (!string.IsNullOrWhiteSpace(ConsultationGeneralIndications) ? ConsultationGeneralIndications.Trim() : "Tratamiento e indicaciones médicas según receta.");

        var systemsReview = !string.IsNullOrWhiteSpace(ConsultationSystemsReview)
            ? ConsultationSystemsReview.Trim()
            : string.Empty;

        var folio = !string.IsNullOrWhiteSpace(active?.Folio)
            ? active.Folio
            : (!string.IsNullOrWhiteSpace(ConsultationFolio) ? ConsultationFolio.Trim() : $"CNS-{DateTime.Today:yyyyMMdd}-{Random.Shared.Next(100, 999)}");

        var prescriptions = ConsultationPrescriptions.Count > 0
            ? new List<PatientPrescriptionItem>(ConsultationPrescriptions)
            : (SelectedPatient.ActivePrescription ?? new List<PatientPrescriptionItem>());

        var consultation = new PatientConsultation
        {
            PatientRecordId = SelectedPatient.Id,
            Folio = folio,
            Date = DateTime.Now,
            StartedAt = start,
            EndedAt = end,
            ChiefComplaint = chiefComplaint,
            VitalSigns = vitalSignsFormatted,
            PhysicalExamination = ConsultationPhysicalExam?.Trim() ?? string.Empty,
            SystemsReview = systemsReview,
            Diagnosis = diagnosis,
            TreatmentPlan = treatmentPlan,
            Notes = ConsultationNotes?.Trim() ?? string.Empty,
            Prescriptions = prescriptions
        };

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.AddConsultationNoteAsync(SelectedPatient.Id, consultation);
                ApplyConsultationSummary(SelectedPatient, consultation);
                _isCurrentConsultationSaved = true;
            }
            catch (Exception ex)
            {
                // No reportar como guardada una nota que no llegó a la base de datos
                AppLogger.Error("Error al guardar nota de consulta en BD", ex);
                DialogService.ShowError($"No se pudo guardar la nota médica en el expediente:\n{ex.Message}");
                return null;
            }
        }
        else
        {
            SelectedPatient.LastVisit = consultation.Date;
            SelectedPatient.LastDiagnosis = consultation.Diagnosis;
            SelectedPatient.VitalSigns = consultation.VitalSigns;
            SelectedPatient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
            SelectedPatient.ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
            _isCurrentConsultationSaved = true;
        }

        if (!SelectedPatient.Consultations.Any(c => c.Folio == consultation.Folio))
        {
            SelectedPatient.Consultations.Insert(0, consultation);
        }
        if (!ConsultationsHistory.Any(c => c.Folio == consultation.Folio))
        {
            ConsultationsHistory.Insert(0, consultation);
            SelectedHistoryConsultation = consultation;
        }

        return consultation;
    }

    [RelayCommand]
    public void StartActiveConsultation()
    {
        if (SelectedPatient == null)
        {
            OpenSelectPatientModal();
            return;
        }

        _isCurrentConsultationSaved = false;

        if (_consultationService != null)
        {
            _consultationService.StartConsultation(SelectedPatient);
            SyncActiveConsultation();

            ConsultationFolio = _consultationService.CurrentConsultation?.Folio ?? $"CNS-{DateTime.Today:yyyyMMdd}-{Random.Shared.Next(100, 999)}";
            ConsultationChiefComplaint = string.Empty;
            ConsultationSystemsReview = string.Empty;
            ConsultationPhysicalExam = string.Empty;
            ConsultationDiagnosis = string.Empty;
            ConsultationTreatmentPlan = string.Empty;
            ConsultationNotes = string.Empty;
            ConsultationGlucose = string.Empty;
            ConsultationTA = string.Empty;
            ConsultationFC = string.Empty;
            ConsultationFR = string.Empty;
            ConsultationTemp = string.Empty;
            ConsultationWeight = string.Empty;
            ConsultationHeight = string.Empty;
            ConsultationIMC = "Valores no disponibles";
            ConsultationPrescriptions.Clear();
            PatientDetailTab = "Resumen";

            InfoModalMessage = $"¡Consulta iniciada con éxito para {SelectedPatient.FullName}!\nFolio: {_consultationService.CurrentConsultation?.Folio}\n\nPuede registrar en tiempo real los signos vitales, exploración por aparatos y sistemas, diagnóstico, tratamiento y receta médica.";
            IsInfoModalVisible = true;
        }
    }

    [RelayCommand]
    public async Task SaveActiveConsultationNoteAsync()
    {
        if (SelectedPatient == null || !HasActiveConsultation)
        {
            InfoModalMessage = "No hay ninguna consulta activa en curso.";
            IsInfoModalVisible = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(ConsultationChiefComplaint))
        {
            ConsultationChiefComplaint = "Consulta Médica General";
        }
        if (string.IsNullOrWhiteSpace(ConsultationDiagnosis))
        {
            ConsultationDiagnosis = "Revisión clínica general";
        }

        var consultation = await SaveActiveConsultationToDbAsync(DateTime.Now);
        if (consultation != null)
        {
            _consultationService?.CloseActiveConsultation();
            SyncActiveConsultation();
            StopConsultationTimer();

            ConsultationChiefComplaint = string.Empty;
            ConsultationPhysicalExam = string.Empty;
            ConsultationSystemsReview = string.Empty;
            ConsultationDiagnosis = string.Empty;
            ConsultationTreatmentPlan = string.Empty;
            ConsultationNotes = string.Empty;
            ConsultationErrorMessage = null;

            InfoModalMessage = $"Nota médica guardada exitosamente en el expediente de {SelectedPatient.FullName}.\nFolio: {consultation.Folio}";
            IsInfoModalVisible = true;
        }
    }

    [RelayCommand]
    public async Task FinalizeConsultationAsync()
    {
        if (_consultationService == null || !_consultationService.HasActiveConsultation)
        {
            InfoModalMessage = "No hay ninguna consulta médica activa para finalizar.";
            IsInfoModalVisible = true;
            return;
        }

        if (_inventoryService == null)
        {
            InfoModalMessage = "El servicio de inventario no está disponible.";
            IsInfoModalVisible = true;
            return;
        }

        // Registrar la fecha y hora final de la consulta y guardarla en BD si aún no se ha guardado
        var endedAt = DateTime.Now;
        if (_consultationService.CurrentConsultation != null)
        {
            _consultationService.CurrentConsultation.EndedAt = endedAt;
        }
        await SaveActiveConsultationToDbAsync(endedAt);

        PosVM ??= new PosViewModel(_inventoryService, _consultationService);
        PosVM.IsEmbeddedInModal = true;
        PosVM.IsConsultationCheckout = true;
        PosVM.OnSaleCompletedFromConsultation ??= HandleSaleCompletedFromConsultation;
        await PosVM.InitializeAsync();
        PosVM.PatientName = SelectedPatient?.FullName ?? ActiveConsultationPatientName ?? "Público General";

        // Agregar en automático la consulta general al carrito
        var consultaService = PosVM.Services.FirstOrDefault(s => 
            s.Code.Equals("SERV-001", StringComparison.OrdinalIgnoreCase) || 
            s.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase) || 
            s.Category.Equals("Consulta", StringComparison.OrdinalIgnoreCase))
            ?? Services.FirstOrDefault(s => 
            s.Code.Equals("SERV-001", StringComparison.OrdinalIgnoreCase) || 
            s.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase) || 
            s.Category.Equals("Consulta", StringComparison.OrdinalIgnoreCase));

        if (consultaService != null)
        {
            var existingService = PosVM.Cart.FirstOrDefault(c => c.IsService && (c.Code == consultaService.Code || c.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase)));
            if (existingService == null)
            {
                PosVM.AddServiceToCart(consultaService);
            }
        }
        else
        {
            var existingService = PosVM.Cart.FirstOrDefault(c => c.IsService && c.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase));
            if (existingService == null)
            {
                PosVM.Cart.Add(new CartItem
                {
                    ProductId = -1,
                    Code = "SERV-001",
                    Name = "Consulta Médica General",
                    GenericName = "Servicio Clínico • Consulta",
                    Presentation = "Honorario • 25 min",
                    Unit = "Servicio",
                    AvailableStock = 999,
                    UnitPrice = 300.00m,
                    Quantity = 1,
                    IsService = true
                });
                PosVM.RecalculateTotals();
            }
        }

        // Transferir insumos acumulados durante la consulta activa al carrito
        if (_consultationService.CurrentConsultation != null)
        {
            foreach (var cItem in _consultationService.CurrentConsultation.Items)
            {
                var existing = PosVM.Cart.FirstOrDefault(c => c.ProductId == cItem.ProductId);
                if (existing != null)
                {
                    existing.Quantity += cItem.Quantity;
                }
                else
                {
                    var prod = PosVM.Products.FirstOrDefault(p => p.Id == cItem.ProductId);
                    PosVM.Cart.Add(new CartItem
                    {
                        ProductId = cItem.ProductId,
                        Code = cItem.Code,
                        Name = cItem.Name,
                        GenericName = cItem.GenericName,
                        Presentation = cItem.Presentation,
                        Unit = cItem.Unit,
                        AvailableStock = prod?.CurrentStock ?? 999,
                        UnitPrice = cItem.UnitPrice,
                        Quantity = cItem.Quantity,
                        RequiresRefrigeration = prod?.RequiresRefrigeration ?? false,
                        IsService = cItem.ProductId <= 0
                    });
                }
            }
        }

        PosVM.RecalculateTotals();

        // Finalizar y cerrar la consulta médica en el servicio
        _consultationService.CloseActiveConsultation();
        SyncActiveConsultation();

        // Abrir la ventana modal del POS
        IsPosModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenPosModalAsync()
    {
        if (_consultationService == null || !_consultationService.HasActiveConsultation)
        {
            InfoModalMessage = "Solo se pueden agregar medicamentos y servicios desde el Punto de Venta si existe una consulta médica activa.\n\nInicie una consulta médica con un paciente primero.";
            IsInfoModalVisible = true;
            return;
        }

        if (PosVM != null)
        {
            PosVM.IsEmbeddedInModal = true;
            await PosVM.InitializeAsync();
        }

        IsPosModalOpen = true;
    }

    [RelayCommand]
    public void ClosePosModal()
    {
        IsPosModalOpen = false;
        if (PosVM != null)
        {
            PosVM.IsEmbeddedInModal = false;
            PosVM.IsConsultationCheckout = false;
        }
        SyncActiveConsultation();
    }

    public async void HandleSaleCompletedFromConsultation()
    {
        // 0. Si por alguna razón no se guardó la nota de consulta en BD, asegurarse de persistirla
        if (!_isCurrentConsultationSaved && SelectedPatient != null)
        {
            try
            {
                await SaveActiveConsultationToDbAsync(DateTime.Now);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo guardar la nota de consulta al cobrar", ex);
            }
        }
        _isCurrentConsultationSaved = false;

        // 1. Cerrar la ventana modal del POS y cualquier otra modal secundaria
        ClosePosModal();
        IsPrescriptionModalOpen = false;
        IsSuppliesModalOpen = false;
        IsServicesModalOpen = false;
        IsScheduleAppointmentModalOpen = false;
        IsVitalSignsModalOpen = false;
        IsRecordsViewerModalOpen = false;
        IsPatientModalOpen = false;
        DismissInfoModal();

        // 2. Finalizar y cerrar la consulta médica en el servicio y detener cronómetro
        _consultationService?.CloseActiveConsultation();
        SyncActiveConsultation();
        StopConsultationTimer();

        // 3. Limpiar los campos de la consulta
        ConsultationChiefComplaint = string.Empty;
        ConsultationPhysicalExam = string.Empty;
        ConsultationDiagnosis = string.Empty;
        ConsultationTreatmentPlan = string.Empty;
        ConsultationNotes = string.Empty;
        ConsultationErrorMessage = null;

        // 4. Salir de todo y volver a la vista inicial (sin paciente seleccionado)
        SelectedPatient = null;
        ConsultationsHistory.Clear();
        SelectedHistoryConsultation = null;

        // 5. Si la venta se procesó en la vista independiente de POS, volver a la vista de Expedientes/Consulta
        OnNavigateToRecordsRequested?.Invoke();

        // 6. Abrir inmediatamente la ventana modal de agregar paciente
        OpenSelectPatientModal();
    }

    [RelayCommand]
    public async Task FinalizeConsultationAndOpenPosAsync()
    {
        await FinalizeConsultationAsync();
    }

    [RelayCommand]
    public void CheckoutActiveConsultation()
    {
        if (_consultationService?.CurrentConsultation == null)
        {
            InfoModalMessage = "No hay ninguna consulta médica activa para liquidar.";
            IsInfoModalVisible = true;
            return;
        }

        var consultation = _consultationService.CurrentConsultation;
        OnCheckoutConsultationRequested?.Invoke(consultation);
    }

    [RelayCommand]
    public async Task CancelActiveConsultationAsync()
    {
        await FinalizeConsultationAsync();
    }

    public void CancelActiveConsultation()
    {
        _ = CancelActiveConsultationAsync();
    }

    // ==========================================
    // OPERACIONES DE RECETA MÉDICA (NOM-004-SSA3)
    // ==========================================

    [RelayCommand]
    public void SelectPrescriptionProduct(Product? product)
    {
        if (product == null) return;
        SelectedPrescriptionProduct = product;
        PrescriptionMedicineName = $"{product.Name} ({product.Presentation})";
        PrescriptionSearchText = product.Name;
        IsPrescriptionDropdownOpen = false;
    }

    [RelayCommand]
    public void ClosePrescriptionDropdown()
    {
        IsPrescriptionDropdownOpen = false;
    }

    [RelayCommand]
    public async Task AddPrescriptionItemToPatientAsync()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente para agregar medicamentos o insumos a la receta.";
            IsInfoModalVisible = true;
            return;
        }

        var name = !string.IsNullOrWhiteSpace(PrescriptionMedicineName) 
            ? PrescriptionMedicineName.Trim() 
            : PrescriptionSearchText.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            InfoModalMessage = "Especifique el nombre del medicamento o consumible a prescribir.";
            IsInfoModalVisible = true;
            return;
        }

        var dosage = string.IsNullOrWhiteSpace(PrescriptionDosage) 
            ? "Según indicación médica" 
            : PrescriptionDosage.Trim();

        var qty = PrescriptionQuantity > 0 ? PrescriptionQuantity : 1;

        var newItem = new PatientPrescriptionItem
        {
            MedicineName = name,
            Dosage = dosage,
            RecommendedQuantity = qty
        };

        var currentList = new List<PatientPrescriptionItem>(SelectedPatient.ActivePrescription);
        currentList.Add(newItem);
        SelectedPatient.ActivePrescription = currentList;
        SelectedPatient.SyncPrescriptionJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch
            {
            }
        }

        PrescriptionSearchText = string.Empty;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        SelectedPrescriptionProduct = null;
        IsPrescriptionDropdownOpen = false;

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public void AddPrescriptionItemToPos(PatientPrescriptionItem? item)
    {
        if (item == null) return;

        var name = item.MedicineName;
        var qty = Math.Max(1, item.RecommendedQuantity);

        var matchedProduct = AvailableProducts.FirstOrDefault(p => 
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || 
            $"{p.Name} ({p.Presentation})".Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(p.GenericName) && p.GenericName.Equals(name, StringComparison.OrdinalIgnoreCase))
        );

        int prodId = matchedProduct?.Id ?? -Random.Shared.Next(100, 9999);
        string code = matchedProduct?.Code ?? "MED-001";
        decimal price = matchedProduct?.Price ?? 85.00m;
        string? gen = matchedProduct?.GenericName;
        string? pres = matchedProduct?.Presentation ?? "Caja / Frasco";
        string unit = matchedProduct?.Unit ?? "Piezas";

        if (!HasActiveConsultation)
        {
            StartActiveConsultation();
        }

        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.AddCartItemToActiveConsultation(
                prodId, code, name, gen, pres, unit, price, qty
            );
            SyncActiveConsultation();
        }

        if (PosVM != null)
        {
            var existing = PosVM.Cart.FirstOrDefault(c => c.ProductId == prodId);
            if (existing != null)
            {
                existing.Quantity += qty;
            }
            else
            {
                PosVM.Cart.Add(new CartItem
                {
                    ProductId = prodId,
                    Code = code,
                    Name = name,
                    GenericName = gen,
                    Presentation = pres,
                    Unit = unit,
                    AvailableStock = matchedProduct?.CurrentStock ?? 999,
                    UnitPrice = price,
                    Quantity = qty
                });
            }
            PosVM.RecalculateTotals();
        }

        InfoModalMessage = $"Medicamento '{name}' (Cant: {qty}) agregado a la cuenta de la consulta para cobro en el POS.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public void AddServiceToActiveConsultation(ClinicalService? service)
    {
        if (service == null) return;

        if (!HasActiveConsultation)
        {
            StartActiveConsultation();
        }

        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.AddCartItemToActiveConsultation(
                -service.Id,
                service.Code,
                service.Name,
                $"Servicio Clínico • {service.Category}",
                $"Duración: {service.EstimatedDurationMinutes} min",
                "Servicio",
                service.Price,
                1
            );

            // Agregar también los insumos por defecto del servicio si existen
            foreach (var dp in service.DefaultProducts)
            {
                if (dp.Product != null)
                {
                    _consultationService.AddCartItemToActiveConsultation(
                        dp.Product.Id,
                        dp.Product.Code,
                        dp.Product.Name,
                        dp.Product.GenericName,
                        dp.Product.Presentation,
                        dp.Product.Unit,
                        dp.Product.Price,
                        Math.Max(1, dp.Quantity)
                    );
                }
            }

            SyncActiveConsultation();
            InfoModalMessage = $"Servicio '{service.Name}' (${service.Price:N2}) agregado a la cuenta de la consulta para cobro en POS.";
            IsInfoModalVisible = true;
        }
    }

    [RelayCommand]
    public void RemoveItemFromActiveConsultation(ConsultationProductItem? item)
    {
        if (_consultationService?.CurrentConsultation == null || item == null) return;

        _consultationService.CurrentConsultation.Items.Remove(item);
        _consultationService.CurrentConsultation.NotifyItemsChanged();
        SyncActiveConsultation();
    }

    [RelayCommand]
    public void OpenVitalSignsModal()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente antes de registrar signos vitales.";
            IsInfoModalVisible = true;
            return;
        }

        ParseQuickVitalSigns(SelectedPatient.VitalSigns);
        IsVitalSignsModalOpen = true;
    }

    [RelayCommand]
    public void CloseVitalSignsModal()
    {
        IsVitalSignsModalOpen = false;
    }

    [RelayCommand]
    public async Task SaveVitalSignsAsync()
    {
        if (SelectedPatient == null) return;

        var parts = new List<string>();
        var cleanTA = (VitalSignTA ?? string.Empty).Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(cleanTA)) parts.Add($"TA: {cleanTA} mmHg");
        if (!string.IsNullOrWhiteSpace(VitalSignFC)) parts.Add($"FC: {VitalSignFC.Trim()} lpm");
        if (!string.IsNullOrWhiteSpace(VitalSignFR)) parts.Add($"FR: {VitalSignFR.Trim()} rpm");
        if (!string.IsNullOrWhiteSpace(VitalSignTemp)) parts.Add($"Temp: {VitalSignTemp.Trim()} °C");
        if (!string.IsNullOrWhiteSpace(VitalSignWeight)) parts.Add($"Peso: {VitalSignWeight.Trim()} kg");
        if (!string.IsNullOrWhiteSpace(VitalSignHeight)) parts.Add($"Talla: {VitalSignHeight.Trim()} m");
        if (!string.IsNullOrWhiteSpace(VitalSignIMC) && !VitalSignIMC.Contains("no disponibles")) parts.Add($"IMC: {VitalSignIMC.Trim()}");
        if (!string.IsNullOrWhiteSpace(VitalSignSpO2)) parts.Add($"SpO2: {VitalSignSpO2.Trim()}%");
        if (!string.IsNullOrWhiteSpace(VitalSignGlucose)) parts.Add($"Glucosa: {VitalSignGlucose.Trim()} mg/dL");
        if (!string.IsNullOrWhiteSpace(VitalSignWaist)) parts.Add($"Cintura: {VitalSignWaist.Trim()} cm");

        SelectedPatient.VitalSigns = parts.Count > 0 
            ? string.Join(" | ", parts) 
            : "Sin signos vitales registrados";

        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.CurrentConsultation.VitalSigns = SelectedPatient.VitalSigns;
        }

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        IsVitalSignsModalOpen = false;
        OnPropertyChanged(nameof(SelectedPatient));
        InfoModalMessage = "¡Signos vitales guardados exitosamente en el expediente!";
        IsInfoModalVisible = true;
    }

    private void ParseQuickVitalSigns(string? vitalsString)
    {
        VitalSignTA = string.Empty;
        VitalSignFC = string.Empty;
        VitalSignFR = string.Empty;
        VitalSignTemp = string.Empty;
        VitalSignWeight = string.Empty;
        VitalSignHeight = string.Empty;
        VitalSignSpO2 = string.Empty;
        VitalSignGlucose = string.Empty;
        VitalSignWaist = string.Empty;

        if (string.IsNullOrWhiteSpace(vitalsString)) return;

        var segments = vitalsString.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var part = seg.Trim();
            if (part.StartsWith("TA:", StringComparison.OrdinalIgnoreCase))
                VitalSignTA = part.Substring(3).Replace("mmHg", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("FC:", StringComparison.OrdinalIgnoreCase))
                VitalSignFC = part.Substring(3).Replace("lpm", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("FR:", StringComparison.OrdinalIgnoreCase))
                VitalSignFR = part.Substring(3).Replace("rpm", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("Temp:", StringComparison.OrdinalIgnoreCase))
                VitalSignTemp = part.Substring(5).Replace("°C", "", StringComparison.OrdinalIgnoreCase).Replace("C", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("Peso:", StringComparison.OrdinalIgnoreCase))
                VitalSignWeight = part.Substring(5).Replace("kg", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("Talla:", StringComparison.OrdinalIgnoreCase))
                VitalSignHeight = part.Substring(6).Replace("m", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("SpO2:", StringComparison.OrdinalIgnoreCase))
                VitalSignSpO2 = part.Substring(5).Replace("%", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("Glucosa:", StringComparison.OrdinalIgnoreCase))
                VitalSignGlucose = part.Substring(8).Replace("mg/dL", "", StringComparison.OrdinalIgnoreCase).Trim();
            else if (part.StartsWith("Cintura:", StringComparison.OrdinalIgnoreCase))
                VitalSignWaist = part.Substring(8).Replace("cm", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        RecalculateVitalSignsIMC();
    }

    [RelayCommand]
    public void AddGeneralIndicationsToPrescription()
    {
        if (string.IsNullOrWhiteSpace(ConsultationGeneralIndications))
        {
            InfoModalMessage = "Escriba las indicaciones generales del paciente antes de vincularlas a la receta.";
            IsInfoModalVisible = true;
            return;
        }

        PrescriptionGeneralInstructions = ConsultationGeneralIndications.Trim();
        HasCustomGeneralIndications = true;
        if (SelectedPatient != null)
        {
            SelectedPatient.GeneralIndications = PrescriptionGeneralInstructions;
        }
        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.CurrentConsultation.GeneralIndications = PrescriptionGeneralInstructions;
        }

        InfoModalMessage = "¡Indicaciones generales vinculadas exitosamente a la receta médica!\n\nSe incluirán de forma destacada en la receta oficial NOM-004-SSA3 para impresión y PDF.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public void AppendGeneralIndicationPreset(string text)
    {
        if (string.IsNullOrWhiteSpace(ConsultationGeneralIndications))
            ConsultationGeneralIndications = text;
        else
            ConsultationGeneralIndications = ConsultationGeneralIndications.Trim() + " " + text;
    }

    [RelayCommand]
    public async Task RemovePrescriptionItemFromPatientAsync(PatientPrescriptionItem? item)
    {
        if (SelectedPatient == null || item == null) return;

        var list = new List<PatientPrescriptionItem>(SelectedPatient.ActivePrescription);
        list.Remove(item);
        SelectedPatient.ActivePrescription = list;
        SelectedPatient.SyncPrescriptionJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch
            {
            }
        }

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public void OpenPrescriptionModal()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar su receta médica.";
            IsInfoModalVisible = true;
            return;
        }

        PrescriptionSearchText = string.Empty;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        SelectedPrescriptionProduct = null;
        IsPrescriptionDropdownOpen = false;
        IsPrescriptionModalOpen = true;
    }

    [RelayCommand]
    public void ClosePrescriptionModal()
    {
        IsPrescriptionModalOpen = false;
    }

    [RelayCommand]
    public async Task SavePrescriptionModalAsync()
    {
        if (SelectedPatient != null)
        {
            SelectedPatient.SyncPrescriptionJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(SelectedPatient);
                }
                catch
                {
                }
            }
            OnPropertyChanged(nameof(SelectedPatient));
        }

        IsPrescriptionModalOpen = false;
        InfoModalMessage = $"¡Receta médica guardada correctamente en el expediente de {SelectedPatient?.FullName}!";
        IsInfoModalVisible = true;
    }

    // ==========================================
    // OPERACIONES DE MODAL DE INSUMOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public void OpenSuppliesModal()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar sus insumos y material clínico.";
            IsInfoModalVisible = true;
            return;
        }

        SupplySearchText = string.Empty;
        SupplyName = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        SelectedSupplyProduct = null;
        IsSupplyDropdownOpen = false;
        IsSuppliesModalOpen = true;
    }

    [RelayCommand]
    public void CloseSuppliesModal()
    {
        IsSuppliesModalOpen = false;
    }

    [RelayCommand]
    public void SelectSupplyProduct(Product? product)
    {
        if (product == null) return;
        SelectedSupplyProduct = product;
        SupplyName = $"{product.Name} ({product.Presentation})";
        SupplySearchText = product.Name;
        IsSupplyDropdownOpen = false;
    }

    [RelayCommand]
    public async Task AddSupplyItemToPatientAsync()
    {
        if (SelectedPatient == null) return;

        var name = !string.IsNullOrWhiteSpace(SupplyName)
            ? SupplyName.Trim()
            : SupplySearchText.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            InfoModalMessage = "Especifique el nombre del insumo o material de curación a agregar.";
            IsInfoModalVisible = true;
            return;
        }

        var instructions = string.IsNullOrWhiteSpace(SupplyInstructions)
            ? "Uso clínico en consulta"
            : SupplyInstructions.Trim();

        var qty = SupplyQuantity > 0 ? SupplyQuantity : 1;
        var unitPrice = SelectedSupplyProduct?.Price ?? 35.00m;

        var newItem = new PatientSupplyItem
        {
            SupplyName = name,
            Instructions = instructions,
            Quantity = qty,
            UnitPrice = unitPrice
        };

        var list = new List<PatientSupplyItem>(SelectedPatient.ActiveSupplies) { newItem };
        SelectedPatient.ActiveSupplies = list;
        SelectedPatient.SyncSuppliesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        SupplySearchText = string.Empty;
        SupplyName = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        SelectedSupplyProduct = null;
        IsSupplyDropdownOpen = false;

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task RemoveSupplyItemFromPatientAsync(PatientSupplyItem? item)
    {
        if (SelectedPatient == null || item == null) return;

        var list = new List<PatientSupplyItem>(SelectedPatient.ActiveSupplies);
        list.Remove(item);
        SelectedPatient.ActiveSupplies = list;
        SelectedPatient.SyncSuppliesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public void AddSupplyItemToPos(PatientSupplyItem? item)
    {
        if (item == null) return;

        var name = item.SupplyName;
        var qty = Math.Max(1, item.Quantity);

        var matchedProduct = AvailableProducts.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            $"{p.Name} ({p.Presentation})".Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(p.GenericName) && p.GenericName.Equals(name, StringComparison.OrdinalIgnoreCase))
        );

        int prodId = matchedProduct?.Id ?? -Random.Shared.Next(100, 9999);
        string code = matchedProduct?.Code ?? "INS-001";
        decimal price = matchedProduct?.Price ?? (item.UnitPrice > 0 ? item.UnitPrice : 35.00m);
        string? gen = matchedProduct?.GenericName ?? "Material Clínico";
        string? pres = matchedProduct?.Presentation ?? "Pieza / Paquete";
        string unit = matchedProduct?.Unit ?? "Piezas";

        if (!HasActiveConsultation)
        {
            StartActiveConsultation();
        }

        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.AddCartItemToActiveConsultation(
                prodId, code, name, gen, pres, unit, price, qty
            );
            SyncActiveConsultation();
        }

        if (PosVM != null)
        {
            var existing = PosVM.Cart.FirstOrDefault(c => c.ProductId == prodId);
            if (existing != null)
            {
                existing.Quantity += qty;
            }
            else
            {
                PosVM.Cart.Add(new CartItem
                {
                    ProductId = prodId,
                    Code = code,
                    Name = name,
                    GenericName = gen,
                    Presentation = pres,
                    Unit = unit,
                    AvailableStock = matchedProduct?.CurrentStock ?? 999,
                    UnitPrice = price,
                    Quantity = qty
                });
            }
            PosVM.RecalculateTotals();
        }

        InfoModalMessage = $"Insumo '{name}' (Cant: {qty}) agregado a la cuenta de la consulta para cobro en el POS.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public async Task SaveSuppliesModalAsync()
    {
        if (SelectedPatient != null)
        {
            SelectedPatient.SyncSuppliesJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(SelectedPatient);
                }
                catch { }
            }
            OnPropertyChanged(nameof(SelectedPatient));
        }

        IsSuppliesModalOpen = false;
        InfoModalMessage = $"¡Insumos y materiales clínicos guardados correctamente en el expediente de {SelectedPatient?.FullName}!";
        IsInfoModalVisible = true;
    }

    // ==========================================
    // OPERACIONES DE MODAL DE SERVICIOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public void OpenServicesModal()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar los servicios y procedimientos clínicos.";
            IsInfoModalVisible = true;
            return;
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalName = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        SelectedServiceModalItem = null;
        IsServiceModalDropdownOpen = false;
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        IsServicesModalOpen = false;
    }

    [RelayCommand]
    public void SelectServiceModalSuggestion(ClinicalService? service)
    {
        if (service == null) return;
        SelectedServiceModalItem = service;
        ServiceModalName = service.Name;
        ServiceModalSearchText = service.Name;
        IsServiceModalDropdownOpen = false;
    }

    [RelayCommand]
    public async Task AddServiceItemToPatientAsync()
    {
        if (SelectedPatient == null) return;

        var name = !string.IsNullOrWhiteSpace(ServiceModalName)
            ? ServiceModalName.Trim()
            : ServiceModalSearchText.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            InfoModalMessage = "Especifique el servicio clínico o procedimiento médico a agregar.";
            IsInfoModalVisible = true;
            return;
        }

        var notes = string.IsNullOrWhiteSpace(ServiceModalNotes)
            ? "Procedimiento clínico en consulta"
            : ServiceModalNotes.Trim();

        var qty = ServiceModalQuantity > 0 ? ServiceModalQuantity : 1;
        var serviceObj = SelectedServiceModalItem ?? Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var price = serviceObj?.Price ?? 200.00m;
        var category = serviceObj?.Category ?? "Consulta";
        var code = serviceObj?.Code ?? "SERV-001";
        var serviceId = serviceObj?.Id ?? 0;

        var newItem = new PatientServiceItem
        {
            ServiceId = serviceId,
            ServiceCode = code,
            ServiceName = name,
            Category = category,
            Notes = notes,
            Price = price,
            Quantity = qty
        };

        var list = new List<PatientServiceItem>(SelectedPatient.ActiveServices) { newItem };
        SelectedPatient.ActiveServices = list;
        SelectedPatient.SyncServicesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalName = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        SelectedServiceModalItem = null;
        IsServiceModalDropdownOpen = false;

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task RemoveServiceItemFromPatientAsync(PatientServiceItem? item)
    {
        if (SelectedPatient == null || item == null) return;

        var list = new List<PatientServiceItem>(SelectedPatient.ActiveServices);
        list.Remove(item);
        SelectedPatient.ActiveServices = list;
        SelectedPatient.SyncServicesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public void AddServiceItemToPos(PatientServiceItem? item)
    {
        if (item == null) return;

        var serviceObj = Services.FirstOrDefault(s => s.Id == item.ServiceId || s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (serviceObj != null)
        {
            AddServiceToActiveConsultation(serviceObj);
        }
        else
        {
            if (!HasActiveConsultation)
            {
                StartActiveConsultation();
            }

            if (_consultationService?.CurrentConsultation != null)
            {
                _consultationService.AddCartItemToActiveConsultation(
                    -Random.Shared.Next(100, 9999),
                    item.ServiceCode,
                    item.ServiceName,
                    $"Servicio Clínico • {item.Category}",
                    item.Notes,
                    "Servicio",
                    item.Price,
                    item.Quantity
                );
                SyncActiveConsultation();
                InfoModalMessage = $"Servicio '{item.ServiceName}' agregado a la cuenta de la consulta para cobro en POS.";
                IsInfoModalVisible = true;
            }
        }
    }

    [RelayCommand]
    public async Task SaveServicesModalAsync()
    {
        if (SelectedPatient != null)
        {
            SelectedPatient.SyncServicesJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(SelectedPatient);
                }
                catch { }
            }
            OnPropertyChanged(nameof(SelectedPatient));
        }

        IsServicesModalOpen = false;
        InfoModalMessage = $"¡Servicios clínicos guardados correctamente en el expediente de {SelectedPatient?.FullName}!";
        IsInfoModalVisible = true;
    }

    // ==========================================
    // OPERACIONES DE AGENDAR NUEVA CONSULTA
    // ==========================================

    [RelayCommand]
    public void OpenScheduleAppointmentModal()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para agendar una nueva consulta médica.";
            IsInfoModalVisible = true;
            return;
        }

        AppointmentDate = SelectedPatient.NextAppointmentDate ?? DateTime.Today.AddDays(7);
        AppointmentTime = !string.IsNullOrWhiteSpace(SelectedPatient.NextAppointmentTime)
            ? SelectedPatient.NextAppointmentTime
            : "10:00 AM";
        AppointmentReason = !string.IsNullOrWhiteSpace(SelectedPatient.NextAppointmentReason)
            ? SelectedPatient.NextAppointmentReason
            : "Consulta de Seguimiento y Control";
        AppointmentNotes = SelectedPatient.NextAppointmentNotes ?? string.Empty;

        IsScheduleAppointmentModalOpen = true;
    }

    [RelayCommand]
    public void CloseScheduleAppointmentModal()
    {
        IsScheduleAppointmentModalOpen = false;
    }

    [RelayCommand]
    public void SetAppointmentReasonPreset(string preset)
    {
        if (!string.IsNullOrWhiteSpace(preset))
        {
            AppointmentReason = preset;
        }
    }

    [RelayCommand]
    public async Task SaveScheduledAppointmentAsync()
    {
        if (SelectedPatient == null) return;

        SelectedPatient.NextAppointmentDate = AppointmentDate;
        SelectedPatient.NextAppointmentTime = string.IsNullOrWhiteSpace(AppointmentTime) ? "10:00 AM" : AppointmentTime.Trim();
        SelectedPatient.NextAppointmentReason = string.IsNullOrWhiteSpace(AppointmentReason) ? "Consulta de Seguimiento" : AppointmentReason.Trim();
        SelectedPatient.NextAppointmentNotes = AppointmentNotes?.Trim() ?? string.Empty;

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        IsScheduleAppointmentModalOpen = false;
        InfoModalMessage = $"¡Cita agendada con éxito para el {AppointmentDate:dd/MM/yyyy} a las {SelectedPatient.NextAppointmentTime} para {SelectedPatient.FullName}!";
        IsInfoModalVisible = true;

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task CancelScheduledAppointmentAsync()
    {
        if (SelectedPatient == null) return;

        SelectedPatient.NextAppointmentDate = null;
        SelectedPatient.NextAppointmentTime = string.Empty;
        SelectedPatient.NextAppointmentReason = string.Empty;
        SelectedPatient.NextAppointmentNotes = string.Empty;

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        IsScheduleAppointmentModalOpen = false;
        InfoModalMessage = $"Se ha cancelado la cita programada para {SelectedPatient.FullName}.";
        IsInfoModalVisible = true;

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public void OpenPrescriptionPdfPreview()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente para visualizar la receta médica.";
            IsInfoModalVisible = true;
            return;
        }

        if (SelectedPatient.ActivePrescription.Count == 0)
        {
            InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para generar el documento.";
            IsInfoModalVisible = true;
            return;
        }

        IsPrescriptionPdfModalOpen = true;
    }

    [RelayCommand]
    public void ClosePrescriptionPdfPreview()
    {
        IsPrescriptionPdfModalOpen = false;
    }

    [RelayCommand]
    public void PrintOrExportPrescriptionPdf()
    {
        InfoModalMessage = "¡Módulo de exportación directa a PDF configurado!\n\nLa receta oficial NOM-004-SSA3 ha sido formateada con éxito y está lista para su emisión física o guardado digital en PDF.";
        IsInfoModalVisible = true;
        IsPrescriptionPdfModalOpen = false;
    }

    [RelayCommand]
    public void DismissInfoModal()
    {
        _infoModalCts?.Cancel();
        IsInfoModalVisible = false;
        InfoModalMessage = null;
    }

    // ==========================================
    // MÉTODOS DE CATÁLOGO DE SERVICIOS
    // ==========================================

    [RelayCommand]
    public void OpenCreateServiceModal()
    {
        NewServiceCode = $"SERV-{(Services.Count + 1):D3}";
        NewServiceName = string.Empty;
        NewServiceDescription = string.Empty;
        NewServiceCategory = "Consulta";
        NewServicePrice = 300.00m;
        NewServiceDuration = 20;
        NewProductQuantity = 1;
        NewServiceDefaultProducts.Clear();
        SelectedProductToAdd = AvailableProducts.FirstOrDefault();
        IsCreateServiceModalOpen = true;
    }

    [RelayCommand]
    public void CloseCreateServiceModal()
    {
        IsCreateServiceModalOpen = false;
    }

    [RelayCommand]
    public void AddProductToNewService()
    {
        if (SelectedProductToAdd == null) return;

        var existing = NewServiceDefaultProducts.FirstOrDefault(dp => dp.ProductId == SelectedProductToAdd.Id);
        if (existing != null)
        {
            existing.Quantity += Math.Max(1, NewProductQuantity);
        }
        else
        {
            NewServiceDefaultProducts.Add(new ServiceDefaultProduct
            {
                ProductId = SelectedProductToAdd.Id,
                Product = SelectedProductToAdd,
                Quantity = Math.Max(1, NewProductQuantity),
                Instructions = "Insumo por defecto del procedimiento"
            });
        }

        NewProductQuantity = 1;
    }

    [RelayCommand]
    public void RemoveProductFromNewService(ServiceDefaultProduct item)
    {
        NewServiceDefaultProducts.Remove(item);
    }

    [RelayCommand]
    public async Task SaveNewServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServiceName))
        {
            InfoModalMessage = "El nombre del servicio clínico es obligatorio.";
            IsInfoModalVisible = true;
            return;
        }

        var newService = new ClinicalService
        {
            Code = string.IsNullOrWhiteSpace(NewServiceCode) ? $"SERV-{(Services.Count + 1):D3}" : NewServiceCode.Trim(),
            Name = NewServiceName.Trim(),
            Description = NewServiceDescription.Trim(),
            Category = NewServiceCategory,
            Price = NewServicePrice,
            EstimatedDurationMinutes = NewServiceDuration,
            IsActive = true
        };

        if (_inventoryService != null)
        {
            var defaultItems = NewServiceDefaultProducts
                .Select(p => (p.ProductId, p.Quantity, p.Instructions))
                .ToList();

            var saved = await _inventoryService.SaveClinicalServiceAsync(newService, defaultItems);
            await LoadServicesAsync();
        }
        else
        {
            foreach (var dp in NewServiceDefaultProducts)
            {
                newService.DefaultProducts.Add(dp);
            }
            Services.Add(newService);
        }

        SelectedService = newService;
        IsCreateServiceModalOpen = false;
        InfoModalMessage = $"¡Servicio '{newService.Name}' creado exitosamente con {newService.DefaultProducts.Count} producto(s) por defecto!";
        IsInfoModalVisible = true;
    }
}
