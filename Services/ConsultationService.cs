using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services;

public class ConsultationService
{
    private ActiveConsultation? _currentConsultation;
    public ActiveConsultation? CurrentConsultation => _currentConsultation;
    public bool HasActiveConsultation => _currentConsultation != null;

    public event Action? OnActiveConsultationChanged;

    /// <summary>La lista de pacientes en memoria cambió (recarga desde la base o alta/edición).</summary>
    public event Action? PatientsChanged;

    public ObservableCollection<PatientRecord> Patients { get; } = new();

    public ConsultationService() : this(seedDemoPatients: true)
    {
    }

    /// <param name="seedDemoPatients">
    /// Pacientes de demostración en memoria (solo para pruebas / vista sin base de datos).
    /// La aplicación real usa false y carga los expedientes desde SQLite.
    /// </param>
    public ConsultationService(bool seedDemoPatients)
    {
        if (seedDemoPatients)
        {
            InitializeDefaultPatients();
        }
    }

    public void LoadPatientsFromDatabase(IEnumerable<PatientRecord> patients)
    {
        Patients.Clear();
        foreach (var p in patients)
        {
            Patients.Add(p);
        }
        PatientsChanged?.Invoke();
    }

    public void UpsertPatient(PatientRecord patient)
    {
        var existing = Patients.FirstOrDefault(p => (p.Id > 0 && p.Id == patient.Id) || (!string.IsNullOrWhiteSpace(p.RecordNumber) && p.RecordNumber.Equals(patient.RecordNumber, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
        {
            var index = Patients.IndexOf(existing);
            Patients[index] = patient;
        }
        else
        {
            Patients.Insert(0, patient);
        }
        PatientsChanged?.Invoke();
    }

    private void InitializeDefaultPatients()
    {
        Patients.Add(new PatientRecord
        {
            Id = 1,
            RecordNumber = "EXP-2026-001",
            FullName = "Pérez Hernández, Juan",
            Age = 42,
            Gender = "Masculino",
            Phone = "55 1234 5678",
            BloodType = "O+",
            Allergies = "Penicilina",
            ChronicDiseases = "Hipertensión Arterial Sistémica (Controlada)",
            LastVisit = DateTime.Today.AddDays(-3),
            LastDiagnosis = "Faringoamigdalitis aguda bacteriana",
            VitalSigns = "TA: 120/80 mmHg | FC: 78 lpm | Temp: 37.2 °C | Peso: 78 kg",
            ClinicalNotes = "Acude por odinofagia de 48 hrs de evolución, hipertermia cuantificada en 38.1°C y astenia. A la exploración física orofaringe hiperémica con placas blanquecinas en amígdalas.",
            ActivePrescription = new List<PatientPrescriptionItem>
            {
                new() { MedicineName = "Amoxicilina con Ácido Clavulánico 500/125 mg", Dosage = "1 tableta c/8 hrs x 7 días", RecommendedQuantity = 1 },
                new() { MedicineName = "Ibuprofeno 400 mg", Dosage = "1 tableta c/8 hrs en caso de dolor o fiebre", RecommendedQuantity = 1 },
                new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta c/6 hrs si persiste fiebre > 38°C", RecommendedQuantity = 1 }
            }
        });

        Patients.Add(new PatientRecord
        {
            Id = 2,
            RecordNumber = "EXP-2026-002",
            FullName = "Gómez Soto, María Elena",
            Age = 28,
            Gender = "Femenino",
            Phone = "55 9876 5432",
            BloodType = "A+",
            Allergies = "Ninguna conocida",
            ChronicDiseases = "Ninguna",
            LastVisit = DateTime.Today.AddDays(-7),
            LastDiagnosis = "Cefalea tensional y fatiga ocular",
            VitalSigns = "TA: 115/75 mmHg | FC: 70 lpm | Temp: 36.4 °C | Peso: 61 kg",
            ClinicalNotes = "Refiere cefalea holocraneana de intensidad moderada asociada a jornadas prolongadas frente a monitor. Exploración neurológica normal.",
            ActivePrescription = new List<PatientPrescriptionItem>
            {
                new() { MedicineName = "Ibuprofeno 400 mg", Dosage = "1 tableta cada 8 horas si hay dolor", RecommendedQuantity = 1 }
            }
        });

        Patients.Add(new PatientRecord
        {
            Id = 3,
            RecordNumber = "EXP-2026-003",
            FullName = "Ramírez Torres, Ana Sofía",
            Age = 8,
            Gender = "Femenino",
            Phone = "55 4567 8901",
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
        });

        Patients.Add(new PatientRecord
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
        });
    }

    public ActiveConsultation StartConsultation(PatientRecord patient, string? chiefComplaint = null)
    {
        _currentConsultation = new ActiveConsultation
        {
            PatientId = patient.Id,
            PatientName = patient.FullName,
            PatientRecordNumber = patient.RecordNumber,
            ChiefComplaint = string.IsNullOrWhiteSpace(chiefComplaint) ? "Consulta Médica General" : chiefComplaint.Trim(),
            StartedAt = DateTime.Now
        };

        OnActiveConsultationChanged?.Invoke();
        return _currentConsultation;
    }

    public bool AddCartItemToActiveConsultation(int productId, string code, string name, string? genericName, string? presentation, string unit, decimal unitPrice, int quantity)
    {
        if (_currentConsultation == null || quantity <= 0)
            return false;

        var existing = _currentConsultation.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            _currentConsultation.Items.Add(new ConsultationProductItem
            {
                ProductId = productId,
                Code = code,
                Name = name,
                GenericName = genericName,
                Presentation = presentation,
                Unit = unit,
                UnitPrice = unitPrice,
                Quantity = quantity,
                AddedAt = DateTime.Now
            });
        }

        _currentConsultation.NotifyItemsChanged();
        OnActiveConsultationChanged?.Invoke();
        return true;
    }

    public void CloseActiveConsultation()
    {
        _currentConsultation = null;
        OnActiveConsultationChanged?.Invoke();
    }
}
