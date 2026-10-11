using System;
using System.Collections.Generic;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Impresión y exportación de las hojas del consultorio: la receta y, desde v1.25.0, la solicitud de laboratorio (en
/// WPF, el servicio de impresión con sus mismas firmas).
/// </summary>
public interface IPrescriptionOutput
{
    /// <param name="consultation">La consulta de la receta: una anterior sale con su fecha y sus signos (desde v1.24.0).</param>
    bool PrintPrescription(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null,
        PatientConsultation? consultation = null);

    string? ExportPrescriptionDocument(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null,
        string? targetFilePath = null,
        PatientConsultation? consultation = null);

    /// <summary>El papel en que saldrá la receta: carta si no cabe en una media carta (la vista previa lo muestra igual).</summary>
    PrescriptionPaper ChoosePaper(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null) => PrescriptionPaper.HalfLetter;

    /// <summary>Las letras de la receta en este equipo.</summary>
    PrescriptionFonts PrescriptionFonts => PrescriptionFonts.Default;

    /// <summary>La solicitud de laboratorio como saldrá (con el papel medido); la usa la vista previa.</summary>
    /// <param name="consultation">La consulta de la orden: una anterior sale con su fecha; null = hoy.</param>
    LabOrderSheet CreateLabOrderSheet(
        PatientRecord patient,
        LabOrder order,
        ClinicProfile profile,
        string? diagnosis,
        PatientConsultation? consultation = null) =>
        LabOrderSheet.Create(patient, order, profile, diagnosis, consultation?.Date ?? DateTime.Now, PrescriptionFonts);

    bool PrintLabOrder(
        PatientRecord patient,
        LabOrder order,
        ClinicProfile profile,
        string? diagnosis,
        PatientConsultation? consultation = null) => false;

    string? ExportLabOrderDocument(
        PatientRecord patient,
        LabOrder order,
        ClinicProfile profile,
        string? diagnosis,
        string? targetFilePath = null,
        PatientConsultation? consultation = null) => null;
}
