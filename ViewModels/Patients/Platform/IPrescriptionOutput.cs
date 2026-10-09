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
}
