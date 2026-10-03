using System;
using System.Collections.Generic;

namespace TPVC.Models;

public class PatientPrescriptionItem
{
    public string MedicineName { get; set; } = string.Empty;
    public string Dosage { get; set; } = string.Empty;
    public int RecommendedQuantity { get; set; } = 1;
}

public class PatientRecord
{
    public int Id { get; set; }
    public string RecordNumber { get; set; } = string.Empty; // ej. EXP-2026-001
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = "No especificado";
    public string Phone { get; set; } = string.Empty;
    public string BloodType { get; set; } = "O+";
    public string Allergies { get; set; } = "Ninguna conocida";
    public string ChronicDiseases { get; set; } = "Ninguna";
    public DateTime LastVisit { get; set; } = DateTime.Today;
    public string LastDiagnosis { get; set; } = string.Empty;
    public string VitalSigns { get; set; } = "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C";
    public string ClinicalNotes { get; set; } = string.Empty;
    public List<PatientPrescriptionItem> ActivePrescription { get; set; } = new();

    public bool HasAllergies => !string.IsNullOrWhiteSpace(Allergies) && 
                                !Allergies.Equals("Ninguna", StringComparison.OrdinalIgnoreCase) && 
                                !Allergies.Equals("Ninguna conocida", StringComparison.OrdinalIgnoreCase);
}
