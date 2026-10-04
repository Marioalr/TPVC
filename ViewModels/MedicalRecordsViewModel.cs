using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MedicalRecordsViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;

    public Action<string, int>? OnSendPrescriptionToPos { get; set; }
    public Action<ActiveConsultation>? OnCheckoutConsultationRequested { get; set; }
    public Action? OnNavigateToPosRequested { get; set; }
    public Action? OnBackToHubRequested { get; set; }

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

    [RelayCommand]
    public void SwitchPatientDetailTab(string tab)
    {
        PatientDetailTab = tab;
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
    private string? _infoModalMessage;

    [ObservableProperty]
    private bool _isInfoModalVisible;

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
    private string _formFullName = string.Empty;

    [ObservableProperty]
    private int _formAge = 30;

    [ObservableProperty]
    private string _formGender = "Masculino";

    [ObservableProperty]
    private string _formPhone = string.Empty;

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

    [ObservableProperty]
    private string _formInitialVitalSigns = "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C";

    [ObservableProperty]
    private string _formClinicalNotes = string.Empty;

    [ObservableProperty]
    private string? _formErrorMessage;

    public List<string> GenderOptions { get; } = new() { "Masculino", "Femenino", "Otro / No especificado" };
    public List<string> BloodTypeOptions { get; } = new() { "O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-" };

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
    private string _consultationPhysicalExam = string.Empty;

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

    public MedicalRecordsViewModel() : this(null, null)
    {
    }

    public MedicalRecordsViewModel(InventoryService? inventoryService, ConsultationService? consultationService = null)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;

        if (_consultationService != null)
        {
            _consultationService.OnActiveConsultationChanged += SyncActiveConsultation;
        }

        if (_inventoryService != null)
        {
            _inventoryService.OnInventoryChanged += () =>
            {
                _ = LoadPatientsAsync();
            };
            _ = LoadInitialDataAsync();
        }
        else if (_consultationService != null && _consultationService.Patients.Count > 0)
        {
            Patients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
            FilteredPatients = new ObservableCollection<PatientRecord>(_consultationService.Patients);
            SelectedPatient = Patients.FirstOrDefault();
        }
        else
        {
            LoadMockPatients();
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
            var prevSelectedId = SelectedPatient?.Id ?? 0;

            Patients = new ObservableCollection<PatientRecord>(dbPatients);
            ApplyPatientFilter();

            _consultationService?.LoadPatientsFromDatabase(dbPatients);

            if (prevSelectedId > 0)
            {
                SelectedPatient = Patients.FirstOrDefault(p => p.Id == prevSelectedId) ?? FilteredPatients.FirstOrDefault();
            }
            else if (SelectedPatient == null)
            {
                SelectedPatient = FilteredPatients.FirstOrDefault();
            }
        }
    }

    partial void OnSelectedPatientChanged(PatientRecord? value)
    {
        if (value != null)
        {
            var list = value.Consultations.OrderByDescending(c => c.Date).ToList();
            ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
            SelectedHistoryConsultation = ConsultationsHistory.FirstOrDefault();
        }
        else
        {
            ConsultationsHistory.Clear();
            SelectedHistoryConsultation = null;
        }
    }

    public void SyncActiveConsultation()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationFolio));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(ActiveConsultationItemsCount));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));
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
                Phone = "55 4123 8890",
                Email = "juan.garcia@email.com",
                Address = "Av. Insurgentes Sur 450, Col. Roma Sur, CDMX",
                BloodType = "O+",
                Allergies = "Penicilina (Reacción alérgica severa)",
                ChronicDiseases = "Ninguna relevante",
                EmergencyContactName = "Laura Morales (Esposa)",
                EmergencyContactPhone = "55 4123 8899",
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
                Phone = "55 9876 1234",
                Email = "maria.elena@email.com",
                Address = "Calle Durango 128, Col. Condesa, CDMX",
                BloodType = "A+",
                Allergies = "Ninguna conocida",
                ChronicDiseases = "Diabetes Mellitus Tipo 2, Hipertensión Arterial",
                EmergencyContactName = "Carlos Rodríguez (Hijo)",
                EmergencyContactPhone = "55 9876 1235",
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
                Phone = "55 3344 5566",
                Email = "sofia.hernandez@email.com",
                Address = "Calzada de Tlalpan 1890, CDMX",
                BloodType = "B+",
                Allergies = "Sulfas",
                ChronicDiseases = "Rinitis alérgica estacional",
                EmergencyContactName = "Patricia López (Madre)",
                EmergencyContactPhone = "55 3344 5560",
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
                Phone = "55 7788 9900",
                Email = "roberto.martinez@email.com",
                Address = "Eje Central Lázaro Cárdenas 55, CDMX",
                BloodType = "O+",
                Allergies = "Ninguna",
                ChronicDiseases = "Ninguna",
                EmergencyContactName = "Mariana Castro (Hermana)",
                EmergencyContactPhone = "55 7788 9911",
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
        SelectedPatient = list.FirstOrDefault();
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
            var q = SearchPatientText.Trim().ToLowerInvariant();
            FilteredPatients = new ObservableCollection<PatientRecord>(
                Patients.Where(p => 
                    p.FullName.ToLowerInvariant().Contains(q) ||
                    p.RecordNumber.ToLowerInvariant().Contains(q) ||
                    p.Phone.Contains(q) ||
                    p.LastDiagnosis.ToLowerInvariant().Contains(q) ||
                    (!string.IsNullOrEmpty(p.Email) && p.Email.ToLowerInvariant().Contains(q))
                )
            );
        }

        if (SelectedPatient != null && !FilteredPatients.Contains(SelectedPatient))
        {
            SelectedPatient = FilteredPatients.FirstOrDefault();
        }
        else if (SelectedPatient == null && FilteredPatients.Count > 0)
        {
            SelectedPatient = FilteredPatients.FirstOrDefault();
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

        FormFullName = string.Empty;
        FormAge = 30;
        FormGender = "Masculino";
        FormPhone = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormBloodType = "O+";
        FormAllergies = "Ninguna conocida";
        FormChronicDiseases = "Ninguna";
        FormEmergencyContactName = string.Empty;
        FormEmergencyContactPhone = string.Empty;
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
        FormAge = SelectedPatient.Age;
        FormGender = SelectedPatient.Gender;
        FormPhone = SelectedPatient.Phone;
        FormEmail = SelectedPatient.Email;
        FormAddress = SelectedPatient.Address;
        FormBloodType = SelectedPatient.BloodType;
        FormAllergies = SelectedPatient.Allergies;
        FormChronicDiseases = SelectedPatient.ChronicDiseases;
        FormEmergencyContactName = SelectedPatient.EmergencyContactName;
        FormEmergencyContactPhone = SelectedPatient.EmergencyContactPhone;
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
        if (string.IsNullOrWhiteSpace(FormFullName))
        {
            FormErrorMessage = "El nombre completo del paciente es obligatorio.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FormRecordNumber))
        {
            FormErrorMessage = "El folio del expediente es obligatorio.";
            return;
        }

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
            : new PatientRecord();

        patient.RecordNumber = normalizedFolio;
        patient.FullName = FormFullName.Trim();
        patient.Age = Math.Max(0, FormAge);
        patient.Gender = FormGender;
        patient.Phone = FormPhone.Trim();
        patient.Email = FormEmail.Trim();
        patient.Address = FormAddress.Trim();
        patient.BloodType = FormBloodType;
        patient.Allergies = string.IsNullOrWhiteSpace(FormAllergies) ? "Ninguna conocida" : FormAllergies.Trim();
        patient.ChronicDiseases = string.IsNullOrWhiteSpace(FormChronicDiseases) ? "Ninguna" : FormChronicDiseases.Trim();
        patient.EmergencyContactName = FormEmergencyContactName.Trim();
        patient.EmergencyContactPhone = FormEmergencyContactPhone.Trim();
        patient.VitalSigns = string.IsNullOrWhiteSpace(FormInitialVitalSigns) ? "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C" : FormInitialVitalSigns.Trim();
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
            InfoModalMessage = "Seleccione un paciente de la lista para registrar su consulta médica.";
            IsInfoModalVisible = true;
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

        var vitalSignsFormatted = $"TA: {ConsultationTA.Trim()} mmHg | FC: {ConsultationFC.Trim()} lpm | FR: {ConsultationFR.Trim()} rpm | Temp: {ConsultationTemp.Trim()} °C | Peso: {ConsultationWeight.Trim()} kg | Talla: {ConsultationHeight.Trim()} m | IMC: {ConsultationIMC.Trim()}";

        var consultation = new PatientConsultation
        {
            PatientRecordId = SelectedPatient.Id,
            Folio = string.IsNullOrWhiteSpace(ConsultationFolio) ? $"CNS-{DateTime.Today:yyyyMMdd}-001" : ConsultationFolio.Trim(),
            Date = DateTime.Now,
            ChiefComplaint = ConsultationChiefComplaint.Trim(),
            VitalSigns = vitalSignsFormatted,
            PhysicalExamination = ConsultationPhysicalExam.Trim(),
            Diagnosis = ConsultationDiagnosis.Trim(),
            TreatmentPlan = ConsultationTreatmentPlan.Trim(),
            Notes = ConsultationNotes.Trim(),
            Prescriptions = new List<PatientPrescriptionItem>(ConsultationPrescriptions)
        };

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.AddConsultationNoteAsync(SelectedPatient.Id, consultation);
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
    public void StartActiveConsultation()
    {
        if (SelectedPatient == null)
        {
            InfoModalMessage = "Seleccione un paciente para iniciar la consulta médica activa.";
            IsInfoModalVisible = true;
            return;
        }

        if (_consultationService != null)
        {
            _consultationService.StartConsultation(SelectedPatient);
            SyncActiveConsultation();
            InfoModalMessage = $"¡Consulta iniciada con éxito para {SelectedPatient.FullName}!\nFolio: {_consultationService.CurrentConsultation?.Folio}\n\nPuede acudir al Punto de Venta (POS) y presionar 'Agregar a Consulta Activa' para acumular insumos y medicamentos utilizados durante la atención médica.";
            IsInfoModalVisible = true;
        }
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
    public void CancelActiveConsultation()
    {
        if (_consultationService != null && _consultationService.HasActiveConsultation)
        {
            var patient = _consultationService.CurrentConsultation?.PatientName;
            _consultationService.CloseActiveConsultation();
            SyncActiveConsultation();
            InfoModalMessage = $"La consulta médica activa de {patient} ha sido finalizada/cerrada.";
            IsInfoModalVisible = true;
        }
    }

    [RelayCommand]
    public void DismissInfoModal()
    {
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
