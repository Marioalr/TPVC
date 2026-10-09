using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Pantalla de consulta en curso: secciones en el orden de la entrevista y una "cuenta de la consulta"
/// que se llena sola (receta que se surte aquí, insumos y servicios). Todo se cobra al terminar,
/// sin salir al Punto de Venta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    // Las secciones de la nota, sus botones rápidos, la alerta de signos vitales, la consulta anterior
    // (PreviousConsultation) y el cobro (BuildCheckoutCart) viven en Consultation.

    // =========================================================================
    // CUENTA DE LA CONSULTA (el honorario y los cargos viven en Account)
    // =========================================================================

    private void NotifyAccountChanged()
    {
        Consultation.RaisePreviousConsultation();
        Account.RaiseAccountChanged();
        Active.RaiseTotalAmount();
    }

    /// <summary>
    /// Descarta la consulta en curso (paciente equivocado, se retiró...): no guarda la nota ni cobra.
    /// Si ya se había guardado un avance, se elimina del expediente.
    /// </summary>
    [RelayCommand]
    public async Task DiscardActiveConsultationAsync()
    {
        if (!Active.HasActiveConsultation || BlockIfConsultationReadOnly()) return;

        if (!await _dialogs.ConfirmAsync(
                "¿Descartar la consulta en curso?\n\nNo se guardará la nota médica ni se cobrará nada de la cuenta.",
                "Descartar consulta"))
        {
            return;
        }

        var folio = _consultationService?.CurrentConsultation?.Folio;
        if (_inventoryService != null && Session.SelectedPatient != null && !string.IsNullOrWhiteSpace(folio))
        {
            try
            {
                await _inventoryService.DeleteConsultationNoteAsync(Session.SelectedPatient.Id, folio);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo eliminar el avance de la consulta descartada", ex);
            }
        }

        // El avance que se hubiera guardado deja de verse en la ficha (historial, signos, resumen)
        var patient = Session.SelectedPatient;
        if (patient != null && !string.IsNullOrWhiteSpace(folio))
        {
            foreach (var note in patient.Consultations.Where(c => c.Folio == folio).ToList()) patient.Consultations.Remove(note);
            foreach (var note in Session.ConsultationsHistory.Where(c => c.Folio == folio).ToList()) Session.ConsultationsHistory.Remove(note);
            RefreshVitalSignsEvolution(patient);
        }

        await Record.EndConsultationOnRecordAsync(discarded: true);
        _consultationService?.CloseActiveConsultation();
        Consultation.IsCurrentConsultationSaved = false;
        Consultation.AttendingAppointmentId = null;
        SyncActiveConsultation();
        Active.StopConsultationTimer();
        Consultation.ClearConsultationFields();
        Navigation.PatientsPage = Session.SelectedPatient != null ? PageChart : PageHome;
    }
}
