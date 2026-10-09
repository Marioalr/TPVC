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
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MedicalRecordsViewModel : ObservableObject, IDisposable
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly PrintingService? _printingService;
    private readonly Action? _onInventoryChangedHandler;
    private readonly Action? _onProfileChangedHandler;

    public Action? OnBackToHubRequested { get; set; }

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    // ==========================================
    // PACIENTES Y EXPEDIENTES
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<PatientRecord> _patients = new();

    private PatientRecord? _selectedPatient;

    /// <summary>
    /// Paciente abierto. Cambia por referencia, no por Id (PatientRecord.Equals compara Id): la copia
    /// más reciente del mismo expediente (cambios de otro equipo, recarga tras guardar) también se
    /// toma. Antes se ignoraba y la ficha seguía con la copia vieja, que al guardarse pisaba lo nuevo.
    /// </summary>
    public PatientRecord? SelectedPatient
    {
        get => _selectedPatient;
        set
        {
            if (ReferenceEquals(_selectedPatient, value)) return;
            var oldValue = _selectedPatient;
            OnPropertyChanging();
            _selectedPatient = value;
            OnSelectedPatientChanged(oldValue, value);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private ObservableCollection<PatientConsultation> _consultationsHistory = new();

    // ==========================================
    // ESTADO DE CONSULTA MÉDICA ACTIVA
    // ==========================================
    public bool HasActiveConsultation => _consultationService?.HasActiveConsultation ?? false;
    public ActiveConsultation? ActiveConsultation => _consultationService?.CurrentConsultation;
    public string ActiveConsultationFolio => _consultationService?.CurrentConsultation?.Folio ?? string.Empty;
    public string ActiveConsultationPatientName => _consultationService?.CurrentConsultation?.PatientName ?? string.Empty;
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
            _infoModalCts?.Dispose();
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
    private string _formAgeText = string.Empty;

    [ObservableProperty]
    private int _formAge;

    partial void OnFormAgeTextChanged(string value)
    {
        var digitsOnly = new string(value.Where(char.IsDigit).ToArray());
        if (digitsOnly != value)
        {
            FormAgeText = digitsOnly;
            return;
        }

        FormAge = int.TryParse(digitsOnly, out var age) ? age : 0;
        RecomputeFormProfile();
    }

    partial void OnFormAgeChanged(int value)
    {
        // Vacío = edad sin capturar (no "0 años", que lo volvería recién nacido)
        var str = value.ToString();
        if (FormAgeText != str && !(value == 0 && string.IsNullOrEmpty(FormAgeText)))
        {
            FormAgeText = str;
        }
        RecomputeFormProfile();
    }

    [ObservableProperty]
    private string _formGender = string.Empty;

    partial void OnFormGenderChanged(string value) => RecomputeFormProfile();

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
    private bool _formIsFemale;

    [ObservableProperty]
    private bool _formIsPediatric;

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
    private string _formBloodType = BloodTypeUnknown;

    [ObservableProperty]
    private string _formAllergies = string.Empty;

    [ObservableProperty]
    private string _formChronicDiseases = string.Empty;

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

    [ObservableProperty]
    private string _formClinicalNotes = string.Empty;

    [ObservableProperty]
    private string? _formErrorMessage;

    public List<string> GenderOptions { get; } = new() { "Masculino", "Femenino", "Otro / No especificado" };
    public const string BloodTypeUnknown = "No determinado";

    public List<string> BloodTypeOptions { get; } = new() { BloodTypeUnknown, "O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-" };
    public List<string> PeriodHeavyOptions { get; } = new() { "Normal", "Abundante", "Escaso" };

    // ==========================================
    // NOTA MÉDICA DE LA CONSULTA EN CURSO (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private string _consultationFolio = string.Empty;

    [ObservableProperty]
    private string _consultationChiefComplaint = string.Empty;

    [ObservableProperty]
    private string _consultationTA = string.Empty;

    partial void OnConsultationTAChanged(string value)
    {
        var formatted = FormatTA(value);
        if (formatted != value)
        {
            ConsultationTA = formatted;
        }
        NotifyVitalSignsWarningChanged();
    }

    [ObservableProperty]
    private string _consultationFC = string.Empty;

    [ObservableProperty]
    private string _consultationFR = string.Empty;

    [ObservableProperty]
    private string _consultationTemp = string.Empty;

    [ObservableProperty]
    private string _consultationWeight = string.Empty;

    [ObservableProperty]
    private string _consultationHeight = string.Empty;

    [ObservableProperty]
    private string _consultationIMC = "Valores no disponibles";

    [ObservableProperty]
    private string _consultationGlucose = string.Empty;

    [ObservableProperty]
    private string _consultationPhysicalExam = string.Empty;

    [ObservableProperty]
    private string _consultationSystemsReview = string.Empty;

    [ObservableProperty]
    private string _consultationDiagnosis = string.Empty;

    private bool _isSelectingCie10;

    partial void OnConsultationDiagnosisChanged(string value)
    {
        if (_isSelectingCie10) return;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            Cie10Suggestions = new ObservableCollection<Cie10Diagnosis>();
            IsCie10DropdownOpen = false;
            return;
        }

        var matches = Cie10Diagnosis.Search(value.Trim(), 20);
        Cie10Suggestions = new ObservableCollection<Cie10Diagnosis>(matches);
        IsCie10DropdownOpen = Cie10Suggestions.Count > 0;
    }

    [ObservableProperty]
    private ObservableCollection<Cie10Diagnosis> _cie10Suggestions = new();

    [ObservableProperty]
    private bool _isCie10DropdownOpen;

    [RelayCommand]
    public void SelectCie10Suggestion(Cie10Diagnosis? item)
    {
        if (item == null) return;
        _isSelectingCie10 = true;
        try
        {
            ConsultationDiagnosis = item.ToString();
            IsCie10DropdownOpen = false;
            Cie10Suggestions.Clear();
        }
        finally
        {
            _isSelectingCie10 = false;
        }
    }

    [ObservableProperty]
    private string _consultationTreatmentPlan = string.Empty;

    [ObservableProperty]
    private string _consultationNotes = string.Empty;

    // ==========================================
    // CATÁLOGO DE SERVICIOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ClinicalService> _services = new();

    [ObservableProperty]
    private ObservableCollection<Product> _availableProducts = new();

    /// <summary>Cuenta de "Terminar consulta": el mismo cobro que el Punto de Venta, con su propia cuenta.</summary>
    public CheckoutViewModel Checkout { get; }

    /// <summary>Hoja "Terminar consulta" (cuenta y cobro de la consulta) abierta.</summary>
    [ObservableProperty]
    private bool _isFinishConsultationOpen;

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
    private string _serviceModalNotes = string.Empty;

    [ObservableProperty]
    private int _serviceModalQuantity = 1;

    // ==========================================
    // ALERTA DE ALERGIA FARMACOLÓGICA (SAFETY CHECK)
    // ==========================================
    [ObservableProperty]
    private bool _isAllergyAlertOpen;

    [ObservableProperty]
    private string _allergyAlertTitle = "Alerta de alergia a medicamento";

    [ObservableProperty]
    private string _allergyAlertMessage = string.Empty;

    [ObservableProperty]
    private string _allergyPatientAllergies = string.Empty;

    private Product? _pendingAllergyProduct;
    private string _pendingAllergyMedName = string.Empty;
    private string _pendingAllergyDosage = string.Empty;
    private int _pendingAllergyQty = 1;

    [RelayCommand]
    public void CancelAllergyPrescription()
    {
        _pendingAllergyProduct = null;
        _pendingAllergyMedName = string.Empty;
        _pendingAllergyDosage = string.Empty;
        _pendingAllergyQty = 1;
        IsAllergyAlertOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmOverrideAllergyPrescriptionAsync()
    {
        IsAllergyAlertOpen = false;
        var prod = _pendingAllergyProduct;
        var name = _pendingAllergyMedName;
        var dosage = _pendingAllergyDosage;
        var qty = _pendingAllergyQty;

        _pendingAllergyProduct = null;
        _pendingAllergyMedName = string.Empty;
        _pendingAllergyDosage = string.Empty;
        _pendingAllergyQty = 1;

        if (!string.IsNullOrWhiteSpace(name))
        {
            await AddPrescriptionItemInternalAsync(prod, name, dosage, qty);
        }
    }

    // ==========================================
    // CITAS DEL PACIENTE
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<PatientAppointment> _patientAppointmentsHistory = new();

    // ==========================================
    // EVOLUCIÓN DE SIGNOS VITALES (HISTORIAL Y GRÁFICAS)
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<VitalSignsEvolutionPoint> _vitalSignsEvolutionPoints = new();

    [ObservableProperty]
    private string _vitalSignsFilter = "Todos"; // "Todos", "Peso", "Presion", "Glucosa"

    [ObservableProperty]
    private string _weightPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _systolicPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _diastolicPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _glucosePolylinePoints = string.Empty;

    [ObservableProperty]
    private ObservableCollection<VitalSignsChartPoint> _chartPoints = new();

    [ObservableProperty]
    private bool _hasEnoughPointsForChart;

    [ObservableProperty]
    private string _latestWeightFormatted = "--";

    [ObservableProperty]
    private string _latestBPFormatted = "--";

    [ObservableProperty]
    private string _latestGlucoseFormatted = "--";

    [ObservableProperty]
    private string _latestIMCFormatted = "--";

    // ==========================================
    // INDICACIONES GENERALES EN CONSULTA ACTIVA
    // ==========================================
    [ObservableProperty]
    private string _consultationGeneralIndications = "Tomar abundantes líquidos, mantener reposo relativo y acudir a urgencias en caso de presentar datos de alarma.";


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
        var matches = string.IsNullOrWhiteSpace(query)
            ? AvailableProducts.Where(IsProductMedication).Take(20).ToList()
            : AvailableProducts
                .Where(IsProductMedication)
                .Where(p => TextSearch.Matches(TextSearch.Normalize(query), p.Name, p.GenericName, p.Code))
                .Take(20)
                .ToList();

        PrescriptionProductSuggestions = new ObservableCollection<Product>(matches);
        SelectedPrescriptionProduct = PrescriptionProductSuggestions.FirstOrDefault();
        IsPrescriptionDropdownOpen = !string.IsNullOrWhiteSpace(query) && PrescriptionProductSuggestions.Count > 0;
    }

    partial void OnSupplySearchTextChanged(string value)
    {
        ApplySupplySuggestions(value);
    }

    public void ApplySupplySuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? AvailableProducts.Where(IsProductSupply).Take(20).ToList()
            : AvailableProducts
                .Where(IsProductSupply)
                .Where(p => TextSearch.Matches(TextSearch.Normalize(query), p.Name, p.GenericName, p.Code))
                .Take(20)
                .ToList();

        SupplyProductSuggestions = new ObservableCollection<Product>(matches);
        SelectedSupplyProduct = SupplyProductSuggestions.FirstOrDefault();
        IsSupplyDropdownOpen = !string.IsNullOrWhiteSpace(query) && SupplyProductSuggestions.Count > 0;
    }

    partial void OnServiceModalSearchTextChanged(string value)
    {
        ApplyServiceModalSuggestions(value);
    }

    public void ApplyServiceModalSuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? Services.Take(20).ToList()
            : Services
                .Where(s => TextSearch.Matches(TextSearch.Normalize(query), s.Name, s.Code, s.Category))
                .Take(20)
                .ToList();

        ServiceModalSuggestions = new ObservableCollection<ClinicalService>(matches);
        SelectedServiceModalItem = ServiceModalSuggestions.FirstOrDefault();
        IsServiceModalDropdownOpen = !string.IsNullOrWhiteSpace(query) && ServiceModalSuggestions.Count > 0;
    }

    // ==========================================
    // EXPLORADOR DE CATÁLOGO (MEDICAMENTOS, INSUMOS, SERVICIOS)
    // ==========================================
    [ObservableProperty]
    private bool _isCatalogBrowserModalOpen;

    [ObservableProperty]
    private string _catalogBrowserTitle = "Catálogo";

    [ObservableProperty]
    private string _catalogBrowserSubtitle = "Explore y seleccione un elemento del catálogo";

    [ObservableProperty]
    private string _catalogBrowserMode = "Medications"; // "Medications", "Supplies", "Services"

    [ObservableProperty]
    private string _catalogBrowserSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _catalogBrowserFilteredProducts = new();

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _catalogBrowserFilteredServices = new();

    partial void OnCatalogBrowserSearchTextChanged(string value)
    {
        ApplyCatalogBrowserFilter();
    }

    public void ApplyCatalogBrowserFilter()
    {
        var q = TextSearch.Normalize(CatalogBrowserSearchText ?? string.Empty);

        if (CatalogBrowserMode == "Medications")
        {
            var list = AvailableProducts.Where(IsProductMedication);
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Presentation));
            }
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            var list = AvailableProducts.Where(IsProductSupply);
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Presentation));
            }
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
        }
        else if (CatalogBrowserMode == "Services")
        {
            var list = Services.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(s => TextSearch.Matches(q, s.Name, s.Code, s.Category, s.Description));
            }
            CatalogBrowserFilteredServices = new ObservableCollection<ClinicalService>(list.OrderBy(s => s.Name));
        }
    }

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
        Checkout = new CheckoutViewModel(inventoryService, () =>
            _clinicProfileService?.CurrentProfile?.DoctorNameWithTitle is { Length: > 0 } doctor ? doctor : "Dr. Titular")
        {
            // Quitar todos los cargos termina una consulta de cortesía sin registrar una venta en ceros
            EmptyChargeText = "Terminar consulta (sin cargos)",
            ChargeSuffix = " y terminar",
            Notify = (message, ok) => { if (!ok) ShowWarning(message); },
            Completed = OnConsultationChargedAsync,
            Failed = () => _ = LoadServicesAsync()
        };
        InitializeAutosave(consultationDraftStore);
        InitializePacientes(notificationService);
        InitializeHandoff(consultationHandoffService);
        _clinicProfileService = clinicProfileService;
        _printingService = printingService;

        if (_clinicProfileService != null)
        {
            _onProfileChangedHandler = () => RefreshProfileData();
            _clinicProfileService.ProfileChanged += _onProfileChangedHandler;
            RefreshProfileData();
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
            InitialLoad = LoadInitialDataAsync();
        }
        else
        {
            LoadFallbackServices();
            if (_consultationService != null && _consultationService.Patients.Count > 0)
            {
                Patients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
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
        await LoadPrescriptionTemplatesAsync();
        await LoadTodayAppointmentsAsync();
        TryRestoreConsultationDraft();
        await TryRestorePatientFormDraftAsync();
    }

    public async Task LoadPatientsAsync()
    {
        if (_inventoryService != null)
        {
            // Listado ligero: las notas de consulta solo se cargan para el expediente abierto
            var dbPatients = await _inventoryService.GetPatientSummariesAsync();
            var prevSelected = SelectedPatient;
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
                    bool forced = _forceFreshPatientIds.Contains(prevSelectedId);
                    if (ReferenceEquals(SelectedPatient, prevSelected)
                        && (forced || fresh.UpdatedAtUtc > prevSelected.UpdatedAtUtc)
                        && CanReplaceSelectedPatientInstance(prevSelected))
                    {
                        _forceFreshPatientIds.Remove(prevSelectedId);
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

            Patients = new ObservableCollection<PatientRecord>(dbPatients);

            _consultationService?.LoadPatientsFromDatabase(dbPatients);

            if (prevSelectedId > 0)
            {
                if (!ReferenceEquals(SelectedPatient, prevSelected))
                {
                    // Se eligió otro paciente mientras se leía la base: no se toca su ficha
                }
                else if (keptSelected == null)
                {
                    SelectedPatient = null; // El expediente fue dado de baja
                }
                else if (replacedSelected)
                {
                    ReplaceSelectedPatientKeepingView(keptSelected);
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

    /// <summary>
    /// Cambia la ficha abierta por la versión de la base sin perder lo que el médico tenía a la vista
    /// (búsqueda del historial, gráfica de signos y secciones abiertas).
    /// </summary>
    private void ReplaceSelectedPatientKeepingView(PatientRecord fresh)
    {
        var timelineSearch = TimelineSearchText;
        var vitalsVisible = IsVitalsChartVisible;
        var openSections = AntecedentSections.Where(a => a.IsOpen).Select(a => a.Key).ToHashSet();

        SelectedPatient = fresh;

        TimelineSearchText = timelineSearch;
        IsVitalsChartVisible = vitalsVisible;
        foreach (var section in AntecedentSections) section.IsOpen = openSections.Contains(section.Key);
    }

    /// <summary>
    /// La instancia abierta se puede cambiar por la de la base salvo que alguien trabaje sobre ella
    /// (alta o edición abierta, o es el paciente de la consulta en curso).
    /// </summary>
    private bool CanReplaceSelectedPatientInstance(PatientRecord patient) =>
        !IsPatientModalOpen
        && (_consultationRecordPatient == null || _consultationRecordPatient.Id != patient.Id)
        && !(HasActiveConsultation && ActiveConsultation?.PatientId == patient.Id);

    private void OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue)
    {
        // Solo un cambio real de paciente reinicia el estado de guardado de la consulta
        if (oldValue?.Id != newValue?.Id || newValue == null)
        {
            _isCurrentConsultationSaved = false;
        }
        OnPropertyChanged(nameof(ShowsPediatricDose));
        NotifyVitalSignsWarningChanged();
        RefreshChartData();
        NotifyPatientsPageState();
        if (newValue != null)
        {
            var list = newValue.Consultations.OrderByDescending(c => c.Date).ToList();
            ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
            RefreshVitalSignsEvolution(newValue);
            _ = LoadPatientAppointmentsAsync(newValue.Id);

            if (NeedsConsultationsLoad(newValue))
            {
                _ = LoadConsultationsForAsync(newValue, refreshSelectedHistory: true);
            }
        }
        else
        {
            ConsultationsHistory.Clear();
            RefreshVitalSignsEvolution(null);
            PatientAppointmentsHistory.Clear();
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

    private bool NeedsConsultationsLoad(PatientRecord patient) =>
        _inventoryService != null && patient.Id > 0 && patient.Consultations.Count == 0 && patient.ConsultationCount > 0;

    /// <summary>Carga bajo demanda el historial de un expediente del listado ligero.</summary>
    private async Task LoadConsultationsForAsync(PatientRecord patient, bool refreshSelectedHistory)
    {
        if (_inventoryService == null) return;
        try
        {
            patient.Consultations = await _inventoryService.GetPatientConsultationsAsync(patient.Id);
            if (refreshSelectedHistory && ReferenceEquals(SelectedPatient, patient))
            {
                RefreshConsultationsHistory(patient);
                RefreshVitalSignsEvolution(patient);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"No se pudo cargar el historial del expediente {patient.RecordNumber}", ex);
        }
    }

    private void RefreshConsultationsHistory(PatientRecord patient)
    {
        var list = patient.Consultations.OrderByDescending(c => c.Date).ToList();
        ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
    }

    public void SyncActiveConsultation()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationFolio));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));
        NotifyAccountChanged();

        if (HasActiveConsultation)
        {
            StartConsultationTimer();
        }
        else
        {
            StopConsultationTimer();
            _feeServiceOverrideId = null;
            _receivedHandoffId = null;
            _receivedHandoffToken = null;
            // Cualquier otro cierre (p. ej. cobrada desde el Punto de Venta) también cierra su receta
            if (_consultationRecordPatient != null) _ = EndConsultationOnRecordAsync(discarded: false);
        }

        OnActiveConsultationStateChangedForAutosave();
        NotifyPatientsPageState();
        NotifyPrescriptionStateChanged();
        NotifyHandoffState();
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

    public void RefreshProfileData(ClinicProfile? profile = null)
    {
        var p = profile ?? _clinicProfileService?.CurrentProfile;
        if (p != null)
        {
            DoctorName = string.IsNullOrWhiteSpace(p.DoctorName) ? "Dr. Titular" : p.DoctorName;
            DoctorLicense = p.DoctorLicense ?? string.Empty;
            DoctorSpecialty = string.IsNullOrWhiteSpace(p.DoctorSpecialty) ? "Médico General" : p.DoctorSpecialty;
            ClinicName = string.IsNullOrWhiteSpace(p.ClinicName) ? "Consultorio Médico" : p.ClinicName;
            ClinicAddress = p.ClinicAddress ?? string.Empty;
            ClinicPhone = p.ClinicPhone ?? string.Empty;
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
        if (_clinicProfileService != null && _onProfileChangedHandler != null)
        {
            _clinicProfileService.ProfileChanged -= _onProfileChangedHandler;
        }
        StopConsultationTimer();
        DisposePacientes();
        DisposeHandoff();
        FlushConsultationDraft();
        _autosaveTimer?.Dispose();
        _formDraftTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task LoadServicesAsync()
    {
        if (_inventoryService != null)
        {
            var dbServices = await _inventoryService.GetClinicalServicesAsync();
            Services = new ObservableCollection<ClinicalService>(dbServices);

            var prods = await _inventoryService.GetProductsAsync();
            AvailableProducts = new ObservableCollection<Product>(prods);
        }
        else
        {
            LoadFallbackServices();
        }

        ApplyPrescriptionSuggestions(PrescriptionSearchText ?? string.Empty);
        ApplySupplySuggestions(SupplySearchText ?? string.Empty);
        ApplyServiceModalSuggestions(ServiceModalSearchText ?? string.Empty);
        ApplyCatalogBrowserFilter();
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
        SelectedPatient = null;
    }

    [RelayCommand]
    public void SelectPatient(PatientRecord? patient)
    {
        if (patient != null)
        {
            SelectedPatient = patient;
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
        FormBirthDateText = string.Empty;
        FormBirthDate = null;
        FormAgeText = string.Empty;
        FormAge = 0;
        FormGender = string.Empty;
        FormPhone = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormBloodType = BloodTypeUnknown;
        FormAllergies = string.Empty;
        FormChronicDiseases = string.Empty;
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
        FormIsFemale = false;
        FormIsPediatric = false;

        FormClinicalNotes = string.Empty;

        IsPatientModalOpen = true;
    }

    [RelayCommand]
    public void EditPatient()
    {
        if (BlockIfPatientHandedOff(SelectedPatient)) return;
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente para editar su expediente clínico.";
            IsInfoModalVisible = true;
            return;
        }

        FormPatientId = SelectedPatient.Id;
        FormErrorMessage = null;

        FormRecordNumber = SelectedPatient.RecordNumber;
        FormFullName = SelectedPatient.FullName;

        ParseFullName(SelectedPatient.FullName, out var first, out var last, out var maternal);
        FormFirstName = first;
        FormLastName = last;
        FormMaternalLastName = maternal;

        FormBirthDateText = SelectedPatient.BirthDate?.ToString("dd/MM/yyyy") ?? string.Empty;
        FormAge = SelectedPatient.Age;
        FormAgeText = SelectedPatient.Age.ToString();
        FormGender = SelectedPatient.Gender;
        FormPhone = FormatPhoneNumber(SelectedPatient.Phone);
        FormEmail = SelectedPatient.Email;
        FormAddress = SelectedPatient.Address;
        FormBloodType = string.IsNullOrWhiteSpace(SelectedPatient.BloodType) ? BloodTypeUnknown : SelectedPatient.BloodType;
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
        NotifyFormProfileChanged();

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

        if (!string.IsNullOrWhiteSpace(FormBirthDateText) && FormBirthDate == null)
        {
            FormErrorMessage = "La fecha de nacimiento no es válida: use día/mes/año (por ejemplo 14/03/1992) y que no sea futura.";
            return;
        }

        int parsedAge;
        if (FormBirthDate is { } birthDate)
        {
            parsedAge = PatientRecord.CalculateAge(birthDate, DateTime.Today);
        }
        else if (!int.TryParse(FormAgeText, out parsedAge) || parsedAge < 0 || parsedAge > 130)
        {
            FormErrorMessage = "Capture la fecha de nacimiento (o la edad en años si no se conoce).";
            return;
        }

        if (string.IsNullOrWhiteSpace(FormGender))
        {
            FormErrorMessage = "Seleccione el sexo del paciente.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FormAllergies))
        {
            FormErrorMessage = "Indique las alergias del paciente o escriba \"Negadas\" (no es lo mismo que no haber preguntado).";
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
        patient.BloodType = string.IsNullOrWhiteSpace(FormBloodType) ? BloodTypeUnknown : FormBloodType;
        patient.Allergies = FormAllergies.Trim();
        patient.ChronicDiseases = FormChronicDiseases?.Trim() ?? string.Empty;
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

        // Los signos vitales no se capturan aquí: se conservan los registrados en las consultas
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
            SelectedPatient = patient;
        }

        IsPatientModalOpen = false;
        FormErrorMessage = null;
        ShowSuccess($"Expediente de {patient.FullName} guardado");
    }

    // ==========================================
    // OPERACIONES DE CONSULTA MÉDICA (NOM-004-SSA3)
    // ==========================================

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

    private bool _isCurrentConsultationSaved;

    public async Task<PatientConsultation?> SaveActiveConsultationToDbAsync(DateTime? endedAt = null)
    {
        if (SelectedPatient == null) return null;

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
        if (!string.IsNullOrWhiteSpace(ConsultationSpO2)) vitalsParts.Add($"SpO2: {ConsultationSpO2.Trim().TrimEnd('%')}%");
        if (!string.IsNullOrWhiteSpace(ConsultationIMC) && !ConsultationIMC.Contains("no disponibles")) vitalsParts.Add($"IMC: {ConsultationIMC.Trim()}");

        // Sin signos capturados la nota queda sin signos: copiar los del expediente los haría
        // pasar por medidos en esta consulta (y duplicaría puntos en la gráfica de evolución)
        var vitalSignsFormatted = string.Join(" | ", vitalsParts);

        var chiefComplaint = !string.IsNullOrWhiteSpace(ConsultationChiefComplaint)
            ? ConsultationChiefComplaint.Trim()
            : (!string.IsNullOrWhiteSpace(active?.ChiefComplaint) ? active.ChiefComplaint : "Consulta Médica General");

        var diagnosis = !string.IsNullOrWhiteSpace(ConsultationDiagnosis)
            ? ConsultationDiagnosis.Trim()
            : "Consulta Médica General";

        var treatmentPlan = !string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? ConsultationTreatmentPlan.Trim()
            : (!string.IsNullOrWhiteSpace(ConsultationGeneralIndications) ? ConsultationGeneralIndications.Trim() : SavedTreatmentPlanPlaceholder);

        var systemsReview = !string.IsNullOrWhiteSpace(ConsultationSystemsReview)
            ? ConsultationSystemsReview.Trim()
            : string.Empty;

        var folio = !string.IsNullOrWhiteSpace(ConsultationFolio)
            ? ConsultationFolio.Trim()
            : (!string.IsNullOrWhiteSpace(active?.Folio)
                ? active.Folio
                : $"CNS-{DateTime.Today:yyyyMMdd}-{Random.Shared.Next(100, 999)}");
        ConsultationFolio = folio;
        if (active != null) active.Folio = folio;

        // La receta de la nota es la que se ve, se imprime y se cobra (la del expediente en curso)
        var prescriptions = new List<PatientPrescriptionItem>(SelectedPatient.ActivePrescription ?? new List<PatientPrescriptionItem>());

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

        consultation.EnsureNumericVitalSigns();

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

        var existingInPatient = SelectedPatient.Consultations.FirstOrDefault(c => c.Folio == consultation.Folio);
        if (existingInPatient != null)
        {
            var idx = SelectedPatient.Consultations.IndexOf(existingInPatient);
            SelectedPatient.Consultations[idx] = consultation;
        }
        else
        {
            SelectedPatient.Consultations.Insert(0, consultation);
        }

        var existingInHistory = ConsultationsHistory.FirstOrDefault(c => c.Folio == consultation.Folio);
        if (existingInHistory != null)
        {
            var idx = ConsultationsHistory.IndexOf(existingInHistory);
            ConsultationsHistory[idx] = consultation;
        }
        else
        {
            ConsultationsHistory.Insert(0, consultation);
        }

        RefreshVitalSignsEvolution(SelectedPatient);

        return consultation;
    }

    [RelayCommand]
    public void StartActiveConsultation()
    {
        if (BlockIfReceivingHandoff()) return;
        if (SelectedPatient == null)
        {
            GoToPatientsHome();
            return;
        }

        _isCurrentConsultationSaved = false;

        if (_consultationService != null)
        {
            _consultationService.StartConsultation(SelectedPatient);
            BeginConsultationOnRecord(SelectedPatient);
            SyncActiveConsultation();

            ConsultationFolio = _consultationService.CurrentConsultation?.Folio ?? $"CNS-{DateTime.Now:yyyyMMdd-HHmmss}";
            ClearConsultationFields();
            NotifyAccountChanged();
            // La pantalla de consulta ya indica que está en curso: sin ventana emergente que interrumpa
            PatientsPage = PageConsultation;
        }
    }

    [RelayCommand]
    public async Task FinalizeConsultationAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (_consultationService == null || !_consultationService.HasActiveConsultation)
        {
            InfoModalMessage = "No hay ninguna consulta médica activa para terminar.";
            IsInfoModalVisible = true;
            return;
        }

        if (_inventoryService == null)
        {
            InfoModalMessage = "El servicio de inventario no está disponible.";
            IsInfoModalVisible = true;
            return;
        }

        // Guardar (o actualizar) la nota con la hora de término; si ya había un avance guardado,
        // se actualiza el mismo registro (mismo folio)
        var endedAt = DateTime.Now;
        _isCurrentConsultationSaved = false;
        await SaveActiveConsultationToDbAsync(endedAt);

        // Existencias al día para la cuenta (lo surtido se descuenta al cobrar)
        await LoadServicesAsync();
        Checkout.PatientName = SelectedPatient?.FullName ?? ActiveConsultationPatientName ?? CheckoutViewModel.DefaultClient;

        // Honorario + todo lo cargado durante la consulta. La consulta sigue activa hasta cobrar:
        // "Volver a la consulta" permite agregar algo que faltó.
        BuildCheckoutCart();
        IsFinishConsultationOpen = true;
    }

    /// <summary>"Volver a la consulta" (o Esc) desde Terminar consulta: no cobra ni la cierra.</summary>
    [RelayCommand]
    public void CloseFinishConsultation()
    {
        IsFinishConsultationOpen = false;

        // El cobro se rearma al terminar de nuevo y la nota se actualizará entonces
        if (HasActiveConsultation)
        {
            Checkout.Reset();
            _isCurrentConsultationSaved = false;
        }
        SyncActiveConsultation();
    }

    /// <summary>Se cobró la consulta (o se terminó sin cargos): se cierra y se vuelve al inicio.</summary>
    private async Task OnConsultationChargedAsync(SaleReceipt? receipt)
    {
        await HandleSaleCompletedFromConsultationAsync();
        if (receipt != null) ShowSuccess($"Consulta cobrada · {receipt.Folio}");
    }

    public async Task HandleSaleCompletedFromConsultationAsync()
    {
        try
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
            await EndConsultationOnRecordAsync(discarded: false);

            // 1. Cerrar "Terminar consulta" y cualquier otra hoja secundaria
            CloseFinishConsultation();
        IsPrescriptionModalOpen = false;
        IsSuppliesModalOpen = false;
        IsServicesModalOpen = false;
        IsPatientModalOpen = false;
        IsCatalogBrowserModalOpen = false;
        DismissInfoModal();

        // 2. Finalizar y cerrar la consulta médica en el servicio y detener cronómetro
        _consultationService?.CloseActiveConsultation();
        SyncActiveConsultation();
        StopConsultationTimer();

        // 3. Limpiar los campos de la consulta
        ClearConsultationFields();

        // 4. Salir de todo y volver a la vista inicial (sin paciente seleccionado)
        SelectedPatient = null;
        ConsultationsHistory.Clear();

        // 6. La cita que se atendía queda como atendida y se vuelve al inicio, listo para el siguiente
        await CompleteAttendedAppointmentAsync();
        GoToPatientsHome();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al finalizar consulta tras venta completada", ex);
        }
    }

    // ==========================================
    // OPERACIONES DE RECETA MÉDICA (NOM-004-SSA3)
    // ==========================================

    [RelayCommand]
    public async Task SelectAndAddPrescriptionProductAsync(Product? product)
    {
        if (SelectedPatient == null) return;

        string name;
        if (!string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            name = PrescriptionMedicineName.Trim();
        }
        else if (product != null)
        {
            name = string.IsNullOrWhiteSpace(product.Presentation)
                ? product.Name
                : $"{product.Name} ({product.Presentation})";
        }
        else if (!string.IsNullOrWhiteSpace(PrescriptionSearchText))
        {
            name = PrescriptionSearchText.Trim();
        }
        else
        {
            return;
        }

        // La lista de sugerencias puede dejar preseleccionado otro medicamento cuando el nombre se
        // escribe a mano: solo cuenta el producto que corresponde a lo recetado (alergias y surtido)
        if (product != null && !ProductMatchesName(product, name))
        {
            product = FindCatalogProduct(name);
        }

        var dosage = string.IsNullOrWhiteSpace(PrescriptionDosage)
            ? "Según indicación médica"
            : PrescriptionDosage.Trim();

        var qty = PrescriptionQuantity > 0 ? PrescriptionQuantity : 1;

        if (SelectedPatient != null)
        {
            var allergyCheck = InventoryService.CheckMedicationAllergy(SelectedPatient, product, name);
            if (allergyCheck.HasConflict)
            {
                _pendingAllergyProduct = product;
                _pendingAllergyMedName = name;
                _pendingAllergyDosage = dosage;
                _pendingAllergyQty = qty;
                AllergyAlertTitle = "Alerta de alergia a medicamento";
                AllergyAlertMessage = allergyCheck.WarningDetails;
                AllergyPatientAllergies = SelectedPatient.Allergies ?? "Sin alergias declaradas";
                IsAllergyAlertOpen = true;
                return;
            }
        }

        await AddPrescriptionItemInternalAsync(product, name, dosage, qty);
    }

    private async Task AddPrescriptionItemInternalAsync(Product? product, string name, string dosage, int qty)
    {
        if (SelectedPatient == null) return;

        var newItem = new PatientPrescriptionItem
        {
            MedicineName = name,
            Dosage = dosage,
            RecommendedQuantity = qty
        };

        // "Se surte aquí" marcado por defecto cuando hay existencias: se carga solo a la cuenta.
        // El producto solo cuenta si corresponde al nombre recetado (la lista de sugerencias puede
        // traer preseleccionado otro medicamento cuando el nombre se escribió a mano).
        if (HasActiveConsultation)
        {
            var dispensed = product != null && ProductMatchesName(product, name) ? product : null;
            newItem.DispenseInClinic = ChargePrescription(newItem, dispensed);
        }

        var currentList = new List<PatientPrescriptionItem>(SelectedPatient.ActivePrescription) { newItem };
        SelectedPatient.ActivePrescription = currentList;
        SelectedPatient.SyncPrescriptionJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        PrescriptionSearchText = string.Empty;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        SelectedPrescriptionProduct = null;
        ApplyPrescriptionSuggestions(string.Empty);

        OnPropertyChanged(nameof(SelectedPatient));
        NotifyPrescriptionStateChanged();
    }

    [RelayCommand]
    public void AddServiceToActiveConsultation(ClinicalService? service)
    {
        if (service == null || BlockIfConsultationReadOnly() || BlockIfReceivingHandoff()) return;

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
            // El cargo aparece al instante en "Cuenta de la consulta": sin ventana que interrumpa
        }
    }

    [RelayCommand]
    public void RemoveItemFromActiveConsultation(ConsultationProductItem? item)
    {
        if (_consultationService?.CurrentConsultation == null || item == null || BlockIfConsultationReadOnly()) return;

        _consultationService.CurrentConsultation.Items.Remove(item);
        _consultationService.CurrentConsultation.NotifyItemsChanged();
        SyncActiveConsultation();
    }

    [RelayCommand]
    public void AppendGeneralIndicationPreset(string text)
    {
        if (string.IsNullOrWhiteSpace(ConsultationGeneralIndications))
            ConsultationGeneralIndications = text;
        else
            ConsultationGeneralIndications = ConsultationGeneralIndications.Trim() + " " + text;

        // Los botones rápidos están junto al "Plan Terapéutico": el texto debe verse ahí
        // (antes solo se guardaba en un campo que no aparecía en pantalla)
        ConsultationTreatmentPlan = string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? text
            : ConsultationTreatmentPlan.TrimEnd() + " " + text;
    }

    [RelayCommand]
    public async Task RemovePrescriptionItemFromPatientAsync(PatientPrescriptionItem? item)
    {
        if (SelectedPatient == null || item == null) return;

        if (item.DispenseInClinic && HasActiveConsultation)
        {
            UnchargePrescription(item);
        }

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
        NotifyPrescriptionStateChanged();
    }

    [RelayCommand]
    public async Task OpenPrescriptionModalAsync()
    {
        OpenPrescriptionModal();
        if (AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
            ApplyPrescriptionSuggestions(string.Empty);
            IsPrescriptionDropdownOpen = false;
        }
    }

    public void OpenPrescriptionModal()
    {
        if (BlockIfConsultationReadOnly()) return;
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
        ApplyPrescriptionSuggestions(string.Empty);
        IsPrescriptionDropdownOpen = false;
        IsPrescriptionModalOpen = true;

        // Plantillas recién importadas del Excel (o llegadas por sincronización)
        _ = LoadPrescriptionTemplatesAsync();
        SelectedPediatricRule = null;
        PediatricWeightText = string.Empty;
    }

    [RelayCommand]
    public async Task OpenPrescriptionCatalogAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
        }
        CatalogBrowserMode = "Medications";
        CatalogBrowserTitle = "Catálogo de Medicamentos";
        CatalogBrowserSubtitle = "Explore todos los fármacos registrados y agregue el medicamento a la receta médica del paciente.";
        CatalogBrowserSearchText = string.Empty;
        ApplyCatalogBrowserFilter();
        IsCatalogBrowserModalOpen = true;
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
        ShowSuccess($"¡Receta médica guardada correctamente en el expediente de {SelectedPatient?.FullName}!");
    }

    // ==========================================
    // OPERACIONES DE MODAL DE INSUMOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public async Task OpenSuppliesModalAsync()
    {
        OpenSuppliesModal();
        if (AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
            ApplySupplySuggestions(string.Empty);
            IsSupplyDropdownOpen = false;
        }
    }

    public void OpenSuppliesModal()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar sus insumos y material clínico.";
            IsInfoModalVisible = true;
            return;
        }

        SupplySearchText = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        ApplySupplySuggestions(string.Empty);
        IsSupplyDropdownOpen = false;
        IsSuppliesModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenSupplyCatalogAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
        }
        CatalogBrowserMode = "Supplies";
        CatalogBrowserTitle = "Catálogo de Insumos y Material de Curación";
        CatalogBrowserSubtitle = "Explore todos los insumos clínicos disponibles y agregue el material utilizado al expediente.";
        CatalogBrowserSearchText = string.Empty;
        ApplyCatalogBrowserFilter();
        IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void CloseSuppliesModal()
    {
        IsSuppliesModalOpen = false;
    }

    [RelayCommand]
    public async Task SelectAndAddSupplyProductAsync(Product? product)
    {
        if (SelectedPatient == null || BlockIfConsultationReadOnly()) return;

        string name;
        decimal unitPrice;
        if (product != null)
        {
            name = string.IsNullOrWhiteSpace(product.Presentation)
                ? product.Name
                : $"{product.Name} ({product.Presentation})";
            unitPrice = product.Price;
        }
        else if (!string.IsNullOrWhiteSpace(SupplySearchText))
        {
            name = SupplySearchText.Trim();
            unitPrice = 35.00m;
        }
        else
        {
            return;
        }

        var instructions = string.IsNullOrWhiteSpace(SupplyInstructions)
            ? "Uso clínico en consulta"
            : SupplyInstructions.Trim();

        var qty = SupplyQuantity > 0 ? SupplyQuantity : 1;

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

        // Durante la consulta, lo que se usa se carga de inmediato a la cuenta (no se olvida cobrarlo)
        if (HasActiveConsultation)
        {
            ChargeSupply(newItem, product ?? FindCatalogProduct(name));
        }

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        SupplySearchText = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        SelectedSupplyProduct = null;
        ApplySupplySuggestions(string.Empty);

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task RemoveSupplyItemFromPatientAsync(PatientSupplyItem? item)
    {
        if (SelectedPatient == null || item == null || BlockIfConsultationReadOnly()) return;

        // Lo que se cargó a la cuenta al agregarlo durante la consulta deja de cobrarse
        if (IsConsultationForSelectedPatient)
        {
            UnchargeSupply(item);
        }

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
    public async Task SaveSuppliesModalAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
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
        ShowSuccess($"¡Insumos y materiales clínicos guardados correctamente en el expediente de {SelectedPatient?.FullName}!");
    }

    // ==========================================
    // OPERACIONES DE MODAL DE SERVICIOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public async Task OpenServicesModalAsync()
    {
        OpenServicesModal();
        if (Services.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
            ApplyServiceModalSuggestions(string.Empty);
            IsServiceModalDropdownOpen = false;
        }
    }

    public void OpenServicesModal()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar los servicios y procedimientos clínicos.";
            IsInfoModalVisible = true;
            return;
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        ApplyServiceModalSuggestions(string.Empty);
        IsServiceModalDropdownOpen = false;
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenServiceCatalogAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (Services.Count == 0 && _inventoryService != null)
        {
            await LoadServicesAsync();
        }
        CatalogBrowserMode = "Services";
        CatalogBrowserTitle = "Catálogo de Servicios y Procedimientos Clínicos";
        CatalogBrowserSubtitle = "Explore el catálogo oficial de servicios clínicos y agregue el procedimiento realizado al expediente.";
        CatalogBrowserSearchText = string.Empty;
        ApplyCatalogBrowserFilter();
        IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void CloseCatalogBrowser()
    {
        IsCatalogBrowserModalOpen = false;
    }

    [RelayCommand]
    public async Task AddProductFromCatalogBrowserAsync(Product? product)
    {
        if (product == null) return;

        if (CatalogBrowserMode == "Medications")
        {
            await SelectAndAddPrescriptionProductAsync(product);
            IsCatalogBrowserModalOpen = false;
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            await SelectAndAddSupplyProductAsync(product);
            IsCatalogBrowserModalOpen = false;
        }
    }

    /// <summary>Enter en el buscador del catálogo: agrega el primero de la lista.</summary>
    [RelayCommand]
    public async Task AddFirstFromCatalogBrowserAsync()
    {
        if (CatalogBrowserMode == "Services")
        {
            await AddServiceFromCatalogBrowserAsync(CatalogBrowserFilteredServices.FirstOrDefault());
        }
        else
        {
            // En insumos solo sirve lo que hay; en la receta se puede recetar aunque no se surta aquí
            var first = CatalogBrowserMode == "Supplies"
                ? CatalogBrowserFilteredProducts.FirstOrDefault(p => p.CurrentStock > 0)
                : CatalogBrowserFilteredProducts.FirstOrDefault();
            await AddProductFromCatalogBrowserAsync(first);
        }
    }

    public bool IsCatalogBrowserServicesMode => CatalogBrowserMode == "Services";

    public string CatalogBrowserSummary => IsCatalogBrowserServicesMode
        ? (CatalogBrowserFilteredServices.Count == 1 ? "1 servicio" : $"{CatalogBrowserFilteredServices.Count} servicios")
        : (CatalogBrowserFilteredProducts.Count == 1 ? "1 producto" : $"{CatalogBrowserFilteredProducts.Count} productos");

    public bool HasNoCatalogBrowserResults => IsCatalogBrowserServicesMode
        ? CatalogBrowserFilteredServices.Count == 0
        : CatalogBrowserFilteredProducts.Count == 0;

    partial void OnCatalogBrowserModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCatalogBrowserServicesMode));
        NotifyCatalogBrowserResults();
    }

    partial void OnCatalogBrowserFilteredProductsChanged(ObservableCollection<Product> value) => NotifyCatalogBrowserResults();
    partial void OnCatalogBrowserFilteredServicesChanged(ObservableCollection<ClinicalService> value) => NotifyCatalogBrowserResults();

    private void NotifyCatalogBrowserResults()
    {
        OnPropertyChanged(nameof(CatalogBrowserSummary));
        OnPropertyChanged(nameof(HasNoCatalogBrowserResults));
    }

    [RelayCommand]
    public async Task AddServiceFromCatalogBrowserAsync(ClinicalService? service)
    {
        if (service == null) return;
        await SelectAndAddServiceModalSuggestionAsync(service);
        IsCatalogBrowserModalOpen = false;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        IsServicesModalOpen = false;
    }

    [RelayCommand]
    public async Task SelectAndAddServiceModalSuggestionAsync(ClinicalService? service)
    {
        if (SelectedPatient == null || BlockIfConsultationReadOnly()) return;

        string name;
        decimal price;
        string category;
        string code;
        int serviceId;

        if (service != null)
        {
            name = service.Name;
            price = service.Price;
            category = service.Category;
            code = service.Code;
            serviceId = service.Id;
        }
        else if (!string.IsNullOrWhiteSpace(ServiceModalSearchText))
        {
            name = ServiceModalSearchText.Trim();
            var serviceObj = Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            price = serviceObj?.Price ?? 200.00m;
            category = serviceObj?.Category ?? "Consulta";
            code = serviceObj?.Code ?? "SERV-001";
            serviceId = serviceObj?.Id ?? 0;
        }
        else
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(ServiceModalNotes)
            ? "Procedimiento clínico en consulta"
            : ServiceModalNotes.Trim();

        var qty = ServiceModalQuantity > 0 ? ServiceModalQuantity : 1;

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

        // Durante la consulta, el servicio realizado se carga de inmediato a la cuenta
        if (HasActiveConsultation)
        {
            ChargeService(newItem);
        }

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(SelectedPatient);
            }
            catch { }
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        SelectedServiceModalItem = null;
        ApplyServiceModalSuggestions(string.Empty);

        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task RemoveServiceItemFromPatientAsync(PatientServiceItem? item)
    {
        if (SelectedPatient == null || item == null || BlockIfConsultationReadOnly()) return;

        // Lo que se cargó a la cuenta al agregarlo durante la consulta deja de cobrarse
        if (IsConsultationForSelectedPatient)
        {
            UnchargeService(item);
        }

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
    public async Task SaveServicesModalAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
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
        ShowSuccess($"¡Servicios clínicos guardados correctamente en el expediente de {SelectedPatient?.FullName}!");
    }

    // ==========================================
    // CITAS: ESTADO E HISTORIAL DEL PACIENTE (el panel de agenda vive en Pacientes)
    // ==========================================

    public async Task UpdateAppointmentStatusAsync(PatientAppointment? appt, string status)
    {
        if (appt == null || _inventoryService == null) return;

        try
        {
            await _inventoryService.UpdateAppointmentStatusAsync(appt.Id, status);
            appt.Status = status;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al actualizar estado de cita", ex);
        }
    }

    public async Task LoadPatientAppointmentsAsync(int? patientId)
    {
        if (_inventoryService == null || !patientId.HasValue || patientId.Value <= 0)
        {
            PatientAppointmentsHistory.Clear();
            return;
        }

        try
        {
            var all = await _inventoryService.GetAppointmentsAsync(DateTime.Today.AddMonths(-6), DateTime.Today.AddMonths(6));
            var patientList = all.Where(a => a.PatientRecordId == patientId.Value).OrderByDescending(a => a.Date).ToList();
            PatientAppointmentsHistory = new ObservableCollection<PatientAppointment>(patientList);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cargar historial de citas del paciente", ex);
        }
    }

    // ==========================================
    // EVOLUCIÓN DE SIGNOS VITALES (HISTORIAL Y GRÁFICAS)
    // ==========================================
    [RelayCommand]
    public void SetVitalSignsFilter(string filter)
    {
        VitalSignsFilter = filter;
        RecalculateChartPoints();
    }

    /// <summary>Filtros de la gráfica como chips (con acentos para leerse bien).</summary>
    public IReadOnlyList<string> VitalSignsFilterOptions { get; } = new[] { "Todos", "Peso", "Presión", "Glucosa" };

    public string VitalSignsFilterLabel
    {
        get => VitalSignsFilter == "Presion" ? "Presión" : VitalSignsFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            SetVitalSignsFilter(value == "Presión" ? "Presion" : value);
        }
    }

    partial void OnVitalSignsFilterChanged(string value) => OnPropertyChanged(nameof(VitalSignsFilterLabel));

    public void RefreshVitalSignsEvolution(PatientRecord? patient = null)
    {
        var pat = patient ?? SelectedPatient;
        if (pat == null)
        {
            // Colección nueva (no Clear): así "Lo esencial" (LatestVitals) también queda vacío
            VitalSignsEvolutionPoints = new ObservableCollection<VitalSignsEvolutionPoint>();
            ChartPoints.Clear();
            HasEnoughPointsForChart = false;
            LatestWeightFormatted = "--";
            LatestBPFormatted = "--";
            LatestGlucoseFormatted = "--";
            LatestIMCFormatted = "--";
            WeightPolylinePoints = string.Empty;
            SystolicPolylinePoints = string.Empty;
            DiastolicPolylinePoints = string.Empty;
            GlucosePolylinePoints = string.Empty;
            return;
        }

        var points = pat.GetVitalSignsEvolution();
        VitalSignsEvolutionPoints = new ObservableCollection<VitalSignsEvolutionPoint>(points);

        var latest = points.LastOrDefault();
        if (latest != null)
        {
            LatestWeightFormatted = latest.Weight.HasValue ? $"{latest.Weight.Value:F1} kg" : "--";
            LatestBPFormatted = (latest.Systolic.HasValue && latest.Diastolic.HasValue) ? $"{latest.Systolic.Value}/{latest.Diastolic.Value} mmHg" : "--";
            LatestGlucoseFormatted = latest.Glucose.HasValue ? $"{latest.Glucose.Value:F0} mg/dL" : "--";
            LatestIMCFormatted = latest.BMI.HasValue ? $"{latest.BMI.Value:F1}" : "--";
        }
        else
        {
            LatestWeightFormatted = "--";
            LatestBPFormatted = "--";
            LatestGlucoseFormatted = "--";
            LatestIMCFormatted = "--";
        }

        RecalculateChartPoints();
    }

    private void RecalculateChartPoints()
    {
        var points = VitalSignsEvolutionPoints.ToList();
        if (points.Count == 0)
        {
            HasEnoughPointsForChart = false;
            WeightPolylinePoints = string.Empty;
            SystolicPolylinePoints = string.Empty;
            DiastolicPolylinePoints = string.Empty;
            GlucosePolylinePoints = string.Empty;
            ChartPoints.Clear();
            return;
        }

        HasEnoughPointsForChart = true;

        double width = 640;
        double height = 200;
        double padLeft = 40;
        double padRight = 30;
        double padTop = 20;
        double padBottom = 30;

        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;

        int n = points.Count;
        double stepX = n > 1 ? plotW / (n - 1) : plotW / 2;

        var chartPts = new ObservableCollection<VitalSignsChartPoint>();

        var weightPoly = new StringBuilder();
        var sysPoly = new StringBuilder();
        var diaPoly = new StringBuilder();
        var gluPoly = new StringBuilder();

        for (int i = 0; i < n; i++)
        {
            var p = points[i];
            double x = n > 1 ? padLeft + (i * stepX) : padLeft + stepX;

            // Peso: escala 30..150 kg
            if (p.Weight.HasValue)
            {
                double normW = (double)Math.Clamp(p.Weight.Value, 30m, 150m);
                double yW = padTop + plotH - ((normW - 30.0) / 120.0 * plotH);
                weightPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yW));
                if (VitalSignsFilter == "Todos" || VitalSignsFilter == "Peso")
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yW,
                        ValueLabel = $"{p.Weight.Value:F1} kg",
                        SeriesName = "Peso"
                    });
                }
            }

            // Presión sistólica: escala 60..220 mmHg
            if (p.Systolic.HasValue)
            {
                double normSys = (double)Math.Clamp(p.Systolic.Value, 60m, 220m);
                double ySys = padTop + plotH - ((normSys - 60.0) / 160.0 * plotH);
                sysPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, ySys));
                if (VitalSignsFilter == "Todos" || VitalSignsFilter == "Presion")
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = ySys,
                        ValueLabel = $"{p.Systolic.Value} Sis",
                        SeriesName = "Presión Sistólica"
                    });
                }
            }

            // Presión diastólica: escala 40..130 mmHg
            if (p.Diastolic.HasValue)
            {
                double normDia = (double)Math.Clamp(p.Diastolic.Value, 40m, 130m);
                double yDia = padTop + plotH - ((normDia - 40.0) / 90.0 * plotH);
                diaPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yDia));
                if (VitalSignsFilter == "Todos" || VitalSignsFilter == "Presion")
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yDia,
                        ValueLabel = $"{p.Diastolic.Value} Dia",
                        SeriesName = "Presión Diastólica"
                    });
                }
            }

            // Glucosa: escala 50..300 mg/dL
            if (p.Glucose.HasValue)
            {
                double normGlu = (double)Math.Clamp(p.Glucose.Value, 50m, 300m);
                double yGlu = padTop + plotH - ((normGlu - 50.0) / 250.0 * plotH);
                gluPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yGlu));
                if (VitalSignsFilter == "Todos" || VitalSignsFilter == "Glucosa")
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yGlu,
                        ValueLabel = $"{p.Glucose.Value:F0} mg/dL",
                        SeriesName = "Glucosa"
                    });
                }
            }
        }

        WeightPolylinePoints = weightPoly.ToString().Trim();
        SystolicPolylinePoints = sysPoly.ToString().Trim();
        DiastolicPolylinePoints = diaPoly.ToString().Trim();
        GlucosePolylinePoints = gluPoly.ToString().Trim();
        ChartPoints = chartPts;
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
        if (SelectedPatient == null || SelectedPatient.ActivePrescription.Count == 0)
        {
            InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para imprimir.";
            IsInfoModalVisible = true;
            return;
        }

        var profile = _clinicProfileService?.CurrentProfile ?? new ClinicProfile
        {
            DoctorName = DoctorName,
            DoctorLicense = DoctorLicense,
            DoctorSpecialty = DoctorSpecialty,
            ClinicName = ClinicName,
            ClinicAddress = ClinicAddress,
            ClinicPhone = ClinicPhone
        };

        if (_printingService != null)
        {
            bool printed = _printingService.PrintPrescription(
                SelectedPatient, 
                SelectedPatient.ActivePrescription, 
                profile, 
                PrescriptionIndications);

            if (printed)
            {
                InfoModalMessage = "¡Receta médica enviada a la impresora física con éxito!";
            }
            else
            {
                InfoModalMessage = "Impresión de receta cancelada o no se seleccionó impresora.";
            }
        }
        else
        {
            InfoModalMessage = "La receta oficial NOM-004-SSA3 ha sido formateada con éxito para emisión física o guardado en PDF.";
        }

        IsInfoModalVisible = true;
        IsPrescriptionPdfModalOpen = false;
    }

    [RelayCommand]
    public void ExportPrescriptionDocument()
    {
        if (SelectedPatient == null || SelectedPatient.ActivePrescription.Count == 0)
        {
            InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para exportar.";
            IsInfoModalVisible = true;
            return;
        }

        var profile = _clinicProfileService?.CurrentProfile ?? new ClinicProfile
        {
            DoctorName = DoctorName,
            DoctorLicense = DoctorLicense,
            DoctorSpecialty = DoctorSpecialty,
            ClinicName = ClinicName,
            ClinicAddress = ClinicAddress,
            ClinicPhone = ClinicPhone
        };

        if (_printingService != null)
        {
            string? filePath = _printingService.ExportPrescriptionDocument(
                SelectedPatient, 
                SelectedPatient.ActivePrescription, 
                profile, 
                PrescriptionIndications);

            if (!string.IsNullOrEmpty(filePath))
            {
                InfoModalMessage = $"¡Receta médica oficial exportada con éxito!\nGuardada en: {filePath}";
            }
            else
            {
                InfoModalMessage = "Exportación de receta cancelada.";
            }
        }
        else
        {
            InfoModalMessage = "Servicio de exportación no disponible en este entorno.";
        }

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
}
