using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// El expediente durante la consulta en curso. La receta empieza vacía: la última del paciente no
/// se arrastra (antes se imprimía y guardaba en la consulta nueva si no se borraba a mano), pero
/// se puede repetir con un clic. Si la consulta termina sin receta, la última emitida sigue siendo
/// la del expediente; si se descarta, el expediente vuelve a como estaba antes de empezarla.
/// </summary>
public partial class MedicalRecordsViewModel
{
    public bool CanRepeatPreviousPrescription => Active.HasActiveConsultation && Record.PendingPreviousPrescription.Count > 0;

    public string PreviousPrescriptionSummary => string.Join(" · ", Record.PendingPreviousPrescription.Select(p => p.MedicineName));

    /// <summary>"Última receta" de la ficha: durante la consulta, la emitida antes de ella.</summary>
    public IReadOnlyList<PatientPrescriptionItem> ChartPrescription =>
        IsConsultationForSelectedPatient && Record.PreviousPrescriptionKnown
            ? Record.PreviousPrescription
            : (IReadOnlyList<PatientPrescriptionItem>?)Session.SelectedPatient?.ActivePrescription ?? Array.Empty<PatientPrescriptionItem>();

    // Las indicaciones de la receta (vista previa, impresión y PDF) son de Preview
    partial void OnConsultationTreatmentPlanChanged(string value) => Preview.RaisePrescriptionIndications();

    private void NotifyPrescriptionStateChanged()
    {
        Preview.RaisePrescriptionIndications();
        OnPropertyChanged(nameof(CanRepeatPreviousPrescription));
        OnPropertyChanged(nameof(PreviousPrescriptionSummary));
        OnPropertyChanged(nameof(ChartPrescription));
        OnPropertyChanged(nameof(HasActivePrescription));
    }

    /// <summary>
    /// Copia a esta consulta los medicamentos de la receta anterior que falten. Se revisan otra vez
    /// las alergias (pudieron registrarse después) y las existencias para "Se surte aquí".
    /// </summary>
    [RelayCommand]
    public async Task RepeatPreviousPrescriptionAsync()
    {
        if (BlockIfConsultationReadOnly()) return;
        if (!Active.HasActiveConsultation || Session.SelectedPatient == null) return;

        var skipped = new List<string>();
        foreach (var item in Record.PendingPreviousPrescription)
        {
            var product = Catalog.FindCatalogProduct(item.MedicineName);
            if (InventoryService.CheckMedicationAllergy(Session.SelectedPatient, product, item.MedicineName).HasConflict)
            {
                skipped.Add(item.MedicineName);
                continue;
            }
            await AddPrescriptionItemInternalAsync(product, item.MedicineName, item.Dosage, Math.Max(1, item.RecommendedQuantity));
        }

        NotifyPrescriptionStateChanged();
        if (skipped.Count > 0)
        {
            Info.InfoModalMessage = $"No se repitió {string.Join(", ", skipped)}: coincide con las alergias registradas del paciente. Si de todos modos debe recetarse, agréguelo a mano para confirmar la alerta.";
            Info.IsInfoModalVisible = true;
        }
    }
}
