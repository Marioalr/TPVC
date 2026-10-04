using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json;

namespace TPVC.Models;

public class PatientPrescriptionItem
{
    public string MedicineName { get; set; } = string.Empty;
    public string Dosage { get; set; } = string.Empty;
    public int RecommendedQuantity { get; set; } = 1;
}

public class PatientConsultation
{
    public int Id { get; set; }
    public int PatientRecordId { get; set; }
    public PatientRecord? PatientRecord { get; set; }
    public string Folio { get; set; } = string.Empty; // ej. CNS-20260404-001
    public DateTime Date { get; set; } = DateTime.Now;
    public string ChiefComplaint { get; set; } = string.Empty; // Motivo de consulta
    public string VitalSigns { get; set; } = string.Empty; // TA, FC, FR, Temp, Peso, Talla, IMC
    public string PhysicalExamination { get; set; } = string.Empty; // Exploración física
    public string Diagnosis { get; set; } = string.Empty; // Diagnóstico clínico
    public string TreatmentPlan { get; set; } = string.Empty; // Plan de tratamiento e indicaciones
    public string Notes { get; set; } = string.Empty; // Notas de evolución
    public string PrescriptionsJson { get; set; } = string.Empty;

    [NotMapped]
    private List<PatientPrescriptionItem>? _prescriptions;

    [NotMapped]
    public List<PatientPrescriptionItem> Prescriptions
    {
        get
        {
            if (_prescriptions == null)
            {
                if (string.IsNullOrWhiteSpace(PrescriptionsJson))
                {
                    _prescriptions = new List<PatientPrescriptionItem>();
                }
                else
                {
                    try
                    {
                        _prescriptions = JsonSerializer.Deserialize<List<PatientPrescriptionItem>>(PrescriptionsJson) ?? new();
                    }
                    catch
                    {
                        _prescriptions = new List<PatientPrescriptionItem>();
                    }
                }
            }
            return _prescriptions;
        }
        set
        {
            _prescriptions = value ?? new();
            PrescriptionsJson = JsonSerializer.Serialize(_prescriptions);
        }
    }
}

public class PatientRecord
{
    public int Id { get; set; }
    public string RecordNumber { get; set; } = string.Empty; // ej. EXP-2026-001
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = "No especificado";
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string BloodType { get; set; } = "O+";
    public string Allergies { get; set; } = "Ninguna conocida";
    public string ChronicDiseases { get; set; } = "Ninguna";
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;

    [NotMapped]
    public string FormattedPhone => FormatPhoneNumber(Phone);

    [NotMapped]
    public string FormattedEmergencyContactPhone => FormatPhoneNumber(EmergencyContactPhone);

    public static string FormatPhoneNumber(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length > 10) digits = digits.Substring(0, 10);

        if (digits.Length <= 3) return digits;
        if (digits.Length <= 6) return $"{digits.Substring(0, 3)}-{digits.Substring(3)}";
        return $"{digits.Substring(0, 3)}-{digits.Substring(3, 3)}-{digits.Substring(6)}";
    }
    public DateTime LastVisit { get; set; } = DateTime.Today;
    public string LastDiagnosis { get; set; } = string.Empty;
    public string VitalSigns { get; set; } = "TA: 120/80 mmHg | FC: 75 lpm | Temp: 36.5 °C";
    public string ClinicalNotes { get; set; } = string.Empty;
    public string ActivePrescriptionJson { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<PatientConsultation> Consultations { get; set; } = new();

    [NotMapped]
    private List<PatientPrescriptionItem>? _activePrescription;

    [NotMapped]
    public List<PatientPrescriptionItem> ActivePrescription
    {
        get
        {
            if (_activePrescription == null)
            {
                if (string.IsNullOrWhiteSpace(ActivePrescriptionJson))
                {
                    _activePrescription = new List<PatientPrescriptionItem>();
                }
                else
                {
                    try
                    {
                        _activePrescription = JsonSerializer.Deserialize<List<PatientPrescriptionItem>>(ActivePrescriptionJson) ?? new();
                    }
                    catch
                    {
                        _activePrescription = new List<PatientPrescriptionItem>();
                    }
                }
            }
            return _activePrescription;
        }
        set
        {
            _activePrescription = value ?? new();
            ActivePrescriptionJson = JsonSerializer.Serialize(_activePrescription);
        }
    }

    public void SyncPrescriptionJson()
    {
        ActivePrescriptionJson = JsonSerializer.Serialize(ActivePrescription);
    }

    public bool HasAllergies => !string.IsNullOrWhiteSpace(Allergies) && 
                                !Allergies.Equals("Ninguna", StringComparison.OrdinalIgnoreCase) && 
                                !Allergies.Equals("Ninguna conocida", StringComparison.OrdinalIgnoreCase);

    [NotMapped]
    public string GeneralIndications { get; set; } = string.Empty;
}
