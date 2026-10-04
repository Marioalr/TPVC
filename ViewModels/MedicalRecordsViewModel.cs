using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                BloodType = "O+",
                Allergies = "Penicilina (Reacción alérgica severa)",
                ChronicDiseases = "Ninguna relevante",
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
                BloodType = "A+",
                Allergies = "Ninguna conocida",
                ChronicDiseases = "Diabetes Mellitus Tipo 2, Hipertensión Arterial",
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
                BloodType = "B+",
                Allergies = "Sulfas",
                ChronicDiseases = "Rinitis alérgica estacional",
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
                BloodType = "O+",
                Allergies = "Ninguna",
                ChronicDiseases = "Ninguna",
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
        if (string.IsNullOrWhiteSpace(value))
        {
            FilteredPatients = new ObservableCollection<PatientRecord>(Patients);
        }
        else
        {
            var q = value.Trim().ToLowerInvariant();
            FilteredPatients = new ObservableCollection<PatientRecord>(
                Patients.Where(p => 
                    p.FullName.ToLowerInvariant().Contains(q) ||
                    p.RecordNumber.ToLowerInvariant().Contains(q) ||
                    p.Phone.Contains(q) ||
                    p.LastDiagnosis.ToLowerInvariant().Contains(q)
                )
            );
        }

        if (SelectedPatient != null && !FilteredPatients.Contains(SelectedPatient))
        {
            SelectedPatient = FilteredPatients.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void NewPatient()
    {
        InfoModalMessage = "El módulo de apertura de expedientes clínicos normados integrará:\n\n• Cumplimiento integral con la norma técnica NOM-004-SSA3.\n• Captura de antecedentes heredo-familiares y patológicos.\n• Formatos de consentimiento informado y firma digital.\n• Alertas sanitarias y semáforo de alergias medicamentosas.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public void NewConsultation()
    {
        InfoModalMessage = "El flujo integral de Nueva Consulta Médica integrará:\n\n• Registro en vivo de signos vitales (TA, FC, FR, Temp, Peso, IMC).\n• Notas médicas de evolución conforme a la NOM-004-SSA3.\n• Catálogo internacional de diagnósticos clínicos CIE-10.\n• Emisión de recetas médicas digitales con código QR.\n• Despacho y deducción inmediata en el inventario/POS.";
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
}
