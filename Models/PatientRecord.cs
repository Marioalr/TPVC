using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPVC.Models;

public class PatientPrescriptionItem : System.ComponentModel.INotifyPropertyChanged
{
    public string MedicineName { get; set; } = string.Empty;
    public string Dosage { get; set; } = string.Empty;
    public int RecommendedQuantity { get; set; } = 1;

    /// <summary>
    /// La presentación que se surte ("Caja con 20 tabletas"), aparte del nombre (art. 30 del Reglamento de Insumos para
    /// la Salud). Desde v1.23.0; las recetas anteriores no la traen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Presentation { get; set; }

    /// <summary>
    /// Lo que va junto al nombre en la hoja: " · Caja con 20 tabletas" (vacío sin presentación). La receta sigue sin
    /// cantidades, como la del Excel: la cantidad solo sirve para cobrar lo que se surte en el consultorio.
    /// </summary>
    [JsonIgnore]
    public string SheetDetail => string.IsNullOrWhiteSpace(Presentation) ? string.Empty : " · " + Presentation.Trim();

    private bool _dispenseInClinic;

    /// <summary>
    /// "Se surte aquí": el medicamento se entrega en el consultorio y se carga a la cuenta de la consulta.
    /// Se marca solo cuando hay existencias suficientes.
    /// </summary>
    public bool DispenseInClinic
    {
        get => _dispenseInClinic;
        set
        {
            if (_dispenseInClinic == value) return;
            _dispenseInClinic = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DispenseInClinic)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class PatientSupplyItem : System.ComponentModel.INotifyPropertyChanged
{
    public string SupplyName { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;

    private int _quantity = 1;

    /// <summary>Piezas usadas; la hoja la cambia con + / − (y la cuenta de la consulta con ella).</summary>
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Quantity)));
        }
    }

    public decimal UnitPrice { get; set; } = 0m;

    /// <summary>
    /// Consulta (folio) que cargó piezas de este renglón a su cuenta, y cuántas. La hoja guarda también
    /// los renglones de consultas anteriores: + / − y la X solo mueven lo que cargó la consulta en curso.
    /// Sin cargos no se escriben (el JSON de siempre).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ChargedConsultationFolio { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedUnits { get; set; }

    /// <summary>Renglón de la cuenta con que se cargó: Id del producto o, fuera del inventario, el del cargo libre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedLineId { get; set; }

    private bool _isFromEarlierConsultation;

    /// <summary>La hoja lo marca al abrirse: renglón de otra consulta, que solo se ve o se quita.</summary>
    [JsonIgnore]
    public bool IsFromEarlierConsultation
    {
        get => _isFromEarlierConsultation;
        set
        {
            if (_isFromEarlierConsultation == value) return;
            _isFromEarlierConsultation = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFromEarlierConsultation)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class PatientServiceItem : System.ComponentModel.INotifyPropertyChanged
{
    public int ServiceId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public decimal Price { get; set; } = 0m;
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Consulta (folio) que cargó este servicio a su cuenta, y cuántas unidades (cada una con sus
    /// insumos por defecto). La hoja guarda también los servicios de consultas anteriores: la X solo
    /// descuenta lo que cargó la consulta en curso. Sin cargos no se escriben (el JSON de siempre).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ChargedConsultationFolio { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedUnits { get; set; }

    private bool _isFromEarlierConsultation;

    /// <summary>La hoja lo marca al abrirse: servicio de otra consulta, que solo se ve o se quita.</summary>
    [JsonIgnore]
    public bool IsFromEarlierConsultation
    {
        get => _isFromEarlierConsultation;
        set
        {
            if (_isFromEarlierConsultation == value) return;
            _isFromEarlierConsultation = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFromEarlierConsultation)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class PatientConsultation : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is PatientConsultation other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public int PatientRecordId { get; set; }
    public PatientRecord? PatientRecord { get; set; }
    public string Folio { get; set; } = string.Empty; // ej. CNS-20260404-001
    public DateTime Date { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string ChiefComplaint { get; set; } = string.Empty; // Motivo de consulta
    public string VitalSigns { get; set; } = string.Empty; // TA, FC, FR, Temp, Peso, Talla, IMC

    // ==========================================
    // SIGNOS VITALES NUMÉRICOS Y METABÓLICOS
    // ==========================================
    public decimal? BloodPressureSystolic { get; set; } // TA Sistólica (mmHg)
    public decimal? BloodPressureDiastolic { get; set; } // TA Diastólica (mmHg)
    public decimal? HeartRate { get; set; } // FC (lpm)
    public decimal? RespiratoryRate { get; set; } // FR (rpm)
    public decimal? Temperature { get; set; } // Temp (°C)
    public decimal? Weight { get; set; } // Peso (kg)
    public decimal? Height { get; set; } // Talla (m)
    public decimal? BMI { get; set; } // IMC (kg/m²)
    public decimal? Glucose { get; set; } // Glucosa (mg/dL)
    public decimal? OxygenSaturation { get; set; } // SpO2 (%)

    public void EnsureNumericVitalSigns()
    {
        if (BloodPressureSystolic.HasValue || Weight.HasValue || Glucose.HasValue || OxygenSaturation.HasValue) return;
        ParseAndSetVitalSigns(VitalSigns);
    }

    public void ParseAndSetVitalSigns(string? vitalsText)
    {
        if (string.IsNullOrWhiteSpace(vitalsText)) return;

        // TA: 120/80 o T/A: 120/80
        var taMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"(?:T/?A|TA|PA|Presi[oó]n):\s*(\d{2,3})\s*/\s*(\d{2,3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (taMatch.Success)
        {
            if (decimal.TryParse(taMatch.Groups[1].Value, out var sys)) BloodPressureSystolic = sys;
            if (decimal.TryParse(taMatch.Groups[2].Value, out var dia)) BloodPressureDiastolic = dia;
        }

        // FC: 75
        var fcMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"FC:\s*(\d{2,3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fcMatch.Success && decimal.TryParse(fcMatch.Groups[1].Value, out var fc)) HeartRate = fc;

        // FR: 18
        var frMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"FR:\s*(\d{1,2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (frMatch.Success && decimal.TryParse(frMatch.Groups[1].Value, out var fr)) RespiratoryRate = fr;

        // Temp: 36.5
        var tempMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"(?:Temp|Temperatura):\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (tempMatch.Success && decimal.TryParse(tempMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var temp)) Temperature = temp;

        // Peso: 70.0
        var pesoMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"(?:Peso|Weight):\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pesoMatch.Success && decimal.TryParse(pesoMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var peso)) Weight = peso;

        // Talla: 1.70 o Talla: 95 cm
        var tallaMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"(?:Talla|Height|Estatura):\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (tallaMatch.Success && decimal.TryParse(tallaMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var talla))
        {
            if (talla > 3) talla /= 100m; // ej. 95 cm -> 0.95 m
            Height = talla;
        }

        // SpO2%: 0.94 o SpO2: 95
        var spo2Match = System.Text.RegularExpressions.Regex.Match(vitalsText, @"(?:SpO2%?|SatO2|SPO2)\s*[:=]?\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (spo2Match.Success && decimal.TryParse(spo2Match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var spo2))
        {
            if (spo2 > 0 && spo2 <= 1.0m) spo2 *= 100m;
            OxygenSaturation = spo2;
        }

        // IMC: 24.2
        var imcMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"IMC:\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (imcMatch.Success && decimal.TryParse(imcMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var imc)) BMI = imc;

        // Glucosa: 95
        var gluMatch = System.Text.RegularExpressions.Regex.Match(vitalsText, @"Glucosa:\s*([\d\.\,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gluMatch.Success && decimal.TryParse(gluMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var glu)) Glucose = glu;

        // Calcular IMC automáticamente si no vino explícito pero hay Peso y Talla
        if (!BMI.HasValue && Weight.HasValue && Height.HasValue && Height.Value > 0)
        {
            var h = Height.Value;
            if (h > 3) h /= 100m; // convertir cm a metros si es necesario (ej. 170 -> 1.70)
            if (h > 0)
            {
                BMI = Math.Round(Weight.Value / (h * h), 2);
            }
        }
    }

    public string PhysicalExamination { get; set; } = string.Empty; // Exploración física general
    public string SystemsReview { get; set; } = string.Empty; // Exploración e interrogatorio por aparatos y sistemas
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

    /// <summary>Orden de laboratorio de la consulta en JSON (<see cref="Models.LabOrder"/>); "" si no se pidieron estudios. Desde v1.25.0.</summary>
    public string LabOrderJson { get; set; } = string.Empty;

    /// <summary>La orden de laboratorio leída de <see cref="LabOrderJson"/> (una copia: para cambiarla se asigna otra).</summary>
    [NotMapped]
    public LabOrder LabOrder
    {
        get => Models.LabOrder.FromJson(LabOrderJson);
        set => LabOrderJson = (value ?? new Models.LabOrder()).ToJson();
    }
}

public class PatientRecord : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is PatientRecord other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public string RecordNumber { get; set; } = string.Empty; // ej. EXP-2026-001
    public string FullName { get; set; } = string.Empty;

    // Con fecha de nacimiento la edad se calcula al día (no se queda congelada en la del alta);
    // sin ella se usa la edad capturada. El campo no sigue la convención _age a propósito: así EF
    // guarda la edad vigente en cada escritura.
    private int _storedAge;
    public int Age
    {
        get => BirthDate is { } birth ? CalculateAge(birth, DateTime.Today) : _storedAge;
        set => _storedAge = value;
    }

    public string Gender { get; set; } = "No especificado";
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    // Sin valores supuestos: vacío = no se ha preguntado (antes O+ y "Ninguna conocida" se
    // guardaban aunque nadie lo hubiera interrogado)
    public string BloodType { get; set; } = string.Empty;
    public string Allergies { get; set; } = string.Empty;
    public string ChronicDiseases { get; set; } = string.Empty;
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;

    // Ficha de identificación extendida
    public DateTime? BirthDate { get; set; }
    public string Occupation { get; set; } = string.Empty;
    public string Religion { get; set; } = string.Empty;

    // Historia Clínica - Antecedentes Heredofamiliares (AHF)
    public string FamilyHistory { get; set; } = string.Empty;

    // Historia Clínica - Antecedentes Personales Patológicos (APP)
    public string Surgeries { get; set; } = string.Empty;
    public string Transfusions { get; set; } = string.Empty;
    public string Hospitalizations { get; set; } = string.Empty;
    public string ImmunizationSchedule { get; set; } = string.Empty; // Para niños / esquema de vacunación

    // Historia Clínica - Antecedentes Gineco-Obstétricos (AGO) (para mujeres)
    public string MenarcheAge { get; set; } = string.Empty; // Edad de la primera regla
    public string SexualOnsetAge { get; set; } = string.Empty; // Inicio de vida sexual activa
    public string SexualPartnersCount { get; set; } = string.Empty; // Número de parejas sexuales
    public string PeriodDuration { get; set; } = string.Empty; // Duración del ciclo / periodo
    public string IsPeriodHeavy { get; set; } = string.Empty; // Si el sangrado es abundante
    public string PapSmear { get; set; } = string.Empty; // Papanicolaou (fecha / resultado)
    public int PregnanciesCount { get; set; } = 0; // Número de embarazos (Gestas)
    public int CesareansCount { get; set; } = 0; // Cesáreas / Partos
    public int AbortionsCount { get; set; } = 0; // Abortos
    public bool IsCurrentlyPregnant { get; set; } = false; // Embarazo actual
    public string GestationalWeeks { get; set; } = string.Empty; // Semanas de gestación

    // Historia Clínica - Hitos del Desarrollo (para niños)
    public string DevelopmentalMilestones { get; set; } = string.Empty; // Hitos psicomotores (caminar, etc.)

    // Historia Clínica - Antecedentes Personales No Patológicos (APNP)
    public string Diet { get; set; } = string.Empty; // Alimentación
    public string HousingConditions { get; set; } = string.Empty; // Vivienda
    public string Zoonosis { get; set; } = string.Empty; // Zoonosis (convivencia con mascotas / animales)

    [NotMapped]
    public bool IsFemale => !string.IsNullOrWhiteSpace(Gender) && Gender.StartsWith("Fem", StringComparison.OrdinalIgnoreCase);

    [NotMapped]
    public bool IsPediatric => Age < 18;

    /// <summary>Antecedentes gineco-obstétricos: solo mujeres de 10 años en adelante.</summary>
    [NotMapped]
    public bool ShowsGynecoObstetric => IsFemale && Age >= GynecoObstetricMinAge;

    public const int GynecoObstetricMinAge = 10;

    /// <summary>Iniciales para el avatar ("García Morales, Juan Carlos" → "JG").</summary>
    [NotMapped]
    public string Initials
    {
        get
        {
            var name = (FullName ?? string.Empty).Trim();
            if (name.Length == 0) return "?";
            string first, second;
            if (name.Contains(','))
            {
                var parts = name.Split(',', 2);
                first = parts[1].Trim();
                second = parts[0].Trim();
            }
            else
            {
                var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                first = words[0];
                second = words.Length > 1 ? words[1] : string.Empty;
            }
            var a = first.Length > 0 ? char.ToUpperInvariant(first[0]).ToString() : string.Empty;
            var b = second.Length > 0 ? char.ToUpperInvariant(second[0]).ToString() : string.Empty;
            return a + b;
        }
    }

    /// <summary>"34 años", "2 años 6 meses", "8 meses" o "12 días".</summary>
    [NotMapped]
    public string AgeDisplay => FormatAge(BirthDate, Age, DateTime.Today);

    public static int CalculateAge(DateTime birthDate, DateTime today) => TotalMonths(birthDate, today) / 12;

    public static int TotalMonths(DateTime birthDate, DateTime today)
    {
        var months = (today.Year - birthDate.Year) * 12 + today.Month - birthDate.Month;
        if (today.Day < birthDate.Day) months--;
        return Math.Max(0, months);
    }

    public static string FormatAge(DateTime? birthDate, int age, DateTime today)
    {
        if (birthDate is not { } birth)
        {
            return age == 1 ? "1 año" : $"{Math.Max(0, age)} años";
        }

        var months = TotalMonths(birth, today);
        if (months < 1)
        {
            var days = Math.Max(0, (today.Date - birth.Date).Days);
            return days == 1 ? "1 día" : $"{days} días";
        }
        if (months < 24) return months == 1 ? "1 mes" : $"{months} meses";

        int years = months / 12, rest = months % 12;
        // Hasta los 6 años los meses importan (desarrollo, dosis); después solo los años
        if (years < 6 && rest > 0) return $"{years} años {rest} {(rest == 1 ? "mes" : "meses")}";
        return $"{years} años";
    }

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
    public string VitalSigns { get; set; } = string.Empty;
    public string ClinicalNotes { get; set; } = string.Empty;
    public string ActivePrescriptionJson { get; set; } = string.Empty;
    public DateTime? NextAppointmentDate { get; set; }
    public string NextAppointmentTime { get; set; } = string.Empty;
    public string NextAppointmentReason { get; set; } = string.Empty;
    public string NextAppointmentNotes { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<PatientConsultation> Consultations { get; set; } = new();

    private int? _consultationCount;

    /// <summary>
    /// Número de consultas del expediente. En el listado ligero viene del conteo en SQL
    /// (las notas no se cargan); con el historial cargado es el tamaño de la lista.
    /// </summary>
    [NotMapped]
    public int ConsultationCount
    {
        get => Consultations.Count > 0 ? Consultations.Count : _consultationCount ?? 0;
        set => _consultationCount = value;
    }

    [NotMapped]
    private List<PatientPrescriptionItem>? _activePrescription;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

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
                        _activePrescription = JsonSerializer.Deserialize<List<PatientPrescriptionItem>>(ActivePrescriptionJson, _jsonOptions) ?? new();
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
            ActivePrescriptionJson = JsonSerializer.Serialize(_activePrescription, _jsonOptions);
        }
    }

    public void SyncPrescriptionJson()
    {
        ActivePrescriptionJson = JsonSerializer.Serialize(ActivePrescription, _jsonOptions);
    }

    public string ActiveSuppliesJson { get; set; } = string.Empty;

    [NotMapped]
    private List<PatientSupplyItem>? _activeSupplies;

    [NotMapped]
    public List<PatientSupplyItem> ActiveSupplies
    {
        get
        {
            if (_activeSupplies == null)
            {
                if (string.IsNullOrWhiteSpace(ActiveSuppliesJson))
                {
                    _activeSupplies = new List<PatientSupplyItem>();
                }
                else
                {
                    try
                    {
                        _activeSupplies = JsonSerializer.Deserialize<List<PatientSupplyItem>>(ActiveSuppliesJson, _jsonOptions) ?? new();
                    }
                    catch
                    {
                        _activeSupplies = new List<PatientSupplyItem>();
                    }
                }
            }
            return _activeSupplies;
        }
        set
        {
            _activeSupplies = value ?? new();
            ActiveSuppliesJson = JsonSerializer.Serialize(_activeSupplies, _jsonOptions);
        }
    }

    public void SyncSuppliesJson()
    {
        ActiveSuppliesJson = JsonSerializer.Serialize(ActiveSupplies, _jsonOptions);
    }

    public string ActiveServicesJson { get; set; } = string.Empty;

    [NotMapped]
    private List<PatientServiceItem>? _activeServices;

    [NotMapped]
    public List<PatientServiceItem> ActiveServices
    {
        get
        {
            if (_activeServices == null)
            {
                if (string.IsNullOrWhiteSpace(ActiveServicesJson))
                {
                    _activeServices = new List<PatientServiceItem>();
                }
                else
                {
                    try
                    {
                        _activeServices = JsonSerializer.Deserialize<List<PatientServiceItem>>(ActiveServicesJson, _jsonOptions) ?? new();
                    }
                    catch
                    {
                        _activeServices = new List<PatientServiceItem>();
                    }
                }
            }
            return _activeServices;
        }
        set
        {
            _activeServices = value ?? new();
            ActiveServicesJson = JsonSerializer.Serialize(_activeServices, _jsonOptions);
        }
    }

    public void SyncServicesJson()
    {
        ActiveServicesJson = JsonSerializer.Serialize(ActiveServices, _jsonOptions);
    }

    public bool HasAllergies => !string.IsNullOrWhiteSpace(Allergies) && !DeniesAllergies(Allergies);

    /// <summary>No se ha interrogado: no es lo mismo que "negadas".</summary>
    [NotMapped]
    public bool AllergiesNotAsked => string.IsNullOrWhiteSpace(Allergies);

    /// <summary>Para la receta y resúmenes: cuáles, "Negadas" o "No interrogadas".</summary>
    [NotMapped]
    public string AllergySummary => HasAllergies
        ? Allergies.Trim()
        : AllergiesNotAsked ? "No interrogadas" : "Negadas";

    [NotMapped]
    public string AllergyBadgeText => HasAllergies
        ? $"ALERGIAS: {Allergies.Trim()}"
        : AllergiesNotAsked ? "Alergias sin interrogar" : "Alergias negadas";

    private static readonly string[] DeniedAllergyAnswers =
    {
        "ninguna", "ninguna conocida", "ningunas", "negadas", "negada", "niega", "niega alergias",
        "no", "sin alergias", "no conocidas", "no conocida", "negativas", "nada"
    };

    /// <summary>Respuestas que significan "sin alergias" ("Negadas", "Ninguna conocida"...).</summary>
    public static bool DeniesAllergies(string? text)
    {
        var normalized = TPVC.Services.TextSearch.Normalize(text).TrimEnd('.');
        return normalized.Length > 0 && DeniedAllergyAnswers.Contains(normalized);
    }

    /// <summary>Respuestas que significan "sin enfermedades crónicas" ("Ninguna relevante", "Niega"...).</summary>
    public static bool DeniesChronicDiseases(string? text)
    {
        var normalized = TPVC.Services.TextSearch.Normalize(text).TrimEnd('.');
        return normalized.Length > 0
            && (DeniedAllergyAnswers.Contains(normalized) || normalized.StartsWith("ningun", StringComparison.Ordinal)
                || normalized.StartsWith("niega", StringComparison.Ordinal) || normalized.StartsWith("negad", StringComparison.Ordinal)
                || normalized is "sin enfermedades" or "sin enfermedades cronicas" or "sin antecedentes" or "sin cronicos");
    }

    /// <summary>Hay enfermedades crónicas anotadas (no cuenta "Ninguna", "Negadas"...).</summary>
    [NotMapped]
    public bool HasChronicDiseases => !string.IsNullOrWhiteSpace(ChronicDiseases) && !DeniesChronicDiseases(ChronicDiseases);

    [NotMapped]
    public string ChronicBadgeText => HasChronicDiseases ? $"Crónicos: {ChronicDiseases.Trim()}" : string.Empty;

    [NotMapped]
    public string GeneralIndications { get; set; } = string.Empty;

    public List<PatientAppointment> Appointments { get; set; } = new();

    public List<VitalSignsEvolutionPoint> GetVitalSignsEvolution()
    {
        var points = new List<VitalSignsEvolutionPoint>();
        var ordered = Consultations.OrderBy(c => c.Date).ToList();
        foreach (var c in ordered)
        {
            c.EnsureNumericVitalSigns();
            if (c.Weight.HasValue || c.BloodPressureSystolic.HasValue || c.Glucose.HasValue || c.Temperature.HasValue || c.OxygenSaturation.HasValue)
            {
                points.Add(new VitalSignsEvolutionPoint
                {
                    Date = c.Date,
                    Weight = c.Weight,
                    Systolic = c.BloodPressureSystolic,
                    Diastolic = c.BloodPressureDiastolic,
                    Glucose = c.Glucose,
                    HeartRate = c.HeartRate,
                    Temperature = c.Temperature,
                    BMI = c.BMI
                });
            }
        }
        return points;
    }
}

public class VitalSignsEvolutionPoint
{
    public DateTime Date { get; set; }
    public string FormattedDate => Date.ToString("dd/MM/yy");
    public decimal? Weight { get; set; }
    public decimal? Systolic { get; set; }
    public decimal? Diastolic { get; set; }
    public decimal? Glucose { get; set; }
    public decimal? HeartRate { get; set; }
    public decimal? Temperature { get; set; }
    public decimal? BMI { get; set; }

    public string WeightText => Weight.HasValue ? $"{Weight.Value:F1} kg" : "--";
    public string BloodPressureText => (Systolic.HasValue && Diastolic.HasValue) ? $"{Systolic.Value:F0}/{Diastolic.Value:F0}" : "--";
    public string GlucoseText => Glucose.HasValue ? $"{Glucose.Value:F0} mg/dL" : "--";
    public string TempText => Temperature.HasValue ? $"{Temperature.Value:F1} °C" : "--";
}

public class VitalSignsChartPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public string ValueLabel { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
}

