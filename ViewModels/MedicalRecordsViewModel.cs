using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels;

public partial class MedicalRecordsViewModel : ObservableObject
{
    public Action<string, int>? OnSendPrescriptionToPos { get; set; }
    public Action? OnNavigateToPosRequested { get; set; }

    [ObservableProperty]
    private string _searchPatientText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _patients = new();

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _filteredPatients = new();

    [ObservableProperty]
    private PatientRecord? _selectedPatient;

    [ObservableProperty]
    private string? _infoModalMessage;

    [ObservableProperty]
    private bool _isInfoModalVisible;

    public MedicalRecordsViewModel()
    {
        LoadMockPatients();
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
        InfoModalMessage = "📌 [TODO / En Desarrollo]\nEl módulo de alta de expedientes clínicos integrará la norma oficial NOM-004-SSA3, captura de antecedentes heredo-familiares, consentimiento informado y firma digital.";
        IsInfoModalVisible = true;
    }

    [RelayCommand]
    public void NewConsultation()
    {
        InfoModalMessage = "📌 [TODO / En Desarrollo]\nEl módulo de consulta médica permitirá registrar notas de evolución, signos vitales en vivo, diagnóstico CIE-10 y generación de recetas con código QR.";
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
    public void DismissInfoModal()
    {
        IsInfoModalVisible = false;
        InfoModalMessage = null;
    }
}
