using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Services;

namespace TPVC.Models;

/// <summary>Los estudios de un área en la hoja: "Hematología" y sus estudios.</summary>
public sealed record LabOrderSheetGroup(string Area, IReadOnlyList<string> Studies);

/// <summary>
/// La solicitud de estudios de laboratorio tal como sale en papel (desde v1.25.0). Usa el membrete de la receta
/// (escudo, nombre, universidad y cédula; teléfono y domicilio al pie) y su mismo papel: media carta horizontal, o
/// carta vertical si no cabe. Debajo: Paciente · Edad · Fecha, Sexo · Diagnóstico, el título, los estudios por área
/// y las indicaciones para el paciente. La usan la impresión, el HTML/PDF y la vista previa.
/// </summary>
public sealed class LabOrderSheet
{
    public const string Title = "Solicitud de estudios de laboratorio";

    /// <summary>El diagnóstico que se guarda cuando el médico no escribió ninguno: en la hoja queda en blanco.</summary>
    public const string PlaceholderDiagnosis = "Consulta Médica General";

    /// <summary>Membrete, paciente, fecha, pie, letras y papel: los de la receta.</summary>
    public PrescriptionSheet Letterhead { get; init; } = new();

    public string Sex { get; init; } = string.Empty;
    public string Diagnosis { get; init; } = string.Empty;
    public IReadOnlyList<LabOrderSheetGroup> Groups { get; init; } = Array.Empty<LabOrderSheetGroup>();
    public string Indications { get; init; } = string.Empty;

    public int StudyCount => Groups.Sum(g => g.Studies.Count);

    /// <summary>El papel: lo mide la impresión (con WPF). Media carta si nadie lo mide.</summary>
    public PrescriptionPaper Paper
    {
        get => Letterhead.Paper;
        set => Letterhead.Paper = value;
    }

    public double PaperHeight => Letterhead.PaperHeight;
    public double PaperHeightInches => Letterhead.PaperHeightInches;

    public static LabOrderSheet Create(
        PatientRecord patient,
        LabOrder order,
        ClinicProfile? profile,
        string? diagnosis,
        DateTime date,
        PrescriptionFonts? fonts = null)
    {
        var groups = order.Studies
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Area) ? LabCatalog.OtherArea : s.Area.Trim())
            .OrderBy(g => LabCatalog.AreaOrder(g.Key))
            .Select(g => new LabOrderSheetGroup(g.Key, g.Select(s => s.Name.Trim()).ToList()))
            .ToList();
        var dx = (diagnosis ?? string.Empty).Trim();

        return new LabOrderSheet
        {
            Letterhead = PrescriptionSheet.Create(patient, null, profile, null, date, fonts),
            Sex = SexOf(patient.Gender),
            Diagnosis = dx == PlaceholderDiagnosis ? string.Empty : dx,
            Groups = groups,
            Indications = (order.Indications ?? string.Empty).Trim()
        };
    }

    /// <summary>"Femenino" o "Masculino"; en blanco si no se especificó.</summary>
    public static string SexOf(string? gender)
    {
        var g = (gender ?? string.Empty).Trim();
        return g.Length == 0 || g.Equals("No especificado", StringComparison.OrdinalIgnoreCase) ? string.Empty : g;
    }
}
