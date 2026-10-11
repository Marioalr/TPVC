using System.Collections.Generic;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>Impresión y exportación de la receta (en WPF, el servicio de impresión con sus mismas firmas).</summary>
public interface IPrescriptionOutput
{
    bool PrintPrescription(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null);

    string? ExportPrescriptionDocument(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null,
        string? targetFilePath = null);

    /// <summary>El papel en que saldrá la receta: carta si no cabe en una media carta (la vista previa lo muestra igual).</summary>
    PrescriptionPaper ChoosePaper(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null) => PrescriptionPaper.HalfLetter;

    /// <summary>Las letras de la receta en este equipo.</summary>
    PrescriptionFonts PrescriptionFonts => PrescriptionFonts.Default;
}
