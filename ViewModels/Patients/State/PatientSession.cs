using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Pacientes y expediente abierto (fase 6, paso 7): la lista de expedientes, el paciente seleccionado
/// y su historial de consultas. Al cambiar cualquiera de ellos avisa al coordinador, que actualiza la
/// ficha, la consulta y la receta como siempre, antes del aviso de la propiedad.
/// </summary>
public partial class PatientSession : ObservableObject
{
    private readonly IPatientsHubs _hubs;
    private readonly TPVC.Services.ConsultationService? _consultationService;

    public PatientSession(IPatientsHubs hubs, TPVC.Services.ConsultationService? consultationService = null)
    {
        _hubs = hubs;
        _consultationService = consultationService;
    }

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _patients = new();

    partial void OnPatientsChanged(ObservableCollection<PatientRecord> value) => _hubs.OnPatientsChanged();

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
            _hubs.OnSelectedPatientChanged(oldValue, value);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private ObservableCollection<PatientConsultation> _consultationsHistory = new();

    partial void OnConsultationsHistoryChanged(ObservableCollection<PatientConsultation> value) => _hubs.OnConsultationsHistoryChanged(value);

    /// <summary>
    /// Expedientes que la próxima recarga toma de la base aunque la copia abierta parezca igual de
    /// reciente (llegaron cambios de otro equipo con una consulta pasada o recibida).
    /// </summary>
    public HashSet<int> ForceFreshPatientIds { get; } = new();

    /// <summary>
    /// El expediente abierto cambió por dentro (receta, insumos, servicios...): se avisa otra vez para
    /// que la ficha y las hojas lo vuelvan a leer. Es el único aviso de SelectedPatient sin cambiarlo.
    /// </summary>
    public void NotifySelectedPatientMutated() => OnPropertyChanged(nameof(SelectedPatient));

    public void RefreshConsultationsHistory(PatientRecord patient)
    {
        var list = patient.Consultations.OrderByDescending(c => c.Date).ToList();
        ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
    }

    public void LoadMockPatients()
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

    public void RemovePatient(PatientRecord? patient)
    {
        if (patient == null) return;

        _consultationService?.RemovePatient(patient);

        var existing = Patients.FirstOrDefault(p =>
            ReferenceEquals(p, patient) ||
            (patient.Id > 0 && p.Id == patient.Id) ||
            (!string.IsNullOrWhiteSpace(patient.RecordNumber) && string.Equals(p.RecordNumber, patient.RecordNumber, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            Patients.Remove(existing);
        }

        if (SelectedPatient != null &&
            (ReferenceEquals(SelectedPatient, patient) ||
             ReferenceEquals(SelectedPatient, existing) ||
             (patient.Id > 0 && SelectedPatient.Id == patient.Id)))
        {
            SelectedPatient = null;
        }

        _hubs.OnPatientsChanged();
    }
}
