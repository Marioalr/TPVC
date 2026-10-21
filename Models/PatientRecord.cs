using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPVC.Models;

/// <summary>
/// Renglón de una hoja del paciente (paso 8.18): receta, insumos o servicios en <c>Active*Json</c>. Su <see cref="RowId"/>
/// lo identifica en cualquier equipo (la combinación por renglón del paso 8.22 lo usa) y <see cref="ExtensionData"/>
/// conserva los campos que agregue un equipo más nuevo, para que una reescritura de la PC no los tire. Las reglas, en
/// <see cref="TPVC.Services.Rules.PatientSheetRows"/>.
/// </summary>
public interface IPatientSheetRow
{
    Guid? RowId { get; set; }

    /// <summary>El nombre que entra al RowId determinista de un renglón viejo: el medicamento, el insumo o el servicio.</summary>
    string RowName { get; }

    Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Lo que identifica el renglón de la cuenta con que se cargaron unas piezas (paso 8.18): el insumo de la hoja y, desde
/// la revisión 14 del contrato, cada insumo de un servicio (<see cref="PatientServiceSupply"/>). Las reglas para
/// encontrarlo en cualquier equipo, en <see cref="TPVC.Services.Rules.PatientSheetRows.ResolveSupplyLine"/>.
/// </summary>
public interface IChargedSupplyLine
{
    /// <summary>El nombre del insumo: el cargo libre sale de él y, sin otra pista, se busca el producto por él.</summary>
    string SupplyName { get; }

    /// <summary>Id local del renglón de la cuenta en el equipo que escribió (un negativo es un cargo libre en cualquier equipo).</summary>
    int ChargedLineId { get; set; }

    /// <summary><c>producto</c> o <c>libre</c>.</summary>
    string? ChargedLineKind { get; set; }

    /// <summary>SyncId del producto; null en un cargo libre.</summary>
    Guid? ChargedLineSyncId { get; set; }
}

public class PatientPrescriptionItem : System.ComponentModel.INotifyPropertyChanged, IPatientSheetRow
{
    /// <summary>
    /// Paso 8.18: identidad del renglón en la hoja (GUID). Uno nuevo lo recibe al azar al guardarse; uno viejo, el
    /// determinista de <see cref="TPVC.Services.Rules.PatientSheetRows.LegacyRowId"/> al leerse. En el borrador de la
    /// consulta solo lo lleva la receta anterior (<c>PreviousPrescriptions</c>); sin él no se escribe.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RowId { get; set; }

    private string _medicineName = string.Empty;

