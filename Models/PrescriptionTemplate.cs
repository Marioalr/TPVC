using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace TPVC.Models;

public static class PrescriptionTemplateKinds
{
    /// <summary>Indicaciones de toma de un medicamento (hoja POSOLOGÍA).</summary>
    public const string Posology = "Posologia";

    /// <summary>Bloques de medidas generales / datos de alarma (hoja M. GRALES).</summary>
    public const string GeneralMeasures = "MedidasGenerales";
}

/// <summary>
/// Texto reutilizable para recetas: posologías frecuentes y medidas generales del consultorio.
/// Se importan del Excel de la doctora y se ofrecen como plantillas al recetar.
/// </summary>
public class PrescriptionTemplate : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string Kind { get; set; } = PrescriptionTemplateKinds.Posology;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public override bool Equals(object? obj) => obj is PrescriptionTemplate other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public override string ToString() => Title;
}

/// <summary>
/// Regla de dosificación pediátrica por peso (hoja DOSIS PEDIA):
/// mililitros por dosis = peso (kg) × <see cref="MlPerKg"/>; gotas = ml × <see cref="DropsPerMl"/>.
/// Ej. Paracetamol 3.2 g/100 ml a 15 mg/kg/dosis → 15/32 = 0.46875 ml/kg.
/// </summary>
public class PediatricDoseRule : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string Medication { get; set; } = string.Empty;
    public string DoseText { get; set; } = string.Empty;
    public double? MlPerKg { get; set; }
    public int? DropsPerMl { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    [NotMapped]
    public bool CanCalculate => MlPerKg is > 0;

    public double? CalculateMl(double weightKg) =>
        CanCalculate && weightKg > 0 ? Math.Round(MlPerKg!.Value * weightKg, 1) : null;

    public double? CalculateDrops(double weightKg)
    {
        var ml = CalculateMl(weightKg);
        return ml.HasValue && DropsPerMl is > 0 ? Math.Round(ml.Value * DropsPerMl.Value, 0) : null;
    }

    [NotMapped]
    public string DisplayName => string.IsNullOrWhiteSpace(DoseText) ? Medication : $"{Medication} • {DoseText}";

    public override bool Equals(object? obj) => obj is PediatricDoseRule other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public override string ToString() => DisplayName;
}