    /// <summary>
    /// El renglón del medicamento tal como se imprime. Avisa al cambiar: «Mostrar (principio activo)» (2026-10-21) le quita o
    /// le pone el paréntesis en el mismo renglón.
    /// </summary>
    public string MedicineName
    {
        get => _medicineName;
        set
        {
            if (_medicineName == value) return;
            _medicineName = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MedicineName)));
        }
    }

    private string? _activeIngredient;

    /// <summary>
    /// Revisión 16 (pedido del médico del 2026-10-21): el principio activo que va entre paréntesis al final de
    /// <see cref="MedicineName"/> ("Paracetamol" en "Tempra 500 mg tabletas (Paracetamol)"), para quitarlo o ponerlo con
    /// «Mostrar (Paracetamol)». Solo lo lleva un renglón de un producto de marca que se recetó desde v1.46; null en uno
    /// genérico, escrito a mano o guardado antes (esos no cambian y no tienen la casilla).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveIngredient
    {
        get => _activeIngredient;
        set
        {
            if (_activeIngredient == value) return;
            _activeIngredient = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveIngredient)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasActiveIngredient)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveIngredientToggleText)));
        }
    }

    private bool? _showActiveIngredient;

    /// <summary>
    /// Revisión 16: si <see cref="MedicineName"/> lleva el principio activo entre paréntesis (true) o no (false). Va junto con
    /// <see cref="ActiveIngredient"/>; null sin él. <see cref="MedicineName"/> ya es el texto que resulta: quien no conoce el
    /// campo imprime el renglón tal cual.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowActiveIngredient
    {
        get => _showActiveIngredient;
        set
        {
            if (_showActiveIngredient == value) return;
            _showActiveIngredient = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowActiveIngredient)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowsActiveIngredient)));
        }
    }

    /// <summary>La casilla «Mostrar (…)» del renglón: sin <see cref="ShowActiveIngredient"/> cuenta como mostrado.</summary>
    [JsonIgnore]
    public bool ShowsActiveIngredient
    {
        get => ShowActiveIngredient != false;
        set => ShowActiveIngredient = value;
    }

    /// <summary>El renglón tiene algo entre paréntesis que se puede ocultar o mostrar.</summary>
    [JsonIgnore]
    public bool HasActiveIngredient => !string.IsNullOrWhiteSpace(ActiveIngredient);

    /// <summary>«Mostrar (Paracetamol)»: la casilla dice el texto que va entre paréntesis.</summary>
    [JsonIgnore]
    public string ActiveIngredientToggleText => HasActiveIngredient ? $"Mostrar ({ActiveIngredient!.Trim()})" : string.Empty;

    private string _dosage = string.Empty;

    /// <summary>
    /// La indicación (dosis, vía, frecuencia y duración). Avisa al cambiar: "Cambiar indicación" (2026-10-14) la corrige en
    /// el mismo renglón y las listas de la receta la muestran sin rehacerse.
    /// </summary>
    public string Dosage
    {
        get => _dosage;
        set
        {
            if (_dosage == value) return;
            _dosage = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Dosage)));
        }
    }

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

    string IPatientSheetRow.RowName => MedicineName;

    /// <summary>Paso 8.18: los campos que este equipo no conoce (de una versión más nueva) se vuelven a escribir tal cual.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class PatientSupplyItem : System.ComponentModel.INotifyPropertyChanged, IPatientSheetRow, IChargedSupplyLine
{
    /// <summary>Paso 8.18: identidad del renglón en la hoja (ver <see cref="PatientPrescriptionItem.RowId"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RowId { get; set; }

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

    /// <summary>
    /// Renglón de la cuenta con que se cargó: Id del producto o, fuera del inventario, el del cargo libre. Es el Id local
    /// del equipo que lo escribió; la PC lo sigue escribiendo para las PC anteriores al paso 8.18, que solo leen este.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedLineId { get; set; }

    /// <summary>
    /// Paso 8.18: la clase del renglón de la cuenta con que se cargó, con las palabras de la consulta entre equipos:
    /// <c>producto</c> (con <see cref="ChargedLineSyncId"/>) o <c>libre</c> (cada equipo calcula su Id con el nombre).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChargedLineKind { get; set; }

    /// <summary>Paso 8.18: SyncId del producto con que se cargó (vale en cualquier equipo); null en un cargo libre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ChargedLineSyncId { get; set; }

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

    string IPatientSheetRow.RowName => SupplyName;

    /// <summary>Paso 8.18: los campos que este equipo no conoce (de una versión más nueva) se vuelven a escribir tal cual.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class PatientServiceItem : System.ComponentModel.INotifyPropertyChanged, IPatientSheetRow
{
    /// <summary>Paso 8.18: identidad del renglón en la hoja (ver <see cref="PatientPrescriptionItem.RowId"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RowId { get; set; }

    /// <summary>Id local del servicio en el equipo que escribió el renglón (0 en un servicio fuera del catálogo).</summary>
    public int ServiceId { get; set; }

    /// <summary>Paso 8.18: SyncId del servicio del catálogo (vale en cualquier equipo); null fuera del catálogo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ServiceSyncId { get; set; }

    public string ServiceCode { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public decimal Price { get; set; } = 0m;

    private int _quantity = 1;

    /// <summary>Unidades del servicio; en la consulta en curso la hoja la cambia con + / − (y la cuenta con ella).</summary>
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            Raise(nameof(Quantity));
        }
    }

    /// <summary>
    /// Consulta (folio) que cargó este servicio a su cuenta, y cuántas unidades (cada una con sus
    /// insumos por defecto). La hoja guarda también los servicios de consultas anteriores: la X solo
    /// descuenta lo que cargó la consulta en curso. Sin cargos no se escriben (el JSON de siempre).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ChargedConsultationFolio { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedUnits { get; set; }

    private List<PatientServiceSupply>? _supplies;

    /// <summary>
    /// Revisión 14 del contrato: los insumos que este renglón cargó a la cuenta de su consulta, con sus piezas. Empieza con los
    /// insumos por defecto del servicio por sus unidades, y el médico la ajusta solo para esta consulta (la configuración del
    /// servicio no cambia). Es lo que se descuenta al quitar el renglón. null = renglón sin lista (de una versión anterior, o
    /// que no se cargó a ninguna cuenta): se descuenta como antes, con los insumos por defecto que tenga el servicio. Reglas
    /// en <see cref="TPVC.Services.Rules.ServiceRowSupplies"/>; sin lista no se escribe.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PatientServiceSupply>? Supplies
    {
        get => _supplies;
        set
        {
            _supplies = value;
            NotifySuppliesChanged();
        }
    }

    /// <summary>"Gasa ×1 · Jeringa ×1": los insumos de la lista en una línea (vacío sin lista o sin insumos).</summary>
    [JsonIgnore]
    public string SuppliesSummary => TPVC.Services.Rules.ServiceRowSupplies.Summary(_supplies);

    /// <summary>La lista tiene algún insumo.</summary>
    [JsonIgnore]
    public bool HasSupplies => _supplies is { Count: > 0 };

    /// <summary>Avisa a la pantalla que cambió la lista de insumos o las piezas de alguno.</summary>
    public void NotifySuppliesChanged()
    {
        Raise(nameof(Supplies));
        Raise(nameof(SuppliesSummary));
        Raise(nameof(HasSupplies));
    }

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
            Raise(nameof(IsFromEarlierConsultation));
        }
    }

    private bool _isEditingSupplies;

    /// <summary>La hoja de servicios tiene abiertos los insumos de este renglón (uno a la vez); no se guarda.</summary>
    [JsonIgnore]
    public bool IsEditingSupplies
    {
        get => _isEditingSupplies;
        set
        {
            if (_isEditingSupplies == value) return;
            _isEditingSupplies = value;
            Raise(nameof(IsEditingSupplies));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    string IPatientSheetRow.RowName => ServiceName;

    /// <summary>Paso 8.18: los campos que este equipo no conoce (de una versión más nueva) se vuelven a escribir tal cual.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Un insumo que cargó un renglón de servicio a la cuenta de su consulta (<see cref="PatientServiceItem.Supplies"/>, revisión 14
/// del contrato): sus piezas en total (no por unidad del servicio) y con qué renglón de la cuenta se cargaron, con los mismos
/// campos y reglas que un insumo de la hoja (paso 8.18): la clase y el SyncId valen en cualquier equipo, y la PC escribe además
/// su Id local. Los campos que no conoce se vuelven a escribir tal cual.
/// </summary>
public class PatientServiceSupply : System.ComponentModel.INotifyPropertyChanged, IChargedSupplyLine
{
    /// <summary>El nombre del producto (el de su renglón de la cuenta), o el del cargo libre.</summary>
    public string SupplyName { get; set; } = string.Empty;

    private int _quantity = 1;

    /// <summary>Piezas que el renglón cargó a la cuenta (todas sus unidades juntas); 1 si falta, como en los demás renglones.</summary>
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

    /// <summary>Precio por pieza al cargarlo (lo usa un cargo libre).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Id local del renglón de la cuenta en el equipo que escribió (ver <see cref="PatientSupplyItem.ChargedLineId"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChargedLineId { get; set; }

    /// <summary><c>producto</c> o <c>libre</c>, como en la hoja de insumos.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChargedLineKind { get; set; }

    /// <summary>SyncId del producto; null en un cargo libre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ChargedLineSyncId { get; set; }

    /// <summary>Los campos que este equipo no conoce (de una versión más nueva) se vuelven a escribir tal cual.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

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

    /// <summary>
    /// Paso 8.17: el folio con letra con que se creó sin conexión (EXP-2026-T1-001), después de que la PC principal le dio
    /// el número normal. La búsqueda también lo compara. null si nunca llevó letra.
    /// </summary>
    public string? OriginalFolio { get; set; }

    public string FullName { get; set; } = string.Empty;

    // Con fecha de nacimiento la edad se calcula al día (no se queda congelada en la del alta);
    // sin ella se usa la edad capturada. El campo no sigue la convención _age a propósito: así EF
    // guarda la edad vigente en cada escritura.
    private int _storedAge;
    public int Age
    {
        get => BirthDate is { } birth ? CalculateAge(birth, ClinicClock.Today) : _storedAge;
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
    public string AgeDisplay => FormatAge(BirthDate, Age, ClinicClock.Today);

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

    /// <summary>
    /// Paso 8.23: el SyncId del expediente con que se unió este (la herramienta de unir duplicados es el opcional 8.A; hasta
    /// entonces nadie la escribe). No es una referencia: el SyncId tal cual, sin llave foránea. Viaja y se combina por campo
    /// como cualquier otro campo (8.22). null = no se unió a otro; uno con valor no cuenta para los avisos de duplicados.
    /// </summary>
    public Guid? MergedIntoSyncId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Paso 8.22: cuándo cambió por última vez cada campo y cada renglón de las hojas, para combinar por campo al bajar
    /// (<see cref="TPVC.Services.Rules.PatientFieldStamps"/>, <see cref="TPVC.Services.Rules.PatientMerge"/>). La mantienen el
    /// sello de cada guardado y la combinación; nadie la edita a mano. No es un campo del expediente (paso 8.21): guardar una
    /// copia nunca escribe la suya.
    /// </summary>
    public string FieldStampsJson { get; set; } = string.Empty;

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

    /// <summary>
    /// Lee una hoja guardada. Paso 8.18: cada renglón sin <c>RowId</c> (escrito antes del paso o por un equipo que no lo
    /// conoce) recibe el determinista del paciente, la hoja, su posición y su nombre, así dos equipos le ponen el mismo.
    /// Se guarda con la siguiente escritura de la hoja (no hay relleno al arrancar).
    /// </summary>
    private List<T> ReadSheet<T>(string json, string column) where T : IPatientSheetRow => ReadSheet<T>(json, column, SyncId);

    private static List<T> ReadSheet<T>(string? json, string column, Guid patientSyncId) where T : IPatientSheetRow
    {
        List<T> rows;
        if (string.IsNullOrWhiteSpace(json))
        {
            rows = new List<T>();
        }
        else
        {
            try
            {
                rows = JsonSerializer.Deserialize<List<T>>(json, _jsonOptions) ?? new();
            }
            catch
            {
                rows = new List<T>();
            }
        }
        TPVC.Services.Rules.PatientSheetRows.AssignLegacyRowIds(rows, patientSyncId, column);
        return rows;
    }

    /// <summary>Escribe una hoja: un renglón nuevo (sin <c>RowId</c>) recibe uno al azar (paso 8.18).</summary>
    private static string WriteSheet<T>(List<T> rows) where T : IPatientSheetRow
    {
        TPVC.Services.Rules.PatientSheetRows.AssignNewRowIds(rows);
        return JsonSerializer.Serialize(rows, _jsonOptions);
    }

    /// <summary>
    /// Paso 8.22: los renglones de una hoja guardada tal como los lee la PC (con el <c>RowId</c> determinista de un renglón
    /// viejo), en su posición; un elemento nulo del arreglo queda nulo. Para combinar por renglón sin una instancia.
    /// </summary>
    internal static List<IPatientSheetRow?> ReadSheetRows(string column, string? json, Guid patientSyncId) => column switch
    {
        TPVC.Services.Rules.PatientSheetRows.PrescriptionColumn => ReadSheet<PatientPrescriptionItem>(json, column, patientSyncId).Cast<IPatientSheetRow?>().ToList(),
        TPVC.Services.Rules.PatientSheetRows.SuppliesColumn => ReadSheet<PatientSupplyItem>(json, column, patientSyncId).Cast<IPatientSheetRow?>().ToList(),
        TPVC.Services.Rules.PatientSheetRows.ServicesColumn => ReadSheet<PatientServiceItem>(json, column, patientSyncId).Cast<IPatientSheetRow?>().ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "No es una hoja del paciente.")
    };

    /// <summary>Paso 8.22: el texto de una hoja con estos renglones, con el formato de la PC.</summary>
    internal static string WriteSheetRows(string column, IEnumerable<IPatientSheetRow> rows) => column switch
    {
        TPVC.Services.Rules.PatientSheetRows.PrescriptionColumn => WriteSheet(rows.Cast<PatientPrescriptionItem>().ToList()),
        TPVC.Services.Rules.PatientSheetRows.SuppliesColumn => WriteSheet(rows.Cast<PatientSupplyItem>().ToList()),
        TPVC.Services.Rules.PatientSheetRows.ServicesColumn => WriteSheet(rows.Cast<PatientServiceItem>().ToList()),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "No es una hoja del paciente.")
    };

    /// <summary>
    /// Paso 8.21: el texto con que esta PC escribiría la hoja guardada en <paramref name="json"/>: leída como siempre (con
    /// el <c>RowId</c> determinista de un renglón viejo) y escrita con su formato. Dos textos con el mismo significado
    /// (otro orden de llaves, otro escape, sin los RowId que se derivan) dan el mismo; así una hoja que nadie tocó no
    /// cuenta como cambiada. No toca las listas de la instancia.
    /// </summary>
    internal string CanonicalSheetJson(string column, string? json) => column switch
    {
        TPVC.Services.Rules.PatientSheetRows.PrescriptionColumn => WriteSheet(ReadSheet<PatientPrescriptionItem>(json, column, SyncId)),
        TPVC.Services.Rules.PatientSheetRows.SuppliesColumn => WriteSheet(ReadSheet<PatientSupplyItem>(json, column, SyncId)),
        TPVC.Services.Rules.PatientSheetRows.ServicesColumn => WriteSheet(ReadSheet<PatientServiceItem>(json, column, SyncId)),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "No es una hoja del paciente.")
    };

    /// <summary>Paso 8.21: vuelve a escribir el texto de una hoja con lo que tiene su lista (como <c>Sync*Json</c>).</summary>
    internal void SyncSheetJson(string column)
    {
        switch (column)
        {
            case TPVC.Services.Rules.PatientSheetRows.PrescriptionColumn: SyncPrescriptionJson(); break;
            case TPVC.Services.Rules.PatientSheetRows.SuppliesColumn: SyncSuppliesJson(); break;
            case TPVC.Services.Rules.PatientSheetRows.ServicesColumn: SyncServicesJson(); break;
            default: throw new ArgumentOutOfRangeException(nameof(column), column, "No es una hoja del paciente.");
        }
    }

    /// <summary>
    /// Paso 8.21: la foto de los campos tal como estaban en la base la última vez que esta instancia estuvo al día con ella
    /// (al leerla y después de cada guardado). Guardar compara con ella para escribir solo lo que cambió; null en una
    /// instancia hecha en memoria, que se guarda completa como antes. Ver <see cref="TPVC.Services.PatientFields"/>.
    /// No es columna ni viaja en ningún JSON.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    internal TPVC.Services.PatientFieldSnapshot? StoredSnapshot { get; set; }

    /// <summary>
    /// Lo que la nota de una consulta deja en el resumen del expediente: <c>AddConsultationNoteAsync</c> lo escribe en la
    /// fila y la consulta en curso lo refleja en la instancia abierta con esta misma regla. Regresa los campos que cambió
    /// (paso 8.21: la instancia los marca como guardados, ver <see cref="TPVC.Services.PatientFields.MarkStored"/>).
    /// </summary>
    internal IReadOnlyList<string> ApplyConsultationSummary(PatientConsultation consultation)
    {
        var fields = new List<string> { nameof(LastVisit) };
        LastVisit = consultation.Date;
        if (!string.IsNullOrWhiteSpace(consultation.Diagnosis))
        {
            LastDiagnosis = consultation.Diagnosis;
            fields.Add(nameof(LastDiagnosis));
        }
        if (!string.IsNullOrWhiteSpace(consultation.VitalSigns))
        {
            VitalSigns = consultation.VitalSigns;
            fields.Add(nameof(VitalSigns));
        }
        if (!string.IsNullOrWhiteSpace(consultation.Notes) || !string.IsNullOrWhiteSpace(consultation.TreatmentPlan))
        {
            ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
            fields.Add(nameof(ClinicalNotes));
        }
        if (consultation.Prescriptions.Count > 0)
        {
            ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
            fields.Add(nameof(ActivePrescriptionJson));
        }
        return fields;
    }

    [NotMapped]
    public List<PatientPrescriptionItem> ActivePrescription
    {
        get => _activePrescription ??= ReadSheet<PatientPrescriptionItem>(ActivePrescriptionJson, TPVC.Services.Rules.PatientSheetRows.PrescriptionColumn);
        set
        {
            _activePrescription = value ?? new();
            ActivePrescriptionJson = WriteSheet(_activePrescription);
        }
    }

    public void SyncPrescriptionJson()
    {
        ActivePrescriptionJson = WriteSheet(ActivePrescription);
    }

    public string ActiveSuppliesJson { get; set; } = string.Empty;

    [NotMapped]
    private List<PatientSupplyItem>? _activeSupplies;

    [NotMapped]
    public List<PatientSupplyItem> ActiveSupplies
    {
        get => _activeSupplies ??= ReadSheet<PatientSupplyItem>(ActiveSuppliesJson, TPVC.Services.Rules.PatientSheetRows.SuppliesColumn);
        set
        {
            _activeSupplies = value ?? new();
            ActiveSuppliesJson = WriteSheet(_activeSupplies);
        }
    }

    public void SyncSuppliesJson()
    {
        ActiveSuppliesJson = WriteSheet(ActiveSupplies);
    }

    public string ActiveServicesJson { get; set; } = string.Empty;

    [NotMapped]
    private List<PatientServiceItem>? _activeServices;

    [NotMapped]
    public List<PatientServiceItem> ActiveServices
    {
        get => _activeServices ??= ReadSheet<PatientServiceItem>(ActiveServicesJson, TPVC.Services.Rules.PatientSheetRows.ServicesColumn);
        set
        {
            _activeServices = value ?? new();
            ActiveServicesJson = WriteSheet(_activeServices);
        }
    }

    public void SyncServicesJson()
    {
        ActiveServicesJson = WriteSheet(ActiveServices);
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

